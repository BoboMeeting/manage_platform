using System.Text;
using System.Text.RegularExpressions;

namespace ManagerPlatform.Services;

/// <summary>
/// 字幕归一化：SRT / ASS → WebVTT（入戏方案：上传字幕统一转 VTT 存储）。
/// 解析仅覆盖常用格式要素（时间码 + 文本行 + 常见标签剥离），满足台词窗按 cue 高亮需求。
/// </summary>
public static partial class SubtitleConverter
{
    /// <summary>
    /// 按原始文件扩展名归一化为 VTT，返回（vtt 内容, cue 数）。
    /// vtt 本身透传（重新规范化头部与时间戳）。
    /// </summary>
    public static (string Vtt, int CueCount) ToVtt(string fileName, string content)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".srt" => SrtToVtt(content),
            ".ass" or ".ssa" => AssToVtt(content),
            ".vtt" => NormalizeVtt(content),
            _ => throw new NotSupportedException($"不支持的字幕格式：{ext}（支持 srt/vtt/ass）"),
        };
    }

    // ==================== SRT → VTT ====================

    public static (string Vtt, int CueCount) SrtToVtt(string srt)
    {
        var sb = new StringBuilder("WEBVTT\n");
        var blocks = srt.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // 找到时间码行（块首可能是序号行）
            var timingIdx = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timingIdx < 0) continue;

            var timing = lines[timingIdx].Trim();
            var m = TimingRegex().Match(timing);
            if (!m.Success) continue;

            var text = string.Join('\n', lines[(timingIdx + 1)..])
                .Trim();
            if (text.Length == 0) continue;

            text = StripTags(text);
            sb.Append('\n')
                .Append(SrtTimeToVtt(m.Groups[1].Value)).Append(" --> ").AppendLine(SrtTimeToVtt(m.Groups[2].Value))
                .AppendLine(text);
            count++;
        }
        return (sb.ToString(), count);
    }

    /// <summary>SRT 时间码 00:00:01,000 → VTT 00:00:01.000</summary>
    private static string SrtTimeToVtt(string t) => t.Replace(',', '.');

    // ==================== VTT 规范化 ====================

    public static (string Vtt, int CueCount) NormalizeVtt(string vtt)
    {
        // 透传并统计 cue 数；保证有 WEBVTT 头
        var normalized = vtt.Replace("\r\n", "\n").TrimStart();
        if (!normalized.StartsWith("WEBVTT"))
            normalized = "WEBVTT\n" + normalized;
        var count = normalized.Split('\n').Count(l => l.Contains("-->"));
        return (normalized, count);
    }

    // ==================== ASS/SSA → VTT ====================

    public static (string Vtt, int CueCount) AssToVtt(string ass)
    {
        var sb = new StringBuilder("WEBVTT\n");
        var count = 0;
        var inEvents = false;

        foreach (var raw in ass.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents || !line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Dialogue: layer,start,end,style,name,marginL,marginR,marginV,effect,text
            var body = line["Dialogue:".Length..];
            var fields = body.Split(',', 10);
            if (fields.Length < 10) continue;

            if (!TryParseAssTime(fields[1].Trim(), out var start)
                || !TryParseAssTime(fields[2].Trim(), out var end))
                continue;

            // 文本：\N / \n 换行，剥离 {} 覆盖标签（{\i1} 等）
            var text = StripTags(fields[9].Replace("\\N", "\n").Replace("\\n", "\n")).Trim();
            if (text.Length == 0) continue;

            sb.Append('\n')
                .Append(start).Append(" --> ").AppendLine(end)
                .AppendLine(text);
            count++;
        }
        return (sb.ToString(), count);
    }

    /// <summary>ASS 时间码 H:MM:SS.cc（百分之一秒）→ VTT HH:MM:SS.mmm</summary>
    private static bool TryParseAssTime(string t, out string vtt)
    {
        vtt = string.Empty;
        var m = AssTimeRegex().Match(t);
        if (!m.Success) return false;
        var cs = int.Parse(m.Groups[4].Value); // 百分之一秒
        vtt = $"{int.Parse(m.Groups[1].Value):00}:{m.Groups[2].Value}:{m.Groups[3].Value}.{cs * 10:000}";
        return true;
    }

    /// <summary>剥离 HTML 标签与 ASS 覆盖标签</summary>
    private static string StripTags(string text) =>
        HtmlTagRegex().Replace(AssOverrideRegex().Replace(text, ""), "").Trim();

    [GeneratedRegex(@"(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3})")]
    private static partial Regex TimingRegex();

    [GeneratedRegex(@"(\d{1,2}):(\d{2}):(\d{2})[.,](\d{1,2})")]
    private static partial Regex AssTimeRegex();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssOverrideRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
