namespace ManagerPlatform.Options;

/// <summary>
/// OCR 配置（appsettings.json "Ocr" 节，可用环境变量 Ocr__ApiKey 等覆盖）。
/// 仅用于扫描版 PDF 兜底：PdfPig 平均每页有效字符低于阈值时整篇转 Markdown。
/// </summary>
public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    /// <summary>关闭后扫描件仍按一期判 Failed（错误信息提示 OCR 未启用）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>OCR 服务地址（OpenAI 兼容）。</summary>
    public string BaseUrl { get; set; } = "https://api.siliconflow.cn/v1";

    /// <summary>API Key：仅触发 OCR 时校验，不在启动期强校验。</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "PaddlePaddle/PaddleOCR-VL-1.5";

    /// <summary>单次 OCR 请求超时（秒）。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>base64 膨胀后请求体过大，超限直接给明确失败原因。</summary>
    public int MaxFileSizeMb { get; set; } = 10;

    /// <summary>平均每页有效字符数低于此值视为扫描件。</summary>
    public int MinCharsPerPage { get; set; } = 20;
}
