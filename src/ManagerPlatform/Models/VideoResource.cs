namespace ManagerPlatform.Models;

/// <summary>
/// 视频处理状态机（对齐入戏技术方案 §3.1）：
/// Uploading → Probing → Transcoding → Packaging → Subtitling → Analyzing → Ready，
/// 任一环节失败进入 Failed（记录 FailedStage，支持从失败点重跑）。
/// </summary>
public enum VideoProcessStatus
{
    /// <summary>上传中（文件尚未完整落盘）</summary>
    Uploading = 0,
    /// <summary>探测中（ffprobe 读取时长/分辨率）</summary>
    Probing = 1,
    /// <summary>转码中（HLS 切片，本期模拟）</summary>
    Transcoding = 2,
    /// <summary>打包中（多码率 master 清单，本期模拟）</summary>
    Packaging = 3,
    /// <summary>字幕处理中（归一化 VTT，本期模拟）</summary>
    Subtitling = 4,
    /// <summary>深意分析中（Agent 服务 LLM 生成，未配置时跳过）</summary>
    Analyzing = 5,
    /// <summary>就绪（可上架、可被会议预约绑定）</summary>
    Ready = 6,
    /// <summary>失败（可重试）</summary>
    Failed = 7,
}

/// <summary>上下架状态。仅 Ready 资源允许上架；可被会议绑定的条件为 Ready 且 OnShelf。</summary>
public enum ShelfStatus
{
    OffShelf = 0,
    OnShelf = 1,
}

/// <summary>字幕来源：管理端上传 / 视频内嵌抽取。</summary>
public enum SubtitleSource
{
    Uploaded = 0,
    Extracted = 1,
}

/// <summary>
/// 视频资源（入戏 §7 内容模型 VideoResource 的媒体部分）。
/// 字幕轨与深意数据关联实体见 SubtitleTrack；深意 JSON 由 Agent 服务生成后写存储，
/// 通过 DeepMeaningKey 引用。
/// </summary>
public sealed class VideoResource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>原始文件名（含扩展名）</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>原始文件大小（字节）</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>时长（毫秒），探测后回填</summary>
    public int? DurationMs { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>原始文件存储键</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>HLS master 清单存储键（真实转码上线后回填；本期为 null，预览走原始文件流）</summary>
    public string? HlsMasterKey { get; set; }

    /// <summary>深意 JSON（deepMeaning.json）存储键，由 Agent 服务生成后回填</summary>
    public string? DeepMeaningKey { get; set; }

    public VideoProcessStatus ProcessStatus { get; set; } = VideoProcessStatus.Uploading;

    public ShelfStatus ShelfStatus { get; set; } = ShelfStatus.OffShelf;

    /// <summary>失败原因（ProcessStatus = Failed 时展示）</summary>
    public string? FailReason { get; set; }

    /// <summary>失败发生的阶段（重试时从该阶段重跑）</summary>
    public VideoProcessStatus? FailedStage { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>字幕轨（多语言）。存储统一归一化为 WebVTT。</summary>
public sealed class SubtitleTrack
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string VideoResourceId { get; set; } = string.Empty;

    /// <summary>语言标签（BCP-47，如 zh-CN / en）</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>展示名（如"简体中文"），缺省用 Language</summary>
    public string? Label { get; set; }

    public bool IsDefault { get; set; }

    /// <summary>来源：上传 / 内嵌抽取</summary>
    public SubtitleSource Source { get; set; } = SubtitleSource.Uploaded;

    /// <summary>归一化后 VTT 的存储键</summary>
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>条目（cue）数量，上传解析时统计</summary>
    public int CueCount { get; set; }

    /// <summary>原始文件名（含扩展名）</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>处理流水事件（状态时间线，供管理端详情展示与故障排查）。</summary>
public sealed class VideoStatusEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string VideoResourceId { get; set; } = string.Empty;

    public VideoProcessStatus Stage { get; set; }

    /// <summary>running / succeeded / failed</summary>
    public string Status { get; set; } = "running";

    public string? Message { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
