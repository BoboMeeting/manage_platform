using System.Security.Cryptography;
using System.Text;
using ManagerPlatform.Auth;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Services;
using ManagerPlatform.Stores;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ManagerPlatform.Endpoints;

/// <summary>
/// 视频资源管理（入戏 §5.1 管理端：资源库列表/搜索/状态/上下架，上传转码 HLS，多语言字幕）。
/// 策略：组级 OperatorPlus；播放流走短时效 HMAC 签名 URL（非 JWT），对齐"客户端不持有长期密钥"约束。
/// </summary>
public static class VideoEndpoints
{
    /// <summary>允许上传的视频容器格式（入戏 §5.1）</summary>
    private static readonly string[] AllowedVideoExtensions = [".mp4", ".mkv", ".mov"];

    private static readonly string[] AllowedSubtitleExtensions = [".srt", ".vtt", ".ass", ".ssa"];

    public static IEndpointRouteBuilder MapVideoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/videos").WithTags("Videos")
            .RequireAuthorization(Policies.OperatorPlus);

        // ===== 资源库 =====

        group.MapGet("/", async (
            string? keyword,
            VideoProcessStatus? status,
            int page,
            int pageSize,
            HttpContext ctx,
            IVideoResourceStore store,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            page = Math.Max(1, page <= 0 ? 1 : page);
            pageSize = Math.Clamp(pageSize <= 0 ? 20 : pageSize, 1, 100);
            var (items, total) = await store.FindAsync(keyword, status, page, pageSize, ct);
            return Results.Ok(new PagedResult<VideoResource>(items, total, page, pageSize));
        });

        // 上传视频：multipart 单请求完成落盘 → 建记录（Uploading）→ 投递流水线（Probing 起）
        group.MapPost("/", async (
            IFormFile file,
            string? title,
            string? description,
            HttpContext ctx,
            IVideoResourceStore store,
            IStorageService storage,
            IVideoPipelineQueue pipeline,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
             
             Console.WriteLine($"1 上传视频：{file.FileName}，大小 {file.Length} 字节");



            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            if (file.Length == 0)
                return Results.BadRequest(new { error = "视频文件为空" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedVideoExtensions.Contains(ext))
                return Results.BadRequest(new { error = $"不支持的格式 {ext}（允许 {string.Join("/", AllowedVideoExtensions)}）" });

            // 资源树统一以 video.Id 为目录标识，保证 original/hls/subtitles/深意在同一目录，
            // 下游 PackageAsync（master.m3u8 用 video.Id 拼接）与删除端点（videos/{id}/hls）保持一致
            var videoId = Guid.NewGuid().ToString("N");
            var video = new VideoResource
            {
                Id = videoId,
                Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(file.FileName) : title.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                OriginalFileName = Path.GetFileName(file.FileName),
                FileSizeBytes = file.Length,
                StorageKey = $"videos/{videoId}/original{ext}",
                CreatedBy = cu.UserId,
            };

             Console.WriteLine($"2 上传视频：{video.Title}，大小 {video.FileSizeBytes} 字节");


            await using var stream = file.OpenReadStream();
            video.StorageKey = await storage.SaveAsync(video.StorageKey, stream, ct);
            await store.AddAsync(video, ct);
            pipeline.Enqueue(new VideoPipelineJob(video.Id, VideoProcessStatus.Probing));

             Console.WriteLine($"3 上传视频：{video.Title}，大小 {video.FileSizeBytes} 字节");


            logger.LogInformation("审计[上传视频]：操作人={Operator}，资源={Id}，文件={File}，大小={Bytes}",
                cu.UserId, video.Id, video.OriginalFileName, video.FileSizeBytes);


            return Results.Created($"/api/admin/videos/{video.Id}", video);

        }).DisableAntiforgery();

        // 详情：资源 + 字幕轨 + 流水事件
        group.MapGet("/{id}", async (
            string id,
            HttpContext ctx,
            IVideoResourceStore store,
            IVideoSubStore subs,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            var video = await store.GetByIdAsync(id, ct);
            if (video is null) return Results.NotFound(new { error = "视频资源不存在" });
            return Results.Ok(new VideoDetail(
                video,
                await subs.GetTracksAsync(id, ct),
                await subs.GetEventsAsync(id, ct)));
        });

        // 删除：活跃处理阶段不允许；就绪/失败/SUBTITLING等待态可删
        group.MapDelete("/{id}", async (
            string id,
            HttpContext ctx,
            IVideoResourceStore store,
            IVideoSubStore subs,
            IStorageService storage,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var video = await store.GetByIdAsync(id, ct);
            if (video is null) return Results.NotFound(new { error = "视频资源不存在" });
            // 删除：仅活跃处理阶段（Probing~Analyzing）不允许；就绪/失败/SUBTITLING等待态可删
            if (video.ProcessStatus is not (VideoProcessStatus.Ready or VideoProcessStatus.Failed or VideoProcessStatus.Subtitling))
                return Results.Conflict(new { error = "资源处理中，不可删除" });

            // 删除关联文件与从属记录（存储删除尽力而为，不阻断主流程）
            // HLS 整目录（各码率分片 + master 清单）单独清理，其余按键删
            await storage.DeleteDirectoryAsync($"videos/{id}/hls", ct);
            var keys = new List<string> { video.StorageKey, video.DeepMeaningKey ?? "" };
            keys.AddRange((await subs.GetTracksAsync(id, ct)).Select(t => t.StorageKey));
            foreach (var key in keys.Where(k => k.Length > 0))
                await storage.DeleteAsync(key, ct);

            await subs.DeleteEventsAsync(id, ct);
            await store.DeleteAsync(id, ct);
            logger.LogInformation("审计[删除视频]：操作人={Operator}，资源={Id}，标题={Title}", cu.UserId, id, video.Title);
            return Results.Ok(new { ok = true });
        });

        // ===== 上下架（仅 Ready 可上架）=====

        group.MapPost("/{id}/publish", async (
            string id, HttpContext ctx, IVideoResourceStore store, ILogger<ApiLog> logger, CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var video = await store.GetByIdAsync(id, ct);
            if (video is null) return Results.NotFound(new { error = "视频资源不存在" });
            if (video.ProcessStatus != VideoProcessStatus.Ready)
                return Results.Conflict(new { error = "仅就绪（READY）资源可上架" });

            video.ShelfStatus = ShelfStatus.OnShelf;
            await store.UpdateAsync(video, ct);
            logger.LogInformation("审计[上架视频]：操作人={Operator}，资源={Id}", cu.UserId, id);
            return Results.Ok(video);
        });

        group.MapPost("/{id}/unpublish", async (
            string id, HttpContext ctx, IVideoResourceStore store, ILogger<ApiLog> logger, CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var video = await store.GetByIdAsync(id, ct);
            if (video is null) return Results.NotFound(new { error = "视频资源不存在" });

            video.ShelfStatus = ShelfStatus.OffShelf;
            await store.UpdateAsync(video, ct);
            logger.LogInformation("审计[下架视频]：操作人={Operator}，资源={Id}", cu.UserId, id);
            return Results.Ok(video);
        });

        // ===== 失败重试：清失败态，从 FailedStage 重跑 =====

        group.MapPost("/{id}/retry", async (
            string id,
            HttpContext ctx,
            IVideoResourceStore store,
            IVideoPipelineQueue pipeline,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var video = await store.GetByIdAsync(id, ct);
            if (video is null) return Results.NotFound(new { error = "视频资源不存在" });
            if (video.ProcessStatus != VideoProcessStatus.Failed)
                return Results.Conflict(new { error = "仅失败（FAILED）资源可重试" });

            var startStage = video.FailedStage ?? VideoProcessStatus.Probing;
            video.FailReason = null;
            video.FailedStage = null;
            await store.UpdateAsync(video, ct);
            pipeline.Enqueue(new VideoPipelineJob(video.Id, startStage));
            logger.LogInformation("审计[重试视频]：操作人={Operator}，资源={Id}，从阶段 {Stage} 重跑",
                cu.UserId, id, startStage);
            return Results.Ok(new { ok = true, startStage });
        });

        // ===== 字幕轨 =====

        // 上传字幕：multipart；SRT/ASS 归一化为 VTT 存储；可设为默认轨
        group.MapPost("/{id}/subtitles", async (
            string id,
            IFormFile file,
            string? language,
            string? label,
            bool isDefault,
            HttpContext ctx,
            IVideoResourceStore store,
            IVideoSubStore subs,
            IStorageService storage,
            IVideoPipelineQueue pipeline,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var video = await store.GetByIdAsync(id, ct);
            if (video is null) return Results.NotFound(new { error = "视频资源不存在" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedSubtitleExtensions.Contains(ext))
                return Results.BadRequest(new { error = $"不支持的字幕格式 {ext}（允许 srt/vtt/ass）" });
            if (file.Length == 0) return Results.BadRequest(new { error = "字幕文件为空" });

            string vtt;
            int cueCount;
            try
            {
                using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
                var raw = await reader.ReadToEndAsync(ct);
                (vtt, cueCount) = SubtitleConverter.ToVtt(file.FileName, raw);
                if (cueCount == 0) return Results.BadRequest(new { error = "未解析到有效字幕条目" });
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (isDefault) await subs.ClearDefaultTracksAsync(id, ct);

            var track = new SubtitleTrack
            {
                VideoResourceId = id,
                Language = string.IsNullOrWhiteSpace(language) ? "zh-CN" : language.Trim(),
                Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
                IsDefault = isDefault,
                Source = SubtitleSource.Uploaded,
                StorageKey = $"videos/{id}/subtitles/{Guid.NewGuid():N}.vtt",
                CueCount = cueCount,
                OriginalFileName = Path.GetFileName(file.FileName),
            };

            await using var outStream = new MemoryStream(Encoding.UTF8.GetBytes(vtt));
            await storage.SaveAsync(track.StorageKey, outStream, ct);
            await subs.AddTrackAsync(track, ct);

            // 若视频正停在 SUBTITLING 等待字幕，上传后自动接续流水线继续后续阶段
            if (video.ProcessStatus == VideoProcessStatus.Subtitling)
            {
                pipeline.Enqueue(new VideoPipelineJob(video.Id, VideoProcessStatus.Subtitling));
            }

            logger.LogInformation("审计[上传字幕]：操作人={Operator}，资源={Id}，轨={TrackId}，语言={Lang}，{Cues} 条 cue",
                cu.UserId, id, track.Id, track.Language, cueCount);
            return Results.Created($"/api/admin/videos/{id}/subtitles/{track.Id}", track);
        }).DisableAntiforgery();

        group.MapDelete("/{id}/subtitles/{trackId}", async (
            string id, string trackId,
            HttpContext ctx,
            IVideoResourceStore store,
            IVideoSubStore subs,
            IStorageService storage,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var track = await subs.GetTrackAsync(id, trackId, ct);
            if (track is null) return Results.NotFound(new { error = "字幕轨不存在" });

            await storage.DeleteAsync(track.StorageKey, ct);
            await subs.DeleteTrackAsync(id, trackId, ct);
            logger.LogInformation("审计[删除字幕]：操作人={Operator}，资源={Id}，轨={TrackId}", cu.UserId, id, trackId);
            return Results.Ok(new { ok = true });
        });

        // ===== 播放 =====

        // 签名播放地址：短时效 HMAC token（对齐入戏 §3.3：客户端不持有长期密钥，
        // 上线后签名 URL 指向 CDN，散会后 token 失效的会议级限制由后续播放控制面实现）
        group.MapGet("/{id}/play-url", async (
            string id,
            HttpContext ctx,
            IVideoResourceStore store,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            var video = await store.GetByIdAsync(id, ct);
            if (video is null) return Results.NotFound(new { error = "视频资源不存在" });

            var expiresInSeconds = 1800;
            var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresInSeconds;
            var sig = SignPlaybackToken(video.Id, exp, ctx.RequestServices);
            var url = $"/api/admin/videos/{id}/stream?exp={exp}&sig={sig}";
            return Results.Ok(new PlayUrlResponse(url, expiresInSeconds));
        });

        return app;
    }

    /// <summary>
    /// 播放流（Range 支持，供管理端预览）。不走 JWT——浏览器新标签页无法携带 Bearer 头，
    /// 改用与 play-url 配对的短时效签名参数校验。
    /// </summary>
    public static IEndpointRouteBuilder MapVideoStreamEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/videos/{id}/stream", async (
            string id,
            long? exp,
            string? sig,
            IVideoResourceStore store,
            IStorageService storage,
            HttpContext httpCtx,
            CancellationToken ct) =>
        {
            if (exp is not { } expiry || string.IsNullOrEmpty(sig))
                return Results.Unauthorized();
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry)
                return Results.Text("签名已过期", statusCode: 401);
            if (!FixedTimeEquals(sig, SignPlaybackToken(id, expiry, httpCtx.RequestServices)))
                return Results.Unauthorized();

            var video = await store.GetByIdAsync(id, ct);
            if (video is null) return Results.NotFound(new { error = "视频资源不存在" });

            // 本期预览原始文件；HLS 上线后此处返回 master 清单（代理 CDN 或本地切片）
            var path = storage.GetPhysicalPath(video.StorageKey);
            if (path is null) return Results.NotFound(new { error = "视频文件不存在" });

            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mp4" => "video/mp4",
                ".mkv" => "video/x-matroska",
                ".mov" => "video/quicktime",
                _ => "application/octet-stream",
            };
            return Results.File(path, contentType, enableRangeProcessing: true);
        });

        return app;
    }

    // ===== 播放签名 =====

    private static string SignPlaybackToken(string videoId, long expUnixSeconds, IServiceProvider services)
    {
        var jwt = services.GetRequiredService<IOptions<JwtOptions>>().Value;
        var payload = $"{videoId}:{expUnixSeconds}";
        var key = new byte[32];
        Encoding.UTF8.GetBytes(jwt.Secret).AsSpan(0, Math.Min(32, jwt.Secret.Length)).CopyTo(key);
        using var hmac = new HMACSHA256(key);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

public sealed record VideoDetail(
    VideoResource Video,
    IReadOnlyList<SubtitleTrack> Subtitles,
    IReadOnlyList<VideoStatusEvent> Events);

public sealed record PlayUrlResponse(string Url, int ExpiresInSeconds);
