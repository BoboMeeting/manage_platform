using ManagerPlatform.Data;
using ManagerPlatform.Models;
using Microsoft.EntityFrameworkCore;

namespace ManagerPlatform.Stores;

/// <summary>视频资源仓储。</summary>
public interface IVideoResourceStore
{
    Task<VideoResource?> GetByIdAsync(string id, CancellationToken ct = default);
    /// <summary>关键词搜标题/原始文件名，可按处理状态过滤，分页返回（items, total）。</summary>
    Task<(IReadOnlyList<VideoResource> Items, int Total)> FindAsync(
        string? keyword, VideoProcessStatus? status, int page, int pageSize, CancellationToken ct = default);
    Task AddAsync(VideoResource video, CancellationToken ct = default);
    Task UpdateAsync(VideoResource video, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>字幕轨 + 处理流水事件仓储。</summary>
public interface IVideoSubStore
{
    Task<IReadOnlyList<SubtitleTrack>> GetTracksAsync(string videoResourceId, CancellationToken ct = default);
    Task<SubtitleTrack?> GetTrackAsync(string videoResourceId, string trackId, CancellationToken ct = default);
    Task AddTrackAsync(SubtitleTrack track, CancellationToken ct = default);
    Task UpdateTrackAsync(SubtitleTrack track, CancellationToken ct = default);
    Task DeleteTrackAsync(string videoResourceId, string trackId, CancellationToken ct = default);
    Task ClearDefaultTracksAsync(string videoResourceId, CancellationToken ct = default);

    Task<IReadOnlyList<VideoStatusEvent>> GetEventsAsync(string videoResourceId, CancellationToken ct = default);
    Task AddEventAsync(VideoStatusEvent evt, CancellationToken ct = default);
    Task DeleteEventsAsync(string videoResourceId, CancellationToken ct = default);
}

public sealed class EfVideoResourceStore(AppDbContext db) : IVideoResourceStore
{
    private readonly AppDbContext _db = db;

    public Task<VideoResource?> GetByIdAsync(string id, CancellationToken ct = default) =>
        _db.VideoResources.FirstOrDefaultAsync(v => v.Id == id, ct);

    public async Task<(IReadOnlyList<VideoResource> Items, int Total)> FindAsync(
        string? keyword, VideoProcessStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        IQueryable<VideoResource> q = _db.VideoResources;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            q = q.Where(v => EF.Functions.ILike(v.Title, "%" + kw + "%")
                          || EF.Functions.ILike(v.OriginalFileName, "%" + kw + "%"));
        }
        if (status.HasValue) q = q.Where(v => v.ProcessStatus == status.Value);

        var total = await q.CountAsync(ct);
        var items = await q.OrderByDescending(v => v.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return (items, total);
    }

    public async Task AddAsync(VideoResource video, CancellationToken ct = default)
    {
        _db.VideoResources.Add(video);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(VideoResource video, CancellationToken ct = default)
    {
        video.UpdatedAt = DateTimeOffset.UtcNow;
        _db.VideoResources.Update(video);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default) =>
        await _db.VideoResources.Where(v => v.Id == id).ExecuteDeleteAsync(ct);

    public Task<int> CountAsync(CancellationToken ct = default) =>
        _db.VideoResources.CountAsync(ct);
}

public sealed class EfVideoSubStore(AppDbContext db) : IVideoSubStore
{
    private readonly AppDbContext _db = db;

    public Task<IReadOnlyList<SubtitleTrack>> GetTracksAsync(string videoResourceId, CancellationToken ct = default) =>
        _db.SubtitleTracks
            .Where(t => t.VideoResourceId == videoResourceId)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.CreatedAt)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<SubtitleTrack>)t.Result, ct);

    public Task<SubtitleTrack?> GetTrackAsync(string videoResourceId, string trackId, CancellationToken ct = default) =>
        _db.SubtitleTracks.FirstOrDefaultAsync(t => t.VideoResourceId == videoResourceId && t.Id == trackId, ct);

    public async Task AddTrackAsync(SubtitleTrack track, CancellationToken ct = default)
    {
        _db.SubtitleTracks.Add(track);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateTrackAsync(SubtitleTrack track, CancellationToken ct = default)
    {
        _db.SubtitleTracks.Update(track);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteTrackAsync(string videoResourceId, string trackId, CancellationToken ct = default) =>
        await _db.SubtitleTracks
            .Where(t => t.VideoResourceId == videoResourceId && t.Id == trackId)
            .ExecuteDeleteAsync(ct);

    /// <summary>取消该视频所有默认轨（上传新默认轨前调用）。</summary>
    public async Task ClearDefaultTracksAsync(string videoResourceId, CancellationToken ct = default) =>
        await _db.SubtitleTracks
            .Where(t => t.VideoResourceId == videoResourceId && t.IsDefault)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), ct);

    public Task<IReadOnlyList<VideoStatusEvent>> GetEventsAsync(string videoResourceId, CancellationToken ct = default) =>
        _db.VideoStatusEvents
            .Where(ev => ev.VideoResourceId == videoResourceId)
            .OrderBy(ev => ev.CreatedAt)
            .ToListAsync(ct)
            .ContinueWith(t => (IReadOnlyList<VideoStatusEvent>)t.Result, ct);

    public async Task AddEventAsync(VideoStatusEvent evt, CancellationToken ct = default)
    {
        _db.VideoStatusEvents.Add(evt);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteEventsAsync(string videoResourceId, CancellationToken ct = default) =>
        await _db.VideoStatusEvents
            .Where(ev => ev.VideoResourceId == videoResourceId)
            .ExecuteDeleteAsync(ct);
}
