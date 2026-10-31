namespace ManagerPlatform.Services;

/// <summary>切分后的文本片段（Seq 为文档内全局序号，用于 kb_chunks 的 (document_id, seq) 唯一索引）。</summary>
public sealed record TextChunk(int Seq, string Content, string? HeadingPath, int? PageNo)
{
    public int CharCount => Content.Length;
}

/// <summary>
/// 文本切分器：段落优先（换行边界）→ 句末标点 → 字符窗上限，相邻片段保留 overlap 字符重叠。
/// 中文按字符计数，不按空格切词（中英文混排同样适用）。
/// </summary>
public static class TextChunker
{
    /// <summary>片段长度下限：过小的 chunkSize 会导致切出的片段无检索意义。</summary>
    public const int MinChunkSize = 50;

    /// <summary>句末标点（中英文）。</summary>
    private static readonly char[] SentenceEnds = ['。', '！', '？', '；', '：', '.', '!', '?', ';', ':'];

    public static IReadOnlyList<TextChunk> Split(
        IReadOnlyList<ParsedSection> sections, int chunkSize, int overlap)
    {
        if (chunkSize < MinChunkSize) chunkSize = MinChunkSize;
        overlap = Math.Clamp(overlap, 0, chunkSize - 1);

        var chunks = new List<TextChunk>();
        var seq = 0;
        foreach (var section in sections)
        {
            foreach (var content in SplitSection(section.Text, chunkSize, overlap))
                chunks.Add(new TextChunk(seq++, content, section.HeadingPath, section.PageNo));
        }
        return chunks;
    }

    /// <summary>滑窗切分：窗口上限 chunkSize；能落在段落/句子边界时优先回退，步进 = chunkSize - overlap。</summary>
    private static IEnumerable<string> SplitSection(string text, int chunkSize, int overlap)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var start = 0;
        while (start < text.Length)
        {
            while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
            if (start >= text.Length) break;

            var hardEnd = Math.Min(start + chunkSize, text.Length);
            var end = hardEnd;
            if (hardEnd < text.Length)
            {
                // 回退区间下限取窗口一半：避免为了对齐边界而切出过短片段
                var minEnd = start + chunkSize / 2;
                end = FindBoundary(text, hardEnd, minEnd, static c => c == '\n');
                if (end < 0) end = FindBoundary(text, hardEnd, minEnd, c => SentenceEnds.Contains(c));
                if (end < 0) end = hardEnd;
            }

            var content = text[start..end].Trim();
            if (content.Length > 0) yield return content;
            if (end >= text.Length) break;

            start = Math.Max(end - overlap, start + 1);
        }
    }

    /// <summary>在 (min, from] 区间内向前查找最近的边界位置；未找到返回 -1。</summary>
    private static int FindBoundary(string text, int from, int min, Func<char, bool> isBoundary)
    {
        for (var i = from; i > min; i--)
        {
            if (isBoundary(text[i - 1])) return i;
        }
        return -1;
    }
}
