using System.Diagnostics;
using System.Text.Json;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Stores;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.Services;

/// <summary>流水线任务：从 StartStage（含）开始执行到 Ready。重试时 StartStage = FailedStage。</summary>
public sealed record VideoPipelineJob(string VideoResourceId, VideoProcessStatus StartStage);

/// <summary>进程内任务队列（上线替换为 Redis/RabbitMQ 消费，接口语义不变）。</summary>
public interface IVideoPipelineQueue
{
    void Enqueue(VideoPipelineJob job);
}

public sealed class InMemoryVideoPipelineQueue : IVideoPipelineQueue
{
    private readonly System.Threading.Channels.Channel<VideoPipelineJob> _channel =
        System.Threading.Channels.Channel.CreateUnbounded<VideoPipelineJob>(new()
        {
            SingleReader = true,
        });

    public void Enqueue(VideoPipelineJob job) => _channel.Writer.TryWrite(job);

    internal IAsyncEnumerable<VideoPipelineJob> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// 视频处理流水线 Worker（后台服务）。
/// 状态机（入戏 §3.1）：PROBING（真实 ffprobe）→ TRANSCODING → PACKAGING → SUBTITLING（模拟流转）
/// → ANALYZING（占位，深意生成后续由 Agent 服务接入）→ READY。
/// 任一阶段异常 → FAILED（记录 FailedStage/FailReason，支持从失败点重跑）。
/// 每阶段写 VideoStatusEvent 流水；状态持久化 DB，进程重启不丢。
/// </summary>
public sealed class VideoPipelineWorker(
    InMemoryVideoPipelineQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<MediaOptions> options,
    ILogger<VideoPipelineWorker> logger) : BackgroundService
{
    private static readonly VideoProcessStatus[] StageOrder =
    [
        VideoProcessStatus.Probing,
        VideoProcessStatus.Transcoding,
        VideoProcessStatus.Packaging,
        VideoProcessStatus.Subtitling,
        VideoProcessStatus.Analyzing,
    ];

    private readonly MediaOptions _options = options.Value;
    private readonly ILogger<VideoPipelineWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("视频流水线 Worker 已启动（探测=ffprobe 真实执行，转码/打包/字幕=模拟流转，深意=Agent 服务）");
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // RunJobAsync 内部已兜底 FAILED；此处仅防御 Store/框架层异常逃逸导致 Worker 退出
                _logger.LogError(ex, "流水线任务未处理异常：视频 {VideoId}", job.VideoResourceId);
            }
        }
    }

    private async Task RunJobAsync(VideoPipelineJob job, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var resources = scope.ServiceProvider.GetRequiredService<IVideoResourceStore>();
        var subs = scope.ServiceProvider.GetRequiredService<IVideoSubStore>();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();

        var video = await resources.GetByIdAsync(job.VideoResourceId, ct);
        if (video is null)
        {
            _logger.LogWarning("流水线任务对应的视频不存在：{VideoId}", job.VideoResourceId);
            return;
        }

        var startIdx = Array.IndexOf(StageOrder, job.StartStage);
        if (startIdx < 0) startIdx = 0;

        try
        {
            for (var i = startIdx; i < StageOrder.Length; i++)
            {
                var stage = StageOrder[i];
                if (stage >= VideoProcessStatus.Ready) break;

                video.ProcessStatus = stage;
                video.FailedStage = null;
                video.FailReason = null;
                await resources.UpdateAsync(video, ct);
                await AddEventAsync(subs, video.Id, stage, "running", ct);

                var message = stage switch
                {
                    VideoProcessStatus.Probing => await ProbeAsync(video, storage, ct),
                    VideoProcessStatus.Transcoding => await SimulateAsync(video, "转码切 HLS"),
                    VideoProcessStatus.Packaging => await SimulateAsync(video, "打包 master 清单"),
                    VideoProcessStatus.Subtitling => await SimulateAsync(video, "字幕归一化"),
                    VideoProcessStatus.Analyzing => await AnalyzeAsync(),
                    _ => throw new InvalidOperationException($"未知阶段：{stage}"),
                };

                await AddEventAsync(subs, video.Id, stage, "succeeded", ct, message);
                _logger.LogInformation("视频 {VideoId} 阶段 {Stage} 完成：{Message}", video.Id, stage, message);
            }

            video.ProcessStatus = VideoProcessStatus.Ready;
            video.FailedStage = null;
            video.FailReason = null;
            await resources.UpdateAsync(video, ct);
            await AddEventAsync(subs, video.Id, VideoProcessStatus.Ready, "succeeded", ct, "资源就绪，可上架/可预约");
            _logger.LogInformation("视频 {VideoId} 流水线全部完成，状态 READY", video.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failedAt = video.ProcessStatus is >= VideoProcessStatus.Probing and <= VideoProcessStatus.Analyzing
                ? video.ProcessStatus
                : VideoProcessStatus.Probing;
            video.ProcessStatus = VideoProcessStatus.Failed;
            video.FailedStage = failedAt;
            video.FailReason = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
            await resources.UpdateAsync(video, ct);
            await AddEventAsync(subs, video.Id, failedAt, "failed", ct, video.FailReason);
            _logger.LogWarning(ex, "视频 {VideoId} 在阶段 {Stage} 失败", video.Id, failedAt);
        }
    }

    // ===== 阶段实现 =====

    /// <summary>PROBING：真实执行 ffprobe，回填时长/分辨率。</summary>
    private async Task<string> ProbeAsync(VideoResource video, IStorageService storage, CancellationToken ct)
    {
        var path = storage.GetPhysicalPath(video.StorageKey)
            ?? throw new InvalidOperationException($"原始文件不存在：{video.StorageKey}");

        var psi = new ProcessStartInfo
        {
            FileName = _options.FFprobePath,
            Arguments = $"-v error -print_format json -show_format -show_streams \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 ffprobe 进程");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe 退出码 {proc.ExitCode}：{Truncate(stderr)}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        if (root.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationEl)
            && double.TryParse(durationEl.GetString(), out var seconds))
        {
            video.DurationMs = (int)Math.Round(seconds * 1000);
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (s.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                {
                    if (s.TryGetProperty("width", out var w)) video.Width = w.GetInt32();
                    if (s.TryGetProperty("height", out var h)) video.Height = h.GetInt32();
                    break;
                }
            }
        }

        return $"时长 {FormatDuration(video.DurationMs)}，分辨率 {video.Width}×{video.Height}";
    }

    /// <summary>模拟阶段：延迟后返回描述（上线由 FFmpeg Worker 替代）。</summary>
    private async Task<string> SimulateAsync(VideoResource video, string what)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.SimulatedStageSeconds)));
        return $"{what}（模拟流转，{_options.SimulatedStageSeconds}s）";
    }

    /// <summary>
    /// ANALYZING：深意生成占位。六维深意数据由 Agent 服务生成（当前决策：暂不接入）；
    /// 接入时改为调用 Agent 服务并回填 video.DeepMeaningKey。
    /// </summary>
    private async Task<string> AnalyzeAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.SimulatedStageSeconds)));
        return "深意分析占位（Agent 服务接入后启用）";
    }

    // ===== 辅助 =====

    private static async Task AddEventAsync(
        IVideoSubStore subs, string videoId, VideoProcessStatus stage, string status, CancellationToken ct,
        string? message = null)
    {
        await subs.AddEventAsync(new VideoStatusEvent
        {
            VideoResourceId = videoId,
            Stage = stage,
            Status = status,
            Message = message,
        }, ct);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static string FormatDuration(int? durationMs)
    {
        if (durationMs is not { } ms) return "未知";
        var t = TimeSpan.FromMilliseconds(ms);
        return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
    }
}
