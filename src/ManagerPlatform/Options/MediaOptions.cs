namespace ManagerPlatform.Options;

/// <summary>
/// 媒体处理配置（appsettings.json "Media" 节）。
/// 本期形态：本地磁盘存储 + ffprobe 真实探测 + ffmpeg 原分辨率流复制切片/打包（HLS 单档，不转码）
/// + 字幕阶段内嵌字幕抽取归一化 VTT（ANALYZING 深意为占位）；
/// 上线替换：StorageRoot → 对象存储实现，深意生成由 Agent 服务接入并回填 DeepMeaningKey，
/// 业务代码零改动。
/// </summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>本地存储根目录（相对路径基于 ContentRoot，也可用绝对路径）</summary>
    public string StorageRoot { get; set; } = "storage";

    /// <summary>ffprobe 可执行文件路径（默认取 PATH 中的 ffprobe）</summary>
    public string FFprobePath { get; set; } = "ffprobe";

    /// <summary>ffmpeg 可执行文件路径（默认取 PATH 中的 ffmpeg，用于 HLS 切片）</summary>
    public string FFmpegPath { get; set; } = "ffmpeg";

    /// <summary>占位阶段（深意分析）的每阶段时长（秒），用于前端联调</summary>
    public int SimulatedStageSeconds { get; set; } = 3;

    /// <summary>
    /// HLS 点播对外基址（nginx 静态服务，如 http://host:8088/media），
    /// play-url 返回 {PublicBaseUrl}/{HlsMasterKey}；未配置时回退为后端签名流式预览原始文件。
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}
