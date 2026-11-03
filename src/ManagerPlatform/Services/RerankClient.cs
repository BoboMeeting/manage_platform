using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManagerPlatform.Options;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.Services;

/// <summary>rerank 客户端：对召回候选做二次精排，返回按相关度降序的 (候选下标, 分数)。</summary>
public interface IRerankClient
{
    /// <summary>
    /// 精排指定文档列表；失败/未配置 Key/未启用时返回空集合，由调用方回退原始顺序。
    /// </summary>
    Task<IReadOnlyList<(int Index, double Score)>> RerankAsync(
        string query, IReadOnlyList<string> documents, int topN, CancellationToken ct = default);
}

/// <summary>
/// 硅基流动 /v1/rerank 实现（Cohere 兼容）。
/// 本类不抛业务异常：仅在调用取消时抛出，其余错误统一告警并返回空结果，避免检索链路因精排失败而中断。
/// </summary>
public sealed class RerankClient : IRerankClient
{
    private readonly RagOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<RerankClient> _logger;

    public RerankClient(HttpClient http, IOptions<RagOptions> options, ILogger<RerankClient> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.RerankTimeoutSeconds));
    }

    public async Task<IReadOnlyList<(int Index, double Score)>> RerankAsync(
        string query, IReadOnlyList<string> documents, int topN, CancellationToken ct = default)
    {
        if (!_options.EnableRerank || documents.Count == 0) return [];

        if (string.IsNullOrWhiteSpace(_options.RerankApiKey))
        {
            _logger.LogWarning("已启用 rerank 但未配置 Rag:RerankApiKey，本次按向量检索原始顺序返回");
            return [];
        }

        try
        {
            var url = _options.RerankBaseUrl.TrimEnd('/') + "/rerank";
            var request = new RerankRequest(
                _options.RerankModel,
                query,
                documents,
                Math.Clamp(topN, 1, documents.Count),
                ReturnDocuments: false);

            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                // 字段名由 JsonPropertyName 显式指定，避免 Web 默认 camelCase 与接口下划线命名不一致
                Content = JsonContent.Create(request, options: JsonSerializerOptions.Web),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.RerankApiKey);

            using var response = await _http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"rerank 接口返回 HTTP {(int)response.StatusCode}：{Truncate(body)}");
            }

            var payload = await response.Content.ReadFromJsonAsync<RerankResponse>(cancellationToken: ct);
            if (payload?.Results is not { Count: > 0 })
            {
                _logger.LogWarning("rerank 接口返回空结果，回退向量检索原始顺序");
                return [];
            }

            return payload.Results
                .Where(r => r.Index >= 0 && r.Index < documents.Count)
                .Select(r => (r.Index, r.RelevanceScore))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 超时 / 网络 / 解析 / 鉴权失败均降级，不影响检索可用性
            _logger.LogWarning(ex, "rerank 调用失败，回退向量检索原始顺序");
            return [];
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed record RerankRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents,
        [property: JsonPropertyName("top_n")] int TopN,
        [property: JsonPropertyName("return_documents")] bool ReturnDocuments);

    private sealed record RerankResponse(
        [property: JsonPropertyName("results")] List<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("relevance_score")] double RelevanceScore);
}
