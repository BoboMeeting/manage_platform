namespace ManagerPlatform.Options;

/// <summary>
/// 媒体处理配置（appsettings.json "Media" 节）。
/// 本期形态：本地磁盘存储 + ffprobe 真实探测 + 转码/打包/字幕阶段模拟流转（深意 ANALYZING 为占位）；
/// 上线替换：StorageRoot → 对象存储实现，转码阶段由独立 FFmpeg Worker 实现同一流水线接口，
/// 深意生成由 Agent 服务接入并回填 DeepMeaningKey，业务代码零改动。
/// </summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>本地存储根目录（相对路径基于 ContentRoot，也可用绝对路径）</summary>
    public string StorageRoot { get; set; } = "storage";

    /// <summary>ffprobe 可执行文件路径（默认取 PATH 中的 ffprobe）</summary>
    public string FFprobePath { get; set; } = "ffprobe";

    /// <summary>模拟流转/占位阶段的每阶段时长（秒），用于前端联调</summary>
    public int SimulatedStageSeconds { get; set; } = 3;
}
