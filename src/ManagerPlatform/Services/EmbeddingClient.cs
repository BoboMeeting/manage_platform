using System.Net.Http.Headers;
using System.Net.Http.Json;
using ManagerPlatform.Options;
using Microsoft.Extensions.Options;
using Pgvector;

namespace ManagerPlatform.Services;

/// <summary>embedding 客户端：批量调用 OpenAI 兼容 /embeddings 接口。</summary>
public interface IEmbeddingClient
{
    /// <summary>批量生成向量；返回顺序与入参一致，维度不符直接抛异常。</summary>
    Task<IReadOnlyList<Vector>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>
/// OpenAI 兼容 embedding 实现（SiliconFlow / LiteLLM 等）：批量、超时、重试、维度校验。
/// 维度校验失败即抛异常、文档判失败，避免写入与 vector(1024) 列不匹配的脏数据。
/// </summary>
public sealed class OpenAiEmbeddingClient : IEmbeddingClient
{
    private const int MaxAttempts = 3;

    private readonly RagOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiEmbeddingClient> _logger;

    public OpenAiEmbeddingClient(
        HttpClient http, IOptions<RagOptions> options, ILogger<OpenAiEmbeddingClient> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.EmbeddingTimeoutSeconds));
    }

    public async Task<IReadOnlyList<Vector>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<Vector>();

        var batchSize = Math.Max(1, _options.EmbeddingBatchSize);
        var result = new List<Vector>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += batchSize)
        {
            var slice = texts.Skip(offset).Take(batchSize).ToList();
            result.AddRange(await EmbedBatchAsync(slice, ct));
        }
        return result;
    }

    private async Task<IReadOnlyList<Vector>> EmbedBatchAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await CallAsync(texts, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                _logger.LogWarning(ex, "embedding 调用失败（第 {Attempt}/{Max} 次）", attempt, MaxAttempts);
                if (attempt < MaxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }
        throw new InvalidOperationException($"embedding 调用失败（已重试 {MaxAttempts} 次）：{lastError?.Message}", lastError);
    }

    private async Task<IReadOnlyList<Vector>> CallAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        var url = _options.EmbeddingBaseUrl.TrimEnd('/') + "/embeddings";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new EmbeddingRequest(_options.EmbeddingModel, texts)),
        };
        // 本地 TEI 无鉴权：ApiKey 留空时不发 Authorization，避免服务端因空 Bearer 拒绝
        if (!string.IsNullOrWhiteSpace(_options.EmbeddingApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.EmbeddingApiKey);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"embedding 接口返回 HTTP {(int)response.StatusCode}：{Truncate(body)}");
        }

        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("embedding 接口响应为空");
        if (payload.Data is not { Count: > 0 })
            throw new InvalidOperationException("embedding 接口未返回向量");
        if (payload.Data.Count != texts.Count)
            throw new InvalidOperationException(
                $"embedding 返回条数 {payload.Data.Count} 与请求 {texts.Count} 不一致");

        var vectors = new List<Vector>(payload.Data.Count);
        foreach (var item in payload.Data.OrderBy(d => d.Index))
        {
            var values = item.Embedding;
            if (values is null || values.Length == 0)
                throw new InvalidOperationException("embedding 返回空向量");
            if (values.Length != _options.EmbeddingDim)
                throw new InvalidOperationException(
                    $"embedding 维度不一致：模型返回 {values.Length}，配置为 {_options.EmbeddingDim}");
            vectors.Add(new Vector(values));
        }
        return vectors;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed record EmbeddingRequest(string Model, IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse(List<EmbeddingItem>? Data);

    private sealed record EmbeddingItem(int Index, float[]? Embedding);
}
