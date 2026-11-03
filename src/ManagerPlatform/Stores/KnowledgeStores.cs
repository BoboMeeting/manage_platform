using ManagerPlatform.Data;
using ManagerPlatform.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace ManagerPlatform.Stores;

/// <summary>
/// 向量检索命中结果（含来源信息，供 MCP 直接组织回答）。
/// Score 为向量余弦相似度；RerankScore 为精排分数（未启用 rerank 或未命中时为 null）；
/// RetrievalSource 标记召回路径（dense / hybrid，P4 使用）。
/// </summary>
public sealed record KbSearchHit(
    string ChunkId,
    string DocumentId,
    string DocumentTitle,
    string? HeadingPath,
    int? PageNo,
    string Content,
    double Score,
    double? RerankScore = null,
    string RetrievalSource = "dense");

/// <summary>某知识库下指定状态的文档数（管理端列表的状态计数）。</summary>
public sealed record KbDocumentStatusCount(string KnowledgeBaseId, KbDocumentStatus Status, int Count);

/// <summary>知识库仓储：知识库、文档、片段、导入事件、检索日志。</summary>
public interface IKnowledgeBaseStore
{
    // ---- 知识库 ----
    Task<KnowledgeBase?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, string? exceptId = null, CancellationToken ct = default);
    Task AddAsync(KnowledgeBase knowledgeBase, CancellationToken ct = default);
    Task UpdateAsync(KnowledgeBase knowledgeBase, CancellationToken ct = default);
    /// <summary>删除知识库（数据库级联删除文档与 chunk；原文件由调用方清理）。</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    // ---- 文档 ----
    Task<KbDocument?> GetDocumentAsync(string id, CancellationToken ct = default);
    Task<(IReadOnlyList<KbDocument> Items, int Total)> ListDocumentsAsync(
        string knowledgeBaseId, int page, int pageSize, CancellationToken ct = default);
    /// <summary>获取该库全部文档（不分页，用于重建索引等批量操作）。</summary>
    Task<IReadOnlyList<KbDocument>> ListDocumentsAllAsync(
        string knowledgeBaseId, CancellationToken ct = default);
    Task<KbDocument?> FindDocumentByChecksumAsync(
        string knowledgeBaseId, string checksum, CancellationToken ct = default);
    /// <summary>非终态文档（Pending/Parsing/Indexing），供 Worker 启动恢复。</summary>
    Task<IReadOnlyList<KbDocument>> ListUnfinishedDocumentsAsync(CancellationToken ct = default);
    Task AddDocumentAsync(KbDocument document, CancellationToken ct = default);
    Task UpdateDocumentAsync(KbDocument document, CancellationToken ct = default);
    Task DeleteDocumentAsync(string id, CancellationToken ct = default);
    /// <summary>全部知识库的文档状态计数（一次分组查询，避免列表页 N+1）。</summary>
    Task<IReadOnlyList<KbDocumentStatusCount>> CountDocumentsByStatusAsync(CancellationToken ct = default);

    // ---- 片段 ----
    Task AddChunksAsync(IReadOnlyCollection<KbChunk> chunks, CancellationToken ct = default);
    Task DeleteChunksByDocumentAsync(string documentId, CancellationToken ct = default);
    /// <summary>按余弦距离检索 top-k 片段；knowledgeBaseIds 为空表示检索全部启用库。</summary>
    Task<IReadOnlyList<KbSearchHit>> SearchChunksAsync(
        Vector queryVector, IReadOnlyList<string>? knowledgeBaseIds,
        int topK, double minScore, CancellationToken ct = default);
    /// <summary>按稀疏召回（ts_rank_cd）检索 top-k 片段；knowledgeBaseIds 为空表示检索全部启用库。</summary>
    Task<IReadOnlyList<KbSearchHit>> SearchChunksSparseAsync(
        IReadOnlyList<string> queryTokens, IReadOnlyList<string>? knowledgeBaseIds,
        int topK, CancellationToken ct = default);

    // ---- 导入事件 ----
    Task<IReadOnlyList<KbIngestEvent>> ListEventsAsync(string documentId, CancellationToken ct = default);
    Task AddEventAsync(KbIngestEvent evt, CancellationToken ct = default);
    /// <summary>清理某文档的全部导入事件（事件表无外键，删除文档时须显式清理）。</summary>
    Task DeleteEventsByDocumentAsync(string documentId, CancellationToken ct = default);
    /// <summary>清理某知识库下全部文档的导入事件（删除知识库时调用）。</summary>
    Task DeleteEventsByKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct = default);

    // ---- 检索日志 ----
    Task AddRetrievalLogAsync(RetrievalLog log, CancellationToken ct = default);
}

public sealed class EfKnowledgeBaseStore(AppDbContext db) : IKnowledgeBaseStore
{
    private readonly AppDbContext _db = db;

    // ---- 知识库 ----

    public Task<KnowledgeBase?> GetAsync(string id, CancellationToken ct = default) =>
        _db.KnowledgeBases.FirstOrDefaultAsync(k => k.Id == id, ct);

    public async Task<IReadOnlyList<KnowledgeBase>> ListAsync(CancellationToken ct = default) =>
        await _db.KnowledgeBases.AsNoTracking()
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);

    public Task<bool> NameExistsAsync(string name, string? exceptId = null, CancellationToken ct = default) =>
        _db.KnowledgeBases.AnyAsync(k => k.Name == name && (exceptId == null || k.Id != exceptId), ct);

    public async Task AddAsync(KnowledgeBase knowledgeBase, CancellationToken ct = default)
    {
        _db.KnowledgeBases.Add(knowledgeBase);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(KnowledgeBase knowledgeBase, CancellationToken ct = default)
    {
        knowledgeBase.UpdatedAt = DateTimeOffset.UtcNow;
        _db.KnowledgeBases.Update(knowledgeBase);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default) =>
        await _db.KnowledgeBases.Where(k => k.Id == id).ExecuteDeleteAsync(ct);

    // ---- 文档 ----

    public Task<KbDocument?> GetDocumentAsync(string id, CancellationToken ct = default) =>
        _db.KbDocuments.FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<(IReadOnlyList<KbDocument> Items, int Total)> ListDocumentsAsync(
        string knowledgeBaseId, int page, int pageSize, CancellationToken ct = default)
    {
        var q = _db.KbDocuments.AsNoTracking()
            .Where(d => d.KnowledgeBaseId == knowledgeBaseId);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public Task<KbDocument?> FindDocumentByChecksumAsync(
        string knowledgeBaseId, string checksum, CancellationToken ct = default) =>
        _db.KbDocuments.FirstOrDefaultAsync(
            d => d.KnowledgeBaseId == knowledgeBaseId && d.Checksum == checksum, ct);

    public async Task<IReadOnlyList<KbDocument>> ListDocumentsAllAsync(
        string knowledgeBaseId, CancellationToken ct = default) =>
        await _db.KbDocuments.AsNoTracking()
            .Where(d => d.KnowledgeBaseId == knowledgeBaseId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<KbDocument>> ListUnfinishedDocumentsAsync(CancellationToken ct = default) =>
        await _db.KbDocuments.AsNoTracking()
            .Where(d => d.Status == KbDocumentStatus.Pending
                     || d.Status == KbDocumentStatus.Parsing
                     || d.Status == KbDocumentStatus.Indexing)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct);

    public async Task AddDocumentAsync(KbDocument document, CancellationToken ct = default)
    {
        _db.KbDocuments.Add(document);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateDocumentAsync(KbDocument document, CancellationToken ct = default)
    {
        document.UpdatedAt = DateTimeOffset.UtcNow;
        _db.KbDocuments.Update(document);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteDocumentAsync(string id, CancellationToken ct = default) =>
        await _db.KbDocuments.Where(d => d.Id == id).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<KbDocumentStatusCount>> CountDocumentsByStatusAsync(
        CancellationToken ct = default) =>
        await _db.KbDocuments.AsNoTracking()
            .GroupBy(d => new { d.KnowledgeBaseId, d.Status })
            .Select(g => new KbDocumentStatusCount(g.Key.KnowledgeBaseId, g.Key.Status, g.Count()))
            .ToListAsync(ct);

    // ---- 片段 ----

    public async Task AddChunksAsync(IReadOnlyCollection<KbChunk> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;
        _db.KbChunks.AddRange(chunks);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteChunksByDocumentAsync(string documentId, CancellationToken ct = default) =>
        await _db.KbChunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync(ct);

    public async Task<IReadOnlyList<KbSearchHit>> SearchChunksAsync(
        Vector queryVector, IReadOnlyList<string>? knowledgeBaseIds,
        int topK, double minScore, CancellationToken ct = default)
    {
        // 只检索启用中的知识库（停用库不参与）
        var q = from c in _db.KbChunks.AsNoTracking()
                join k in _db.KnowledgeBases on c.KnowledgeBaseId equals k.Id
                where k.Status == KbStatus.Enabled
                select c;

        if (knowledgeBaseIds is { Count: > 0 })
        {
            var ids = knowledgeBaseIds.ToArray();
            q = q.Where(c => ids.Contains(c.KnowledgeBaseId));
        }

        // 余弦距离越大越不相关；先按距离升序取 top-k，再按阈值过滤
        var maxDistance = 1.0 - minScore;
        var rows = await q
            .Where(c => c.Embedding.CosineDistance(queryVector) <= maxDistance)
            .OrderBy(c => c.Embedding.CosineDistance(queryVector))
            .Take(topK)
            .Select(c => new
            {
                c.Id,
                c.DocumentId,
                c.Content,
                c.HeadingPath,
                c.PageNo,
                Distance = c.Embedding.CosineDistance(queryVector),
            })
            .ToListAsync(ct);

        if (rows.Count == 0) return Array.Empty<KbSearchHit>();

        var docIds = rows.Select(r => r.DocumentId).Distinct().ToArray();
        var titles = await _db.KbDocuments.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Title })
            .ToDictionaryAsync(d => d.Id, d => d.Title, ct);

        return rows
            .Select(r => new KbSearchHit(
                r.Id, r.DocumentId, titles.GetValueOrDefault(r.DocumentId, string.Empty),
                r.HeadingPath, r.PageNo, r.Content, 1.0 - r.Distance))
            .ToList();
    }

    public async Task<IReadOnlyList<KbSearchHit>> SearchChunksSparseAsync(
        IReadOnlyList<string> queryTokens, IReadOnlyList<string>? knowledgeBaseIds,
        int topK, CancellationToken ct = default)
    {
        if (queryTokens is not { Count: > 0 })
            return Array.Empty<KbSearchHit>();

        // 转成 PostgreSQL simple 配置的 tsquery：用 | 连接各 gram
        // simple 配置不做词形还原，按空格/标点分词，与中文 bigram 输出天然对齐
        var escaped = queryTokens
            .Select(t => EscapeTsQuerySimple(t))
            .Where(t => t.Length > 0)
            .ToList();
        if (escaped.Count == 0) return Array.Empty<KbSearchHit>();
        var tsQueryStr = string.Join(" | ", escaped);

        // 使用 ADO.NET 直接执行 ts_rank_cd 查询（EF Core 难以投影计算列）
        var ids = knowledgeBaseIds?.ToArray();
        var kbFilterSql = ids is { Length: > 0 }
            ? "AND k.\"Id\" = ANY(@kbIds)"
            : string.Empty;

        var sql = $@"
            SELECT c.""Id"", c.""DocumentId"", c.""Content"", c.""HeadingPath"", c.""PageNo"",
                   ts_rank_cd(c.""SearchVector"", to_tsquery('simple', @tsQuery)) AS Rank
            FROM kb_chunks c
            JOIN knowledge_bases k ON c.""KnowledgeBaseId"" = k.""Id""
            WHERE k.""Status"" = 0
              {kbFilterSql}
              AND c.""SearchVector"" @@ to_tsquery('simple', @tsQuery)
            ORDER BY Rank DESC
            LIMIT @topK";

        var conn = _db.Database.GetDbConnection();
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("tsQuery", tsQueryStr));
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter("topK", topK));
        if (ids is { Length: > 0 })
            cmd.Parameters.Add(new Npgsql.NpgsqlParameter("kbIds", ids));

        var hits = new List<(string Id, string DocId, string Content, string? HeadingPath, int? PageNo, double Rank)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                hits.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.GetDouble(5)));
            }
        }
        await conn.CloseAsync();

        if (hits.Count == 0) return Array.Empty<KbSearchHit>();

        var docIds = hits.Select(h => h.DocId).Distinct().ToArray();
        var titles = await _db.KbDocuments.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Title })
            .ToDictionaryAsync(d => d.Id, d => d.Title, ct);

        return hits.Select(h => new KbSearchHit(
            h.Id, h.DocId, titles.GetValueOrDefault(h.DocId, string.Empty),
            h.HeadingPath, h.PageNo, h.Content, h.Rank)).ToList();
    }

    /// <summary>
    /// 转义 PostgreSQL simple 配置 tsquery 的特殊字符（直接丢弃，不转义）。
    /// simple 配置会将查询文本按非字母数字分词，所以只保留安全 token 即可。
    /// </summary>
    private static string EscapeTsQuerySimple(string token)
    {
        if (string.IsNullOrEmpty(token)) return string.Empty;
        var sb = new System.Text.StringBuilder(token.Length);
        foreach (char c in token)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- 导入事件 ----

    public async Task<IReadOnlyList<KbIngestEvent>> ListEventsAsync(
        string documentId, CancellationToken ct = default) =>
        await _db.KbIngestEvents.AsNoTracking()
            .Where(ev => ev.DocumentId == documentId)
            .OrderBy(ev => ev.CreatedAt)
            .ToListAsync(ct);

    public async Task AddEventAsync(KbIngestEvent evt, CancellationToken ct = default)
    {
        _db.KbIngestEvents.Add(evt);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteEventsByDocumentAsync(string documentId, CancellationToken ct = default) =>
        await _db.KbIngestEvents.Where(ev => ev.DocumentId == documentId).ExecuteDeleteAsync(ct);

    public async Task DeleteEventsByKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct = default) =>
        await _db.KbIngestEvents
            .Where(ev => _db.KbDocuments
                .Where(d => d.KnowledgeBaseId == knowledgeBaseId)
                .Select(d => d.Id)
                .Contains(ev.DocumentId))
            .ExecuteDeleteAsync(ct);

    // ---- 检索日志 ----

    public async Task AddRetrievalLogAsync(RetrievalLog log, CancellationToken ct = default)
    {
        _db.RetrievalLogs.Add(log);
        await _db.SaveChangesAsync(ct);
    }
}
