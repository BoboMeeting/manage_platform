using System.Security.Cryptography;
using ManagerPlatform.Auth;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Services;
using ManagerPlatform.Stores;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.Endpoints;

/// <summary>
/// 知识库管理端 API（RAG 一期：全局共享知识库，dense 向量检索）。
/// 全部挂在 /api/admin 下，组级 OperatorPlus（启动期守护 EnsureAdminEndpointsAreIsolated 要求）。
/// 导入为异步：上传只落文件 + 建记录 + 入队并返回 202，解析/切分/embedding/入库全在 Worker 完成。
/// </summary>
public static class KnowledgeEndpoints
{
    /// <summary>支持格式（PDF 优先文本层，无文本层时按 OCR 配置兜底）。</summary>
    private static readonly string[] AllowedExtensions = [".txt", ".md", ".docx", ".pdf"];

    /// <summary>单文件大小上限（技术方案 §7.5），上传即拦截，避免无效任务进入流水线。</summary>
    private const long MaxFileSizeBytes = 50L * 1024 * 1024;

    /// <summary>失败重试上限（技术方案 §7.4），按该文档 failed 事件数计。</summary>
    private const int MaxRetryCount = 3;

    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/knowledge-bases").WithTags("Knowledge")
            .RequireAuthorization(Policies.OperatorPlus);

        // ===== 知识库 =====

        // 创建：embedding 模型/维度取自全局配置（一期全局唯一维度），名称唯一
        group.MapPost("/", async (
            CreateKnowledgeBaseRequest req,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            IOptions<RagOptions> rag,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();

            var name = req.Name?.Trim() ?? string.Empty;
            if (name.Length is 0 or > 128)
                return Results.BadRequest(new { error = "知识库名称长度须为 1-128 字符" });

            var chunkSize = req.ChunkSize ?? rag.Value.ChunkSize;
            var chunkOverlap = req.ChunkOverlap ?? rag.Value.ChunkOverlap;
            if (chunkSize <= 0)
                return Results.BadRequest(new { error = "chunkSize 必须为正数" });
            if (chunkOverlap < 0 || chunkOverlap >= chunkSize)
                return Results.BadRequest(new { error = "chunkOverlap 必须 ≥ 0 且小于 chunkSize" });

            if (await store.NameExistsAsync(name, null, ct))
                return Results.Conflict(new { error = "知识库名称已存在" });

            var kb = new KnowledgeBase
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
                EmbeddingModel = rag.Value.EmbeddingModel,
                EmbeddingDim = rag.Value.EmbeddingDim,
                ChunkSize = chunkSize,
                ChunkOverlap = chunkOverlap,
                CreatedBy = cu.UserId,
            };
            await store.AddAsync(kb, ct);

            logger.LogInformation("审计[创建知识库]：操作人={Operator}，知识库={Id}，名称={Name}",
                cu.UserId, kb.Id, kb.Name);
            return Results.Created($"/api/admin/knowledge-bases/{kb.Id}", kb);
        });

        // 列表：含各库文档总数与状态计数（一次分组查询，避免 N+1）
        group.MapGet("/", async (
            HttpContext ctx,
            IKnowledgeBaseStore store,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();

            var knowledgeBases = await store.ListAsync(ct);
            var counts = await store.CountDocumentsByStatusAsync(ct);

            var items = knowledgeBases.Select(kb =>
            {
                var rows = counts.Where(c => c.KnowledgeBaseId == kb.Id).ToList();
                return new KnowledgeBaseListItem(
                    kb,
                    DocumentCount: rows.Sum(r => r.Count),
                    ReadyCount: rows.Where(r => r.Status == KbDocumentStatus.Ready).Sum(r => r.Count),
                    ProcessingCount: rows.Where(r => r.Status is KbDocumentStatus.Pending
                        or KbDocumentStatus.Parsing or KbDocumentStatus.Indexing).Sum(r => r.Count),
                    FailedCount: rows.Where(r => r.Status == KbDocumentStatus.Failed).Sum(r => r.Count));
            }).ToList();

            return Results.Ok(items);
        });

        // 详情：管理端详情页头部/编辑回填（列表项包了一层计数，单独取更直接）
        group.MapGet("/{kbId}", async (
            string kbId,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            var kb = await store.GetAsync(kbId, ct);
            return kb is null
                ? Results.NotFound(new { error = "知识库不存在" })
                : Results.Ok(kb);
        });

        // 改名 / 启停 / 调 chunk 参数（部分更新；chunk 参数只影响后续导入与重建）
        group.MapPatch("/{kbId}", async (
            string kbId,
            UpdateKnowledgeBaseRequest req,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var kb = await store.GetAsync(kbId, ct);
            if (kb is null) return Results.NotFound(new { error = "知识库不存在" });

            if (req.Name is not null)
            {
                var name = req.Name.Trim();
                if (name.Length is 0 or > 128)
                    return Results.BadRequest(new { error = "知识库名称长度须为 1-128 字符" });
                if (await store.NameExistsAsync(name, kbId, ct))
                    return Results.Conflict(new { error = "知识库名称已存在" });
                kb.Name = name;
            }

            if (req.Description is not null)
                kb.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();

            if (req.Enabled is { } enabled)
                kb.Status = enabled ? KbStatus.Enabled : KbStatus.Disabled;

            if (req.ChunkSize is not null || req.ChunkOverlap is not null)
            {
                var chunkSize = req.ChunkSize ?? kb.ChunkSize;
                var chunkOverlap = req.ChunkOverlap ?? kb.ChunkOverlap;
                if (chunkSize <= 0)
                    return Results.BadRequest(new { error = "chunkSize 必须为正数" });
                if (chunkOverlap < 0 || chunkOverlap >= chunkSize)
                    return Results.BadRequest(new { error = "chunkOverlap 必须 ≥ 0 且小于 chunkSize" });
                kb.ChunkSize = chunkSize;
                kb.ChunkOverlap = chunkOverlap;
            }

            await store.UpdateAsync(kb, ct);
            logger.LogInformation("审计[更新知识库]：操作人={Operator}，知识库={Id}", cu.UserId, kbId);
            return Results.Ok(kb);
        });

        // 删除：文档与 chunk 由数据库级联删除；事件表无外键须显式清理；原文件目录一并删除
        group.MapDelete("/{kbId}", async (
            string kbId,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            IStorageService storage,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var kb = await store.GetAsync(kbId, ct);
            if (kb is null) return Results.NotFound(new { error = "知识库不存在" });

            await store.DeleteEventsByKnowledgeBaseAsync(kbId, ct);
            await store.DeleteAsync(kbId, ct);
            await KnowledgeStorage.DeleteKnowledgeBaseAsync(storage, kbId, ct);

            logger.LogInformation("审计[删除知识库]：操作人={Operator}，知识库={Id}，名称={Name}",
                cu.UserId, kbId, kb.Name);
            return Results.Ok(new { ok = true });
        });

        // ===== 文档 =====

        // 上传：multipart；落盘（同时算 sha256）→ 查重 → 建记录（Pending）→ 入队，返回 202
        group.MapPost("/{kbId}/documents", async (
            string kbId,
            IFormFile file,
            string? title,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            IStorageService storage,
            IKbIngestQueue queue,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var kb = await store.GetAsync(kbId, ct);
            if (kb is null) return Results.NotFound(new { error = "知识库不存在" });

            if (file.Length == 0)
                return Results.BadRequest(new { error = "文件为空" });
            if (file.Length > MaxFileSizeBytes)
                return Results.BadRequest(new { error = "文件大小超过上限 50MB" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return Results.BadRequest(new { error = $"不支持的格式 {ext}（允许 txt/md/docx/pdf）" });
            if (!string.IsNullOrWhiteSpace(title) && title.Trim().Length > 256)
                return Results.BadRequest(new { error = "标题长度不能超过 256 字符" });

            var documentId = Guid.NewGuid().ToString("N");
            var fileName = Path.GetFileName(file.FileName);
            var storageKey = KnowledgeStorage.BuildKey(kbId, documentId, fileName);

            // 落盘同时计算 sha256（读流模式 CryptoStream），用于同库重复导入检测
            string checksum;
            await using (var input = file.OpenReadStream())
            {
                using var sha = SHA256.Create();
                await using var hashing = new CryptoStream(input, sha, CryptoStreamMode.Read);
                await storage.SaveAsync(storageKey, hashing, ct);
                checksum = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            var duplicate = await store.FindDocumentByChecksumAsync(kbId, checksum, ct);
            if (duplicate is not null)
            {
                // 同内容文件已导入：清掉刚落的副本，不产生重复索引
                await storage.DeleteAsync(storageKey, ct);
                return Results.Conflict(new { error = "同内容文件已导入，不重复索引", existingDocumentId = duplicate.Id });
            }

            var document = new KbDocument
            {
                Id = documentId,
                KnowledgeBaseId = kbId,
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fileName) : title.Trim(),
                OriginalFileName = fileName,
                ContentType = file.ContentType,
                FileSizeBytes = file.Length,
                StorageKey = storageKey,
                Checksum = checksum,
                CreatedBy = cu.UserId,
            };
            await store.AddDocumentAsync(document, ct);
            queue.Enqueue(documentId);

            logger.LogInformation("审计[上传知识库文档]：操作人={Operator}，知识库={KbId}，文档={Id}，文件={File}，大小={Bytes}",
                cu.UserId, kbId, documentId, fileName, file.Length);
            return Results.Accepted($"/api/admin/knowledge-bases/{kbId}/documents/{documentId}", document);
        }).DisableAntiforgery();

        // 文档列表：分页 + 处理状态（管理端轮询进度用）
        group.MapGet("/{kbId}/documents", async (
            string kbId,
            int page,
            int pageSize,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            if (await store.GetAsync(kbId, ct) is null)
                return Results.NotFound(new { error = "知识库不存在" });

            page = Math.Max(1, page <= 0 ? 1 : page);
            pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);
            var (items, total) = await store.ListDocumentsAsync(kbId, page, pageSize, ct);
            return Results.Ok(new PagedResult<KbDocument>(items, total, page, pageSize));
        });

        // 文档详情：详情弹窗刷新（状态/失败原因/chunk 数）
        group.MapGet("/{kbId}/documents/{docId}", async (
            string kbId,
            string docId,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            var document = await store.GetDocumentAsync(docId, ct);
            if (document is null || document.KnowledgeBaseId != kbId)
                return Results.NotFound(new { error = "文档不存在" });
            return Results.Ok(document);
        });

        // 失败重试：按文档整体重建（Worker 先删旧 chunk 再写新 chunk）；重试上限 3 次（按 failed 事件数计）
        group.MapPost("/{kbId}/documents/{docId}/retry", async (
            string kbId,
            string docId,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            IKbIngestQueue queue,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var document = await store.GetDocumentAsync(docId, ct);
            if (document is null || document.KnowledgeBaseId != kbId)
                return Results.NotFound(new { error = "文档不存在" });
            if (document.Status != KbDocumentStatus.Failed)
                return Results.Conflict(new { error = "仅失败（Failed）文档可重试" });

            var events = await store.ListEventsAsync(docId, ct);
            var failures = events.Count(ev => ev.Status == "failed");
            if (failures >= MaxRetryCount)
                return Results.Conflict(new { error = $"重试次数已达上限（{MaxRetryCount} 次）" });

            document.Status = KbDocumentStatus.Pending;
            document.ErrorMessage = null;
            await store.UpdateDocumentAsync(document, ct);
            queue.Enqueue(docId);

            logger.LogInformation("审计[重试知识库文档]：操作人={Operator}，文档={Id}，第 {Retry} 次重试",
                cu.UserId, docId, failures + 1);
            return Results.Ok(new { ok = true, retryCount = failures + 1 });
        });

        // 删除文档：处理中（Parsing/Indexing）不可删；chunk 级联删除，事件与原文件显式清理
        group.MapDelete("/{kbId}/documents/{docId}", async (
            string kbId,
            string docId,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            IStorageService storage,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var document = await store.GetDocumentAsync(docId, ct);
            if (document is null || document.KnowledgeBaseId != kbId)
                return Results.NotFound(new { error = "文档不存在" });
            if (document.Status is KbDocumentStatus.Parsing or KbDocumentStatus.Indexing)
                return Results.Conflict(new { error = "文档处理中，不可删除" });

            await store.DeleteEventsByDocumentAsync(docId, ct);
            await store.DeleteDocumentAsync(docId, ct);
            await KnowledgeStorage.DeleteDocumentAsync(storage, kbId, docId, ct);

            logger.LogInformation("审计[删除知识库文档]：操作人={Operator}，知识库={KbId}，文档={Id}，标题={Title}",
                cu.UserId, kbId, docId, document.Title);
            return Results.Ok(new { ok = true });
        });

        // 处理时间线：Parse / Chunk / Embed / Persist 各阶段流水
        group.MapGet("/{kbId}/documents/{docId}/events", async (
            string kbId,
            string docId,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            var document = await store.GetDocumentAsync(docId, ct);
            if (document is null || document.KnowledgeBaseId != kbId)
                return Results.NotFound(new { error = "文档不存在" });
            return Results.Ok(await store.ListEventsAsync(docId, ct));
        });

        // ===== 检索调试（Scalar 里直接验证检索效果，写 retrieval_logs 记为 admin-debug）=====

        group.MapPost("/{kbId}/search", async (
            string kbId,
            KnowledgeSearchRequest req,
            HttpContext ctx,
            IKnowledgeBaseStore store,
            IRetrievalService retrieval,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            if (await store.GetAsync(kbId, ct) is null)
                return Results.NotFound(new { error = "知识库不存在" });
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new { error = "query 不能为空" });

            var result = await retrieval.SearchAsync(
                req.Query.Trim(), [kbId], req.TopK, RetrievalCallers.AdminDebug, ct);
            return Results.Ok(new KnowledgeSearchResponse(
                req.Query.Trim(), result.ElapsedMs, result.RerankElapsedMs, result.Hits));
        });

        return app;
    }
}

public sealed record CreateKnowledgeBaseRequest(
    string? Name,
    string? Description,
    int? ChunkSize,
    int? ChunkOverlap);

public sealed record UpdateKnowledgeBaseRequest(
    string? Name,
    string? Description,
    bool? Enabled,
    int? ChunkSize,
    int? ChunkOverlap);

/// <summary>知识库列表项：实体 + 文档总数与各状态计数。</summary>
public sealed record KnowledgeBaseListItem(
    KnowledgeBase KnowledgeBase,
    int DocumentCount,
    int ReadyCount,
    int ProcessingCount,
    int FailedCount);

public sealed record KnowledgeSearchRequest(string? Query, int? TopK);

public sealed record KnowledgeSearchResponse(
    string Query,
    int ElapsedMs,
    int? RerankElapsedMs,
    IReadOnlyList<KbSearchHit> Hits);
