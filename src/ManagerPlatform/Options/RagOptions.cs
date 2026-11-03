namespace ManagerPlatform.Options;

/// <summary>
/// RAG 知识库配置（appsettings.json "Rag" 节，
/// 可用环境变量 Rag__EmbeddingApiKey 等覆盖）。
/// 一期：dense 向量检索（pgvector），不做 rerank / hybrid。
/// </summary>
public sealed class RagOptions
{
    public const string SectionName = "Rag";

    /// <summary>embedding 服务地址（OpenAI 兼容）。如 SiliconFlow：https://api.siliconflow.cn/v1</summary>
    public string EmbeddingBaseUrl { get; set; } = "https://api.siliconflow.cn/v1";

    /// <summary>embedding 服务 API Key。本地 TEI 无鉴权可留空；云端服务（如 SiliconFlow）必填。</summary>
    public string EmbeddingApiKey { get; set; } = string.Empty;

    /// <summary>embedding 模型名。如 BAAI/bge-m3（维度 1024）。</summary>
    public string EmbeddingModel { get; set; } = "BAAI/bge-m3";

    /// <summary>embedding 向量维度，须与 <see cref="EmbeddingModel"/> 一致，也是 kb_chunks 定长列的唯一来源。</summary>
    public int EmbeddingDim { get; set; } = 1024;

    /// <summary>单次批量 embedding 的文本条数。</summary>
    public int EmbeddingBatchSize { get; set; } = 32;

    /// <summary>embedding HTTP 超时（秒）。</summary>
    public int EmbeddingTimeoutSeconds { get; set; } = 30;

    /// <summary>默认返回 top-k 片段。</summary>
    public int DefaultTopK { get; set; } = 3;

    /// <summary>top-k 上限。</summary>
    public int MaxTopK { get; set; } = 5;

    /// <summary>相似度阈值（余弦），低于此值视为未命中。</summary>
    public double MinScore { get; set; } = 0.3;

    /// <summary>切分单段字符数上限。</summary>
    public int ChunkSize { get; set; } = 500;

    /// <summary>切分重叠字符数（须小于 <see cref="ChunkSize"/>）。</summary>
    public int ChunkOverlap { get; set; } = 80;

    // ===== 二期能力开关（P3 rerank / P4 hybrid；实现前保持关闭，行为与一期一致）=====

    /// <summary>是否启用 rerank 精排（P3）。</summary>
    public bool EnableRerank { get; set; } = false;

    /// <summary>rerank 服务地址（OpenAI/Cohere 兼容，硅基流动为 https://api.siliconflow.cn/v1）。</summary>
    public string RerankBaseUrl { get; set; } = "https://api.siliconflow.cn/v1";

    /// <summary>rerank API Key（与 OCR 共用硅基流动 Key；为空时跳过调用并回退原始顺序）。</summary>
    public string RerankApiKey { get; set; } = string.Empty;

    /// <summary>rerank 模型名。</summary>
    public string RerankModel { get; set; } = "BAAI/bge-reranker-v2-m3";

    /// <summary>送入 rerank 的候选片段数上限（须大于最终 top-k 才有意义）。</summary>
    public int RerankCandidateTopN { get; set; } = 20;

    /// <summary>rerank HTTP 超时（秒）；超时即回退原始顺序，不让检索失败。</summary>
    public int RerankTimeoutSeconds { get; set; } = 20;

    /// <summary>是否启用 dense + sparse 混合召回（P4）。</summary>
    public bool EnableHybrid { get; set; } = false;
}
