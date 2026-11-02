using System.Threading.Channels;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Stores;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.Services;

/// <summary>知识库导入任务队列（进程内实现；上线替换为 Redis/MQ 时接口语义不变）。</summary>
public interface IKbIngestQueue
{
    void Enqueue(string documentId);
}

public sealed class InMemoryKbIngestQueue : IKbIngestQueue
{
    private readonly Channel<string> _channel =
        Channel.CreateUnbounded<string>(new() { SingleReader = true });

    public void Enqueue(string documentId) => _channel.Writer.TryWrite(documentId);

    internal IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// 知识库导入流水线 Worker（后台服务）。
/// 状态机：Pending → Parsing（解析）→ Indexing（切分 / embedding / 入库）→ Ready；
/// 任一阶段异常 → Failed（记录 ErrorMessage，可重试；重试按文档整体重建）。
/// 每阶段写 KbIngestEvent 流水；状态持久化 DB，进程重启由启动恢复扫描重新入队。
/// </summary>
public sealed class KbIngestWorker(
    InMemoryKbIngestQueue queue,
    IServiceScopeFactory scopeFactory,
    IEnumerable<IDocumentParser> parsers,
    IOptions<OcrOptions> ocrOptions,
    ILogger<KbIngestWorker> logger) : BackgroundService
{
    /// <summary>单文件大小上限（见技术方案 §7.5），超限直接判失败，防止 Worker 被拖死。</summary>
    private const long MaxFileSizeBytes = 50L * 1024 * 1024;

    private const string StageParse = "Parse";
    private const string StageOcr = "Ocr";
    private const string StageChunk = "Chunk";
    private const string StageEmbed = "Embed";
    private const string StagePersist = "Persist";

    private readonly IEnumerable<IDocumentParser> _parsers = parsers;
    private readonly OcrOptions _ocrOptions = ocrOptions.Value;
    private readonly ILogger<KbIngestWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("知识库导入流水线 Worker 已启动（解析→切分→embedding→入库）");
        await RecoverAsync(stoppingToken);

        await foreach (var documentId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunJobAsync(documentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // RunJobAsync 内部已兜底 Failed；此处仅防御 Store/框架层异常逃逸导致 Worker 退出
                _logger.LogError(ex, "导入任务未处理异常：文档 {DocumentId}", documentId);
            }
        }
    }

    /// <summary>启动恢复：内存队列重启即丢，扫描非终态文档重新入队（技术方案 §7.4）。</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseStore>();
            var unfinished = await store.ListUnfinishedDocumentsAsync(ct);
            foreach (var document in unfinished) queue.Enqueue(document.Id);
            if (unfinished.Count > 0)
                _logger.LogInformation("导入流水线启动恢复：重新入队 {Count} 个未完成文档", unfinished.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 恢复失败不阻断 Worker 启动（后续上传仍可正常入队）
            _logger.LogError(ex, "导入流水线启动恢复失败");
        }
    }

    private async Task RunJobAsync(string documentId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseStore>();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();
        var embedding = scope.ServiceProvider.GetRequiredService<IEmbeddingClient>();

        var document = await store.GetDocumentAsync(documentId, ct);
        if (document is null)
        {
            _logger.LogWarning("导入任务对应的文档不存在：{DocumentId}", documentId);
            return;
        }
        if (document.Status == KbDocumentStatus.Ready) return;

        var stage = StageParse;
        try
        {
            var knowledgeBase = await store.GetAsync(document.KnowledgeBaseId, ct)
                ?? throw new InvalidOperationException($"知识库不存在：{document.KnowledgeBaseId}");

            if (document.FileSizeBytes > MaxFileSizeBytes)
                throw new InvalidOperationException(
                    $"文件大小 {document.FileSizeBytes / 1024 / 1024}MB 超过上限 50MB");

            // ---- Parse ----
            document.Status = KbDocumentStatus.Parsing;
            document.ErrorMessage = null;
            await store.UpdateDocumentAsync(document, ct);
            await AddEventAsync(store, document.Id, stage, "running", null, ct);

            var extension = Path.GetExtension(document.OriginalFileName).ToLowerInvariant();
            var parser = _parsers.FirstOrDefault(p => p.CanParse(extension))
                ?? throw new InvalidOperationException($"不支持的文件格式：{extension}");

            ParsedDocument parsed;
            var source = await storage.OpenReadAsync(document.StorageKey, ct)
                ?? throw new InvalidOperationException($"原文件不存在：{document.StorageKey}");
            using (source)
            {
                parsed = await parser.ParseAsync(source, ct);
            }
            await AddEventAsync(store, document.Id, stage, "succeeded", $"解析 {parsed.Sections.Count} 个章节", ct);
            if (parsed.UsedOcr)
                await AddEventAsync(store, document.Id, StageOcr, "succeeded",
                    $"OCR 识别 {parsed.Sections.Count} 个章节（{_ocrOptions.Model}）", ct);

            // ---- Chunk ----
            document.Status = KbDocumentStatus.Indexing;
            await store.UpdateDocumentAsync(document, ct);

            stage = StageChunk;
            await AddEventAsync(store, document.Id, stage, "running", null, ct);
            var chunks = TextChunker.Split(parsed.Sections, knowledgeBase.ChunkSize, knowledgeBase.ChunkOverlap);
            if (chunks.Count == 0)
                throw new InvalidOperationException("未解析到有效文本（扫描版 PDF 或无文本层）");
            await AddEventAsync(store, document.Id, stage, "succeeded", $"切分 {chunks.Count} 段", ct);

            // ---- Embed ----
            stage = StageEmbed;
            await AddEventAsync(store, document.Id, stage, "running", null, ct);
            var vectors = await embedding.EmbedAsync(chunks.Select(c => c.Content).ToList(), ct);
            if (vectors.Count != chunks.Count)
                throw new InvalidOperationException($"向量数量 {vectors.Count} 与片段 {chunks.Count} 不一致");
            await AddEventAsync(store, document.Id, stage, "succeeded",
                $"生成 {vectors.Count} 个向量（{knowledgeBase.EmbeddingModel}）", ct);

            // ---- Persist：一期按文档整体重建（先删旧 chunk 再写新 chunk）----
            stage = StagePersist;
            await AddEventAsync(store, document.Id, stage, "running", null, ct);
            await store.DeleteChunksByDocumentAsync(document.Id, ct);

            var now = DateTimeOffset.UtcNow;
            var rows = chunks.Select((c, i) => new KbChunk
            {
                KnowledgeBaseId = document.KnowledgeBaseId,
                DocumentId = document.Id,
                Seq = c.Seq,
                Content = c.Content,
                CharCount = c.CharCount,
                HeadingPath = c.HeadingPath,
                PageNo = c.PageNo,
                Embedding = vectors[i],
                CreatedAt = now,
            }).ToList();
            await store.AddChunksAsync(rows, ct);

            document.Status = KbDocumentStatus.Ready;
            document.ChunkCount = rows.Count;
            document.IndexedAt = now;
            document.ErrorMessage = null;
            await store.UpdateDocumentAsync(document, ct);
            await AddEventAsync(store, document.Id, stage, "succeeded", $"写入 {rows.Count} 段", ct);

            _logger.LogInformation("知识库文档 {DocumentId} 索引完成：{Chunks} 段", document.Id, rows.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            document.Status = KbDocumentStatus.Failed;
            document.ErrorMessage = message;
            // 失败兜底不随请求/停机取消丢失（CancellationToken.None）
            await store.UpdateDocumentAsync(document, CancellationToken.None);
            await AddEventAsync(store, document.Id, stage, "failed", message, CancellationToken.None);
            _logger.LogWarning(ex, "知识库文档 {DocumentId} 在阶段 {Stage} 失败", document.Id, stage);
        }
    }

    private static async Task AddEventAsync(
        IKnowledgeBaseStore store, string documentId, string stage, string status,
        string? message, CancellationToken ct)
    {
        await store.AddEventAsync(new KbIngestEvent
        {
            DocumentId = documentId,
            Stage = stage,
            Status = status,
            Message = message,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }
}
