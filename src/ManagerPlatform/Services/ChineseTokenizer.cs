using System.Text;

namespace ManagerPlatform.Services;

/// <summary>
/// 中文 bigram 分词器（自研，用于 P4 hybrid 稀疏检索）。
/// 规则：
///   - CJK 连续段：长度 1 输出单字；长度 ≥2 输出全部 2-gram（如 "知识库" → "知识 识库"）
///   - 拉丁字母/数字：按非字母数字分隔，转小写整词（如 "ERR_5001" → "err 5001"）
///   - 标点与空白：直接丢弃
/// 输出为空格分隔的 token 串，供 NpgsqlTsVector.Parse 与查询侧共用。
/// </summary>
public static class ChineseTokenizer
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>将输入文本转换为空格分隔的 token 序列。</summary>
    public static string ToTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var sb = new StringBuilder(text.Length * 2);
        var hasToken = false;

        var span = text.AsSpan();
        int i = 0;
        while (i < span.Length)
        {
            char c = span[i];

            if (IsCjk(c))
            {
                // 收集连续 CJK 段
                int start = i;
                while (i < span.Length && IsCjk(span[i])) i++;
                int len = i - start;

                if (len == 1)
                {
                    AppendToken(sb, span[start..1], ref hasToken);
                }
                else
                {
                    // 全部 2-gram
                    for (int j = 0; j < len - 1; j++)
                    {
                        int gramStart = start + j;
                        int gramEnd = gramStart + 2;
                        AppendToken(sb, span[gramStart..gramEnd], ref hasToken);
                    }
                }
            }
            else if (IsLetterOrDigit(c))
            {
                // 收集连续字母/数字段，整体转小写
                int start = i;
                while (i < span.Length && IsLetterOrDigit(span[i])) i++;
                var segment = span[start..i];

                if (IsAllDigits(segment))
                {
                    // 纯数字：直接保留
                    AppendToken(sb, segment, ref hasToken);
                }
                else
                {
                    // 混合字母（可能含数字），整体转小写
                    AppendLowerToken(sb, segment, ref hasToken);
                }
            }
            else
            {
                // 标点、空白、符号 → 跳过
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>用于查询侧：对 token 去重（保持顺序），避免重复 gram 稀释查询权重。</summary>
    public static IReadOnlyList<string> QueryTokens(string? text)
    {
        var tokens = ToTokens(text);
        if (string.IsNullOrEmpty(tokens)) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var t in tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(t)) result.Add(t);
        }
        return result;
    }

    private static void AppendToken(StringBuilder sb, ReadOnlySpan<char> token, ref bool hasToken)
    {
        if (hasToken) sb.Append(' ');
        sb.Append(token);
        hasToken = true;
    }

    private static void AppendLowerToken(StringBuilder sb, ReadOnlySpan<char> token, ref bool hasToken)
    {
        if (hasToken) sb.Append(' ');
        foreach (char c in token)
        {
            sb.Append(char.ToLowerInvariant(c));
        }
        hasToken = true;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (!char.IsDigit(c)) return false;
        }
        return span.Length > 0;
    }

    /// <summary>CJK Unified Ideographs + 扩展 A + 兼容表意文字 + 常用标点（暂按范围粗判）。</summary>
    private static bool IsCjk(char c)
    {
        // CJK Unified Ideographs: U+4E00 ~ U+9FFF
        // CJK Unified Ideographs Extension A: U+3400 ~ U+4DBF
        // CJK Compatibility Ideographs: U+F900 ~ U+FAFF
        // CJK Unified Ideographs Extension B-F 超出 char 范围（需 surrogate pair），实际文档极少出现
        return c is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';
    }

    private static bool IsLetterOrDigit(char c) => char.IsLetterOrDigit(c);

    /// <summary>计算字符串的 sha256 小写 hex（供 ContentHash 使用）。</summary>
    public static string Sha256Hex(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = Utf8NoBom.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
