using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace ManagerPlatform.Services;

/// <summary>解析出的章节：文本 + 章节路径（引用展示）+ PDF 页码（1 起）。</summary>
public sealed record ParsedSection(string Text, string? HeadingPath, int? PageNo);

/// <summary>解析结果：章节集合 + PDF 总页数（非 PDF 为 0）。</summary>
public sealed record ParsedDocument(IReadOnlyList<ParsedSection> Sections, int PageCount = 0);

/// <summary>
/// 文档文本解析器：按扩展名分派（txt / md / docx / PDF 文本层）。
/// 不返回任何格式信息中的图片/排版，扫描版 PDF 无文本层时返回空章节，由调用方判失败。
/// </summary>
public interface IDocumentParser
{
    bool CanParse(string extension);

    Task<ParsedDocument> ParseAsync(Stream content, CancellationToken ct = default);
}

/// <summary>纯文本（UTF-8，兼容 BOM 与其他编码探测）。</summary>
public sealed class PlainTextParser : IDocumentParser
{
    public bool CanParse(string extension) => extension is ".txt";

    public async Task<ParsedDocument> ParseAsync(Stream content, CancellationToken ct = default)
    {
        var text = await ReadTextAsync(content, ct);
        return new ParsedDocument([new ParsedSection(text, null, null)]);
    }

    internal static async Task<string> ReadTextAsync(Stream content, CancellationToken ct)
    {
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }
}

/// <summary>Markdown：按 # 标题层级切章节，HeadingPath 形如 "第三章 &gt; 3.2 接口设计"。</summary>
public sealed class MarkdownParser : IDocumentParser
{
    public bool CanParse(string extension) => extension is ".md" or ".markdown";

    public async Task<ParsedDocument> ParseAsync(Stream content, CancellationToken ct = default)
    {
        var text = await PlainTextParser.ReadTextAsync(content, ct);
        var sections = new List<ParsedSection>();
        var headingStack = new List<(int Level, string Title)>();
        var body = new StringBuilder();
        string? currentPath = null;

        void Flush()
        {
            var value = body.ToString().Trim();
            if (value.Length > 0) sections.Add(new ParsedSection(value, currentPath, null));
            body.Clear();
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (TryParseHeading(rawLine, out var level, out var title))
            {
                Flush();
                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                    headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add((level, title));
                currentPath = string.Join(" > ", headingStack.Select(h => h.Title));
            }
            body.AppendLine(rawLine);
        }
        Flush();
        return new ParsedDocument(sections);
    }

    /// <summary>识别 ATX 标题（1~6 个 # + 空格/行尾）。</summary>
    private static bool TryParseHeading(string line, out int level, out string title)
    {
        level = 0;
        title = string.Empty;
        if (!line.StartsWith('#')) return false;

        while (level < line.Length && line[level] == '#') level++;
        if (level is < 1 or > 6) return false;
        if (level < line.Length && line[level] != ' ') return false;

        title = line[level..].Trim().TrimEnd('#').Trim();
        return title.Length > 0;
    }
}

/// <summary>DOCX（Open XML）：按顶层段落与表格抽取纯文本，表格行以 " | " 连接单元格。</summary>
public sealed class DocxParser : IDocumentParser
{
    public bool CanParse(string extension) => extension is ".docx";

    public async Task<ParsedDocument> ParseAsync(Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        using var doc = WordprocessingDocument.Open(buffer, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return new ParsedDocument([]);

        var blocks = new List<string>();
        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    AddBlock(blocks, paragraph.InnerText);
                    break;
                case Table table:
                    foreach (var row in table.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>()
                            .Select(c => c.InnerText.Trim())
                            .Where(t => t.Length > 0);
                        var line = string.Join(" | ", cells);
                        if (line.Length > 0) blocks.Add(line);
                    }
                    break;
            }
        }

        if (blocks.Count == 0) return new ParsedDocument([]);
        return new ParsedDocument([new ParsedSection(string.Join("\n\n", blocks), null, null)]);
    }

    private static void AddBlock(List<string> blocks, string? text)
    {
        var value = text?.Trim();
        if (value is { Length: > 0 }) blocks.Add(value);
    }
}

/// <summary>PDF：逐页抽取文本层（PdfPig），无文本层的页跳过；总页数超上限直接失败。</summary>
public sealed class PdfParser : IDocumentParser
{
    /// <summary>PDF 页数上限：防止单文件拖死 Worker（见技术方案 §7.5）。</summary>
    public const int MaxPages = 200;

    public bool CanParse(string extension) => extension is ".pdf";

    public async Task<ParsedDocument> ParseAsync(Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);

        using var pdf = PdfDocument.Open(buffer.ToArray());
        if (pdf.NumberOfPages > MaxPages)
            throw new InvalidOperationException($"PDF 共 {pdf.NumberOfPages} 页，超过上限 {MaxPages} 页");

        var sections = new List<ParsedSection>();
        for (var pageNo = 1; pageNo <= pdf.NumberOfPages; pageNo++)
        {
            ct.ThrowIfCancellationRequested();
            var text = Normalize(pdf.GetPage(pageNo).Text);
            if (text.Length > 0) sections.Add(new ParsedSection(text, null, pageNo));
        }
        return new ParsedDocument(sections, pdf.NumberOfPages);
    }

    /// <summary>PDF 文本层换行碎片较多：压缩连续空行/空格，保留段落分隔。</summary>
    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .ToList();
        return string.Join("\n", lines).Trim();
    }
}
