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

/// <summary>检索结果：命中片段（含来源信息）+ 端到端耗时（embedding + 向量检索），以及可选的 rerank 耗时。</summary>
public sealed record KnowledgeSearchResult(
    IReadOnlyList<KbSearchHit> Hits, int ElapsedMs, int? RerankElapsedMs = null);

/// <summary>
/// 检索服务：query → embedding → pgvector 余弦 top-k → 组装引用信息 → 写检索日志。
/// 只检索启用中的知识库；knowledgeBaseIds 为空表示检索全部启用库。
/// 
/// P4 hybrid（EnableHybrid=true）：同时跑 dense 与 sparse 两条召回路径，RRF 融合后再 rerank。
/// 关闭 hybrid 时行为与一期完全一致（仅 dense）。
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
    IRerankClient rerank,
    QueryEmbeddingCache cache,
    IOptions<RagOptions> options,
    ILogger<RetrievalService> logger) : IRetrievalService
{
    /// <summary>候选数不超过该值时跳过 rerank（精排收益有限，且省一次外部调用）。</summary>
    private const int MinRerankCandidates = 3;

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

        IReadOnlyList<KbSearchHit> candidates;
        string retrievalSource;

        if (_options.EnableHybrid)
        {
            // ===== P4 hybrid：dense + sparse 并行召回 → RRF 融合 =====
            var hybridSw = Stopwatch.StartNew();

            // dense 路径：多取候选给 rerank 留出空间
            var denseTopK = Math.Max(_options.HybridDenseTopK,
                _options.EnableRerank ? _options.RerankCandidateTopN : k);
            var denseVector = await GetQueryVectorAsync(normalized, ct);
            var denseTask = store.SearchChunksAsync(
                denseVector, kbIds, denseTopK, _options.MinScore, ct);

            // sparse 路径：中文 bigram token → tsquery
            var queryTokens = ChineseTokenizer.QueryTokens(normalized);
            IReadOnlyList<KbSearchHit> sparseHits = Array.Empty<KbSearchHit>();
            if (queryTokens.Count > 0)
            {
                try
                {
                    sparseHits = await store.SearchChunksSparseAsync(
                        queryTokens, kbIds, _options.HybridSparseTopK, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // sparse 失败降级为纯 dense（只告警，不让检索失败）
                    _logger.LogWarning(ex, "hybrid sparse 路径失败，降级为纯 dense 召回");
                }
            }

            var denseHits = await denseTask;

            // RRF 融合：score = Σ 1/(k + rank)
            candidates = RrfFuse(denseHits, sparseHits, _options.RrfK);
            retrievalSource = sparseHits.Count > 0 && denseHits.Count > 0 ? "hybrid"
                : denseHits.Count > 0 ? "dense"
                : sparseHits.Count > 0 ? "sparse"
                : "dense";

            hybridSw.Stop();
            _logger.LogInformation(
                "hybrid 召回完成：dense={Dense}，sparse={Sparse}，融合后={Fused}，耗时 {HybridElapsed}ms",
                denseHits.Count, sparseHits.Count, candidates.Count, hybridSw.ElapsedMilliseconds);
        }
        else
        {
            // ===== 一期 / 纯 dense 路径 =====
            var vector = await GetQueryVectorAsync(normalized, ct);
            var candidateCount = _options.EnableRerank
                ? Math.Max(k, _options.RerankCandidateTopN)
                : k;
            candidates = await store.SearchChunksAsync(vector, kbIds, candidateCount, _options.MinScore, ct);
            retrievalSource = "dense";
        }

        int? rerankElapsedMs = null;
        IReadOnlyList<KbSearchHit> hits = candidates;

        // rerank：开启后在融合候选上精排，候选 > 3 才调用
        if (_options.EnableRerank && candidates.Count > MinRerankCandidates)
        {
            var rerankSw = Stopwatch.StartNew();
            var ranked = await rerank.RerankAsync(
                normalized, candidates.Select(c => c.Content).ToList(), k, ct);
            rerankSw.Stop();
            rerankElapsedMs = (int)rerankSw.ElapsedMilliseconds;

            hits = ranked.Count > 0
                ? ranked.Take(k).Select(r => candidates[r.Index] with { RerankScore = r.Score, RetrievalSource = retrievalSource }).ToList()
                : candidates.Take(k).Select(c => c with { RetrievalSource = retrievalSource }).ToList();
        }
        else
        {
            hits = candidates.Take(k).Select(c => c with { RetrievalSource = retrievalSource }).ToList();
        }

        sw.Stop();
        var elapsedMs = (int)sw.ElapsedMilliseconds;

        await WriteLogAsync(normalized, kbIds, k, hits, elapsedMs, caller, ct);

        _logger.LogInformation(
            "知识库检索完成：{Hits} 条命中，top-k={TopK}，候选={Candidates}，来源={Source}，rerank 耗时={RerankElapsed}ms，总耗时 {Elapsed}ms，调用方={Caller}",
            hits.Count, k, candidates.Count, retrievalSource,
            rerankElapsedMs?.ToString() ?? "-", elapsedMs, Truncate(caller));

        return new KnowledgeSearchResult(hits, elapsedMs, rerankElapsedMs);
    }

    /// <summary>
    /// Reciprocal Rank Fusion 融合。
    /// 对 dense + sparse 两路结果按 RRF 合并：score = Σ 1/(k + rank)。
    /// sparse 独有的片段（dense 未召回）Score 直接使用 ts_rank 原始分。
    /// </summary>
    private static IReadOnlyList<KbSearchHit> RrfFuse(
        IReadOnlyList<KbSearchHit> dense, IReadOnlyList<KbSearchHit> sparse, int rrfK)
    {
        var scores = new Dictionary<string, (double RrfScore, KbSearchHit Hit)>();

        for (int rank = 0; rank < dense.Count; rank++)
        {
            var hit = dense[rank];
            var rrfScore = 1.0 / (rrfK + rank);
            if (scores.TryGetValue(hit.ChunkId, out var existing))
                scores[hit.ChunkId] = (existing.RrfScore + rrfScore, existing.Hit);
            else
                scores[hit.ChunkId] = (rrfScore, hit);
        }

        for (int rank = 0; rank < sparse.Count; rank++)
        {
            var hit = sparse[rank];
            var rrfScore = 1.0 / (rrfK + rank);
            if (scores.TryGetValue(hit.ChunkId, out var existing))
                scores[hit.ChunkId] = (existing.RrfScore + rrfScore, existing.Hit);
            else
                scores[hit.ChunkId] = (rrfScore, hit with { RetrievalSource = "sparse-only" });
        }

        return scores.Values
            .OrderByDescending(x => x.RrfScore)
            .Select(x => x.Hit with { Score = x.RrfScore })
            .ToList();
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
