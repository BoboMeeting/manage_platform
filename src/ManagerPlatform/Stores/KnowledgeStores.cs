using ManagerPlatform.Data;
using ManagerPlatform.Models;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace ManagerPlatform.Stores;

/// <summary>向量检索命中结果（含来源信息，供 MCP 直接组织回答）。</summary>
public sealed record KbSearchHit(
    string ChunkId,
    string DocumentId,
    string DocumentTitle,
    string? HeadingPath,
    int? PageNo,
    string Content,
    double Score);

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
    Task<KbDocument?> FindDocumentByChecksumAsync(
        string knowledgeBaseId, string checksum, CancellationToken ct = default);
    /// <summary>非终态文档（Pending/Parsing/Indexing），供 Worker 启动恢复。</summary>
    Task<IReadOnlyList<KbDocument>> ListUnfinishedDocumentsAsync(CancellationToken ct = default);
    Task AddDocumentAsync(KbDocument document, CancellationToken ct = default);
    Task UpdateDocumentAsync(KbDocument document, CancellationToken ct = default);
    Task DeleteDocumentAsync(string id, CancellationToken ct = default);

    // ---- 片段 ----
    Task AddChunksAsync(IReadOnlyCollection<KbChunk> chunks, CancellationToken ct = default);
    Task DeleteChunksByDocumentAsync(string documentId, CancellationToken ct = default);
    /// <summary>按余弦距离检索 top-k 片段；knowledgeBaseIds 为空表示检索全部启用库。</summary>
    Task<IReadOnlyList<KbSearchHit>> SearchChunksAsync(
        Vector queryVector, IReadOnlyList<string>? knowledgeBaseIds,
        int topK, double minScore, CancellationToken ct = default);

    // ---- 导入事件 ----
    Task<IReadOnlyList<KbIngestEvent>> ListEventsAsync(string documentId, CancellationToken ct = default);
    Task AddEventAsync(KbIngestEvent evt, CancellationToken ct = default);

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

    // ---- 检索日志 ----

    public async Task AddRetrievalLogAsync(RetrievalLog log, CancellationToken ct = default)
    {
        _db.RetrievalLogs.Add(log);
        await _db.SaveChangesAsync(ct);
    }
}
