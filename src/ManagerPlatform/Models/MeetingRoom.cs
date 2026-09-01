namespace ManagerPlatform.Models;

/// <summary>
/// 访谈室（会议预约记录）。对应设计文档 MeetingRoom 实体。
/// </summary>
public sealed class MeetingRoom
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>会议主题</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>预约/创建会议的主持人用户 Id</summary>
    public string HostUserId { get; set; } = string.Empty;

    public string HostNickname { get; set; } = string.Empty;

    /// <summary>LiveKit 房间名（默认与 Id 一致，便于客户端直接入会）</summary>
    public string RoomName { get; set; } = string.Empty;

    public DateTimeOffset StartTime { get; set; }

    /// <summary>预计时长（秒）</summary>
    public int DurationSeconds { get; set; } = 3600;

    public DateTimeOffset EndTime => StartTime.AddSeconds(DurationSeconds);

    /// <summary>最大参会人数（含 AI）</summary>
    public int MaxParticipants { get; set; } = 50;

    public MeetingStatus Status { get; set; } = MeetingStatus.Scheduled;

    /// <summary>是否锁定（禁止新成员加入）</summary>
    public bool Locked { get; set; }

    /// <summary>邀请码（短码，便于分享），可选</summary>
    public string? InviteCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum MeetingStatus
{
    /// <summary>已预约</summary>
    Scheduled = 0,

    /// <summary>进行中</summary>
    InProgress = 1,

    /// <summary>已结束</summary>
    Ended = 2,

    /// <summary>已取消</summary>
    Cancelled = 3,
}
