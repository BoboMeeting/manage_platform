using System.Diagnostics;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Stores;
using Microsoft.Extensions.Options;
using Pgvector;

namespace ManagerPlatform.Services;

/// <summary>检索调用方标识（写入 retrieval_logs.Caller，便于按来源做评测与审计）。</summary>
public static class RetrievalCallers
{
    public const string Mcp = "mcp";
    public const string AdminDebug = "admin-debug";
}

/// <summary>检索结果：命中片段（含来源信息）+ 端到端耗时（embedding + 向量检索）。</summary>
public sealed record KnowledgeSearchResult(IReadOnlyList<KbSearchHit> Hits, int ElapsedMs);

/// <summary>
/// 检索服务：query → embedding → pgvector 余弦 top-k → 组装引用信息 → 写检索日志。
/// 只检索启用中的知识库；knowledgeBaseIds 为空表示检索全部启用库。
/// </summary>
public interface IRetrievalService
{
    Task<KnowledgeSearchResult> SearchAsync(
        string query, IReadOnlyList<string>? knowledgeBaseIds, int? topK,
        string caller, CancellationToken ct = default);
}

/// <summary>
/// query embedding 的进程内 LRU 缓存（容量 1024）：重复问句不再调用 embedding API。
/// 键包含模型名，避免换模型后命中旧向量；本类线程安全（单例）。
/// </summary>
public sealed class QueryEmbeddingCache
{
    public const int Capacity = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();

    public Vector? Get(string key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Vector;
        }
    }

    public void Set(string key, Vector vector)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _map.Remove(key);
            }

            var node = _lru.AddFirst(new Entry(key, vector));
            _map[key] = node;

            if (_map.Count <= Capacity) return;
            var last = _lru.Last;
            if (last is null) return;
            _lru.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }

    private sealed record Entry(string Key, Vector Vector);
}

public sealed class RetrievalService(
    IKnowledgeBaseStore store,
    IEmbeddingClient embedding,
    QueryEmbeddingCache cache,
    IOptions<RagOptions> options,
    ILogger<RetrievalService> logger) : IRetrievalService
{
    private readonly RagOptions _options = options.Value;
    private readonly ILogger<RetrievalService> _logger = logger;

    public async Task<KnowledgeSearchResult> SearchAsync(
        string query, IReadOnlyList<string>? knowledgeBaseIds, int? topK,
        string caller, CancellationToken ct = default)
    {
        var normalized = query?.Trim() ?? string.Empty;
        var k = Math.Clamp(topK ?? _options.DefaultTopK, 1, Math.Max(1, _options.MaxTopK));
        var kbIds = knowledgeBaseIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
            return new KnowledgeSearchResult(Array.Empty<KbSearchHit>(), 0);

        var sw = Stopwatch.StartNew();
        var vector = await GetQueryVectorAsync(normalized, ct);
        var hits = await store.SearchChunksAsync(vector, kbIds, k, _options.MinScore, ct);
        sw.Stop();

        var elapsedMs = (int)sw.ElapsedMilliseconds;
        await WriteLogAsync(normalized, kbIds, k, hits, elapsedMs, caller, ct);

        _logger.LogInformation(
            "知识库检索完成：{Hits} 条命中，top-k={TopK}，耗时 {Elapsed}ms，调用方={Caller}",
            hits.Count, k, elapsedMs, Truncate(caller));

        return new KnowledgeSearchResult(hits, elapsedMs);
    }

    private async Task<Vector> GetQueryVectorAsync(string query, CancellationToken ct)
    {
        var key = _options.EmbeddingModel + "\u0000" + query;
        if (cache.Get(key) is { } cached) return cached;

        var vectors = await embedding.EmbedAsync([query], ct);
        if (vectors.Count != 1)
            throw new InvalidOperationException($"query embedding 返回 {vectors.Count} 条，期望 1 条");

        cache.Set(key, vectors[0]);
        return vectors[0];
    }

    /// <summary>写检索日志（评测与审计数据源）；失败只告警，不影响检索结果。</summary>
    private async Task WriteLogAsync(
        string query, IReadOnlyList<string>? knowledgeBaseIds, int topK,
        IReadOnlyList<KbSearchHit> hits, int elapsedMs, string caller, CancellationToken ct)
    {
        try
        {
            await store.AddRetrievalLogAsync(new RetrievalLog
            {
                KnowledgeBaseId = knowledgeBaseIds is { Count: 1 } ? knowledgeBaseIds[0] : null,
                Query = query,
                TopK = topK,
                HitCount = hits.Count,
                TopScore = hits.Count > 0 ? (float)hits[0].Score : null,
                ElapsedMs = elapsedMs,
                Caller = Truncate(caller),
                CreatedAt = DateTimeOffset.UtcNow,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "写入检索日志失败（不影响检索结果）");
        }
    }

    /// <summary>Caller 列为 varchar(64)，空值回落为 admin-debug。</summary>
    private static string Truncate(string? caller)
    {
        var value = string.IsNullOrWhiteSpace(caller) ? RetrievalCallers.AdminDebug : caller.Trim();
        return value.Length <= 64 ? value : value[..64];
    }
}
