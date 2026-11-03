using NpgsqlTypes;
using Pgvector;

namespace ManagerPlatform.Models;

/// <summary>知识库状态：启用 / 停用（停用后不参与检索）。</summary>
public enum KbStatus
{
    Enabled = 0,
    Disabled = 1,
}

/// <summary>
/// 文档索引进度状态机：Pending → Parsing → Indexing → Ready，
/// 任一步失败进入 Failed（可重试）。
/// </summary>
public enum KbDocumentStatus
{
    Pending = 0,
    Parsing = 1,
    Indexing = 2,
    Ready = 3,
    Failed = 4,
}

/// <summary>
/// 知识库（一期全局共享，不按 AI 角色隔离）。
/// 硬约束：全局只允许一种 embedding 维度（见 <see cref="KbChunk.EmbeddingDimension"/>），换模型需全量重嵌。
/// </summary>
public sealed class KnowledgeBase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>embedding 模型名（如 BAAI/bge-m3），用于换模型时识别需重嵌的数据。</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>向量维度，须与全局 <see cref="KbChunk.EmbeddingDimension"/> 一致。</summary>
    public int EmbeddingDim { get; set; } = KbChunk.EmbeddingDimension;

    /// <summary>切分单段字符数上限。</summary>
    public int ChunkSize { get; set; } = 500;

    /// <summary>切分重叠字符数（须小于 <see cref="ChunkSize"/>）。</summary>
    public int ChunkOverlap { get; set; } = 80;

    public KbStatus Status { get; set; } = KbStatus.Enabled;

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>知识库文档（原文件 + 处理状态）。</summary>
public sealed class KbDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string KnowledgeBaseId { get; set; } = string.Empty;

    /// <summary>展示标题，缺省取原始文件名。</summary>
    public string Title { get; set; } = string.Empty;

    public string OriginalFileName { get; set; } = string.Empty;

    public string? ContentType { get; set; }

    public long FileSizeBytes { get; set; }

    /// <summary>原文件存储键（kb/{kbId}/{docId}/{fileName}）。</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>sha256，用于同库内重复导入检测。</summary>
    public string? Checksum { get; set; }

    public KbDocumentStatus Status { get; set; } = KbDocumentStatus.Pending;

    /// <summary>失败原因（Status = Failed 时展示）。</summary>
    public string? ErrorMessage { get; set; }

    public int ChunkCount { get; set; }

    /// <summary>索引完成时间。</summary>
    public DateTimeOffset? IndexedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>文档片段（检索基本单位）。</summary>
public sealed class KbChunk
{
    /// <summary>
    /// 全局唯一 embedding 维度：同一张表的向量列必须定长。
    /// 启动时由 Program.cs 按 Rag:EmbeddingDim 注入（在建 DbContext 模型之前）；
    /// 换模型/维度 = 重建 kb_chunks，不能热切换。
    /// </summary>
    public static int EmbeddingDimension { get; set; } = 1024;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>冗余列：避免检索过滤时 join 文档表。</summary>
    public string KnowledgeBaseId { get; set; } = string.Empty;

    public string DocumentId { get; set; } = string.Empty;

    /// <summary>文档内序号，从 0 开始。</summary>
    public int Seq { get; set; }

    public string Content { get; set; } = string.Empty;

    public int CharCount { get; set; }

    /// <summary>章节路径（如 "第三章 &gt; 3.2 接口设计"），用于引用展示。</summary>
    public string? HeadingPath { get; set; }

    /// <summary>PDF 页码（1 起），其他格式为空。</summary>
    public int? PageNo { get; set; }

    /// <summary>向量（写入前必须赋值，不允许为空）。</summary>
    public Vector Embedding { get; set; } = null!;

    /// <summary>bigram 分词后的 tsvector（P4 hybrid 稀疏检索；写入前赋值，不允许为空）。</summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    /// <summary>sha256(content) 小写 hex（P5 增量索引用；P4 阶段也一并写入，后续增量直接启用）。</summary>
    public string ContentHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>导入流水事件（处理时间线，供详情展示与排查）。</summary>
public sealed class KbIngestEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DocumentId { get; set; } = string.Empty;

    /// <summary>Parse / Ocr / Chunk / Embed / Persist（Ocr 仅扫描版 PDF 触发）。</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>running / succeeded / failed。</summary>
    public string Status { get; set; } = string.Empty;

    public string? Message { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>检索日志（评测与审计数据来源）。</summary>
public sealed class RetrievalLog
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>跨库检索时为空。</summary>
    public string? KnowledgeBaseId { get; set; }

    public string Query { get; set; } = string.Empty;

    public int TopK { get; set; }

    public int HitCount { get; set; }

    public float? TopScore { get; set; }

    public int ElapsedMs { get; set; }

    /// <summary>调用方：mcp / admin-debug。</summary>
    public string Caller { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
