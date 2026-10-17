using System.Diagnostics;
using System.Text;
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
/// 状态机（入戏 §3.1）：PROBING（ffprobe 探测）→ TRANSCODING（原分辨率 HLS 流复制切片，不做转码）
/// → PACKAGING（生成 master 清单并回填 HlsMasterKey）→ SUBTITLING（内嵌字幕抽取并归一化 VTT）
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
        _logger.LogInformation("视频流水线 Worker 已启动（探测/切片/打包=真实 ffmpeg（原分辨率流复制），字幕=内嵌字幕抽取归一化，深意=Agent 服务）");
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
                    VideoProcessStatus.Transcoding => await SliceAsync(video, storage, ct),
                    VideoProcessStatus.Packaging => await PackageAsync(video, storage, ct),
                    VideoProcessStatus.Subtitling => await SubtitleAsync(video, storage, subs, ct),
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

    // ===== SUBTITLING：内嵌字幕抽取与归一化 =====

    /// <summary>文本类字幕编码（可 ffmpeg 转 SRT 后归一化）；其余（PGS/DVB 等位图/专有格式）跳过。</summary>
    private static readonly HashSet<string> TextSubtitleCodecs = ["subrip", "ass", "ssa", "mov_text", "webvtt"];

    private sealed record SubtitleStreamProbe(int Index, string Codec, string? Language, string? Title, bool Default);

    /// <summary>
    /// SUBTITLING：字幕归一化。探测源文件内嵌字幕流，文本类（SRT/ASS/mov_text/WebVTT）逐路
    /// ffmpeg 抽取为 SRT，经 SubtitleConverter 归一化为 WebVTT 入库（Source=Extracted）；
    /// 位图/专有字幕流无法转文本，跳过并计数；源无字幕流则直接通过。
    /// 单路抽取失败不阻断整体；重跑前清理上次抽取的轨（管理员上传的字幕不受影响）。
    /// </summary>
    private async Task<string> SubtitleAsync(
        VideoResource video, IStorageService storage, IVideoSubStore subs, CancellationToken ct)
    {
        var srcPath = storage.GetPhysicalPath(video.StorageKey)
            ?? throw new InvalidOperationException($"原始文件不存在：{video.StorageKey}");

        // 重跑清理：仅移除上次抽取的轨，管理员上传的字幕保留
        var existing = await subs.GetTracksAsync(video.Id, ct);
        foreach (var old in existing.Where(t => t.Source == SubtitleSource.Extracted))
        {
            await storage.DeleteAsync(old.StorageKey, ct);
            await subs.DeleteTrackAsync(video.Id, old.Id, ct);
        }
        var hasUploadedDefault = existing.Any(t => t.IsDefault && t.Source == SubtitleSource.Uploaded);

        var streams = await ProbeSubtitleStreamsAsync(srcPath, ct);
        if (streams.Count == 0)
            return "源无内嵌字幕流，跳过（可上传外挂字幕补充）";

        var extracted = new List<(SubtitleTrack Track, bool Default)>();
        var skipped = 0;
        var failed = 0;
        foreach (var s in streams)
        {
            if (!TextSubtitleCodecs.Contains(s.Codec))
            {
                skipped++;
                continue;
            }

            try
            {
                var srt = await ExtractSubtitleSrtAsync(srcPath, s.Index, ct);
                var (vtt, cueCount) = SubtitleConverter.SrtToVtt(srt);
                if (cueCount == 0)
                {
                    skipped++;
                    continue;
                }

                var track = new SubtitleTrack
                {
                    VideoResourceId = video.Id,
                    Language = MapLanguage(s.Language),
                    Label = s.Title,
                    Source = SubtitleSource.Extracted,
                    StorageKey = $"videos/{video.Id}/subtitles/{Guid.NewGuid():N}.vtt",
                    CueCount = cueCount,
                    OriginalFileName = $"{Path.GetFileNameWithoutExtension(video.OriginalFileName)}.0:{s.Index}.srt",
                };
                await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(vtt));
                await storage.SaveAsync(track.StorageKey, ms, ct);
                extracted.Add((track, s.Default));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "视频 {VideoId} 内嵌字幕流 0:{Index}（{Codec}）抽取失败",
                    video.Id, s.Index, s.Codec);
            }
        }

        // 默认轨：优先流 disposition.default 标记；单路无标记时即默认；不抢占已上传的默认轨
        var defaultIdx = extracted.FindIndex(e => e.Default);
        if (defaultIdx < 0 && extracted.Count == 1) defaultIdx = 0;
        if (defaultIdx >= 0 && !hasUploadedDefault)
            extracted[defaultIdx].Track.IsDefault = true;

        foreach (var (track, _) in extracted)
            await subs.AddTrackAsync(track, ct);

        if (extracted.Count == 0 && failed == 0)
            return $"内嵌字幕 {skipped} 路均为位图/空轨，无法归一化，跳过";

        var parts = new List<string>();
        if (extracted.Count > 0) parts.Add($"抽取内嵌字幕 {extracted.Count} 路并归一化 VTT 入库");
        if (failed > 0) parts.Add($"{failed} 路抽取失败");
        if (skipped > 0) parts.Add($"跳过 {skipped} 路（位图/空轨）");
        return string.Join("，", parts);
    }

    /// <summary>探测源文件内嵌字幕流（绝对流序号/编码/语言/标题/默认标记）。</summary>
    private async Task<List<SubtitleStreamProbe>> ProbeSubtitleStreamsAsync(string srcPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FFprobePath,
            Arguments = $"-v error -print_format json -select_streams s -show_streams \"{srcPath.Replace('\\', '/')}\"",
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

        var result = new List<SubtitleStreamProbe>();
        using var doc = JsonDocument.Parse(stdout);
        if (!doc.RootElement.TryGetProperty("streams", out var streams)) return result;

        foreach (var s in streams.EnumerateArray())
        {
            var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
            if (string.IsNullOrEmpty(codec)) continue;

            string? lang = null, title = null;
            if (s.TryGetProperty("tags", out var tags))
            {
                if (tags.TryGetProperty("language", out var l)) lang = l.GetString();
                if (tags.TryGetProperty("title", out var t)) title = t.GetString();
            }
            var isDefault = s.TryGetProperty("disposition", out var disp)
                && disp.TryGetProperty("default", out var d) && d.GetInt32() == 1;

            result.Add(new SubtitleStreamProbe(
                s.TryGetProperty("index", out var idx) ? idx.GetInt32() : -1,
                codec, NullIfEmpty(lang), NullIfEmpty(title), isDefault));
        }
        return result;
    }

    /// <summary>抽取单路内嵌字幕为 SRT 文本（ffmpeg 转码，顺带统一 UTF-8）；临时文件用后即删。</summary>
    private async Task<string> ExtractSubtitleSrtAsync(string srcPath, int streamIndex, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sub_{Guid.NewGuid():N}.srt");
        try
        {
            await RunFFmpegAsync(
                $"-nostdin -y -hide_banner -loglevel error -i \"{srcPath.Replace('\\', '/')}\" " +
                $"-map 0:{streamIndex} -c:s srt \"{tmp.Replace('\\', '/')}\"", ct);
            return await File.ReadAllTextAsync(tmp, Encoding.UTF8, ct);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>ffprobe 语言标签（ISO 639-2/1）→ 简化 BCP-47 主子标签；缺失记为 und。</summary>
    private static string MapLanguage(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "und";
        var lang = raw.Trim().ToLowerInvariant();
        return lang switch
        {
            "chi" or "zho" => "zh",
            "eng" => "en",
            "jpn" => "ja",
            "kor" => "ko",
            "fre" or "fra" => "fr",
            "ger" or "deu" => "de",
            "rus" => "ru",
            "spa" => "es",
            _ => lang,
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// TRANSCODING：原分辨率 HLS 切片（流复制封装，不解码不重编码，速度近似磁盘 I/O）。
    /// 仅要求源编码为 TS/浏览器兼容（H.264+AAC 最稳；HEVC/AC3 等源不转码则不会纠正）；
    /// 分片边界由源关键帧决定（-hls_time 6 为下限），时长不均匀属预期。
    /// 重跑前清空旧分片目录。
    /// </summary>
    private async Task<string> SliceAsync(VideoResource video, IStorageService storage, CancellationToken ct)
    {
        var srcPath = storage.GetPhysicalPath(video.StorageKey)
            ?? throw new InvalidOperationException($"原始文件不存在：{video.StorageKey}");
        var hlsDir = Path.Combine(Path.GetDirectoryName(srcPath)!, "hls");

        // 清理旧分片（重试场景）
        if (Directory.Exists(hlsDir)) Directory.Delete(hlsDir, recursive: true);
        Directory.CreateDirectory(hlsDir);

        await RunFFmpegAsync(BuildSliceArgs(srcPath, hlsDir), ct);

        if (!File.Exists(Path.Combine(hlsDir, "index.m3u8")))
            throw new InvalidOperationException("切片输出缺失：hls/index.m3u8");

        var segments = Directory.EnumerateFiles(hlsDir, "*.ts", SearchOption.TopDirectoryOnly).Count();
        return $"HLS 原分辨率流复制切片，共 {segments} 个分片";
    }

    /// <summary>
    /// PACKAGING：生成 master 清单写入存储并回填 HlsMasterKey。
    /// 流复制无码率目标，BANDWIDTH 按 media 清单中分片字节数/时长实测计算。
    /// </summary>
    private async Task<string> PackageAsync(VideoResource video, IStorageService storage, CancellationToken ct)
    {
        var srcPath = storage.GetPhysicalPath(video.StorageKey)
            ?? throw new InvalidOperationException($"原始文件不存在：{video.StorageKey}");
        var hlsDir = Path.Combine(Path.GetDirectoryName(srcPath)!, "hls");
        var mediaPath = Path.Combine(hlsDir, "index.m3u8");
        if (!File.Exists(mediaPath))
            throw new InvalidOperationException($"媒体清单缺失：videos/{video.Id}/hls/index.m3u8");

        var (bytes, seconds) = SumPlaylist(mediaPath);
        var bandwidth = seconds > 0 ? Math.Max((long)(bytes * 8 / seconds), 1) : 1;

        var masterKey = $"videos/{video.Id}/hls/master.m3u8";
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n");
        sb.Append("#EXT-X-STREAM-INF:BANDWIDTH=").Append(bandwidth);
        if (video.Width is { } w && video.Height is { } h && w > 0 && h > 0)
            sb.Append(",RESOLUTION=").Append(w).Append('x').Append(h);
        sb.Append("\nindex.m3u8\n");

        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        await storage.SaveAsync(masterKey, ms, ct);
        video.HlsMasterKey = masterKey;
        return $"master 清单打包完成（原分辨率单档，实测码率 {bandwidth / 1000} kbps）";
    }

    private static string BuildSliceArgs(string srcPath, string hlsDir)
    {
        var dir = hlsDir.Replace('\\', '/');
        var sb = new StringBuilder("-nostdin -y -hide_banner -loglevel error");
        sb.Append(" -i \"").Append(srcPath.Replace('\\', '/')).Append('"');
        sb.Append(" -map 0:v:0 -map 0:a:0? -c copy");
        sb.Append(" -f hls -hls_time 6 -hls_playlist_type vod -hls_list_size 0");
        sb.Append(" -hls_flags independent_segments -hls_segment_type mpegts");
        sb.Append(" -hls_segment_filename \"").Append(dir).Append("/seg_%05d.ts\"");
        sb.Append(" \"").Append(dir).Append("/index.m3u8\"");
        return sb.ToString();
    }

    /// <summary>统计 media 清单声明的总时长与分片总字节（用于实测码率）。</summary>
    private static (long Bytes, double Seconds) SumPlaylist(string playlistPath)
    {
        long bytes = 0;
        double seconds = 0;
        var dir = Path.GetDirectoryName(playlistPath)!;
        foreach (var line in File.ReadLines(playlistPath))
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var v = line["#EXTINF:".Length..];
                var comma = v.IndexOf(',');
                if (comma >= 0) v = v[..comma];
                if (double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    seconds += d;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                var seg = Path.Combine(dir, line);
                if (File.Exists(seg)) bytes += new FileInfo(seg).Length;
            }
        }
        return (bytes, seconds);
    }

    /// <summary>执行 ffmpeg；取消时终止整个进程树，失败抛出含 stderr 摘要的异常。</summary>
    private async Task RunFFmpegAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FFmpegPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 ffmpeg 进程");
        using var _ = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 进程已退出 */ }
        });
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg 退出码 {proc.ExitCode}：{Truncate(await stderrTask)}");
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
