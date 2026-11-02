using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManagerPlatform.Options;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.Services;

/// <summary>OCR 客户端：把扫描版 PDF 整篇转为 Markdown。</summary>
public interface IOcrClient
{
    /// <summary>识别 PDF 并返回 Markdown；失败抛异常（由 Worker 记录 ErrorMessage）。</summary>
    Task<string> RecognizePdfAsync(Stream pdf, CancellationToken ct = default);
}

/// <summary>
/// 硅基流动 PaddleOCR-VL 实现（OpenAI 兼容 /chat/completions，PDF 以 data URL 传入）。
/// 超时、重试与大小上限统一在此处理；仅在 PdfParser 判定为扫描件时调用。
/// </summary>
public sealed class OcrClient : IOcrClient
{
    /// <summary>首次调用 + 2 次重试。</summary>
    private const int MaxAttempts = 3;

    private static readonly string Prompt = "<image>\n<|grounding|>Convert the document to markdown.";

    private readonly OcrOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<OcrClient> _logger;

    public OcrClient(HttpClient http, IOptions<OcrOptions> options, ILogger<OcrClient> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(10, _options.TimeoutSeconds));
    }

    public async Task<string> RecognizePdfAsync(Stream pdf, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException("OCR 输入为空");

        var maxBytes = (long)Math.Max(1, _options.MaxFileSizeMb) * 1024 * 1024;
        if (bytes.Length > maxBytes)
            throw new InvalidOperationException(
                $"PDF 大小 {bytes.Length / 1024 / 1024}MB 超过 OCR 上限 {_options.MaxFileSizeMb}MB（可调整 Ocr:MaxFileSizeMb）");

        var dataUrl = "data:application/pdf;base64," + Convert.ToBase64String(bytes);

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await CallAsync(dataUrl, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "OCR 调用失败（第 {Attempt}/{Max} 次）", attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
        throw new InvalidOperationException(
            $"OCR 调用失败（已重试 {MaxAttempts - 1} 次）：{Truncate(lastError?.Message ?? "未知错误")}", lastError);
    }

    private async Task<string> CallAsync(string dataUrl, CancellationToken ct)
    {
        var url = _options.BaseUrl.TrimEnd('/') + "/chat/completions";
        var request = new OcrRequest(
            _options.Model,
            [
                new OcrMessage("user",
                [
                    new OcrContent("image_url", ImageUrl: new OcrImageUrl(dataUrl)),
                    new OcrContent("text", Text: Prompt),
                ]),
            ],
            Temperature: 0,
            Stream: false);

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // 显式使用 Web 默认序列化（camelCase），与 OpenAI 兼容接口字段名一致
            Content = JsonContent.Create(request, options: JsonSerializerOptions.Web),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"OCR 接口返回 HTTP {(int)response.StatusCode}：{Truncate(body)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<OcrResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("OCR 接口响应为空");
        var content = payload.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("OCR 接口未返回文本内容");
        return content;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed record OcrRequest(
        string Model,
        IReadOnlyList<OcrMessage> Messages,
        int Temperature,
        bool Stream);

    private sealed record OcrMessage(string Role, IReadOnlyList<OcrContent> Content);

    private sealed record OcrContent(
        string Type,
        [property: JsonPropertyName("text")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Text = null,
        [property: JsonPropertyName("image_url")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OcrImageUrl? ImageUrl = null);

    private sealed record OcrImageUrl(
        [property: JsonPropertyName("url")] string Url);

    private sealed record OcrResponse(List<OcrChoice>? Choices);

    private sealed record OcrChoice(OcrResponseMessage? Message);

    private sealed record OcrResponseMessage(string? Content);
}
