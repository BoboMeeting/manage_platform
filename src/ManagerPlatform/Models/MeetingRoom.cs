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

    /// <summary>LiveKit 房间名（随机 9 位数字，便于客户端直接入会）</summary>
    public string RoomName { get; set; } = Random.Shared.Next(100_000_000, 1_000_000_000).ToString();

    public DateTimeOffset StartTime { get; set; }

    /// <summary>预计时长（秒）</summary>
    public int DurationSeconds { get; set; } = 3600;

    public DateTimeOffset EndTime => StartTime.AddSeconds(DurationSeconds);

    /// <summary>最大参会人数（含 AI）</summary>
    public int MaxParticipants { get; set; } = 50;

    public MeetingRoomStatus Status { get; set; } = MeetingRoomStatus.Scheduled;

    /// <summary>是否锁定（禁止新成员加入）</summary>
    public bool Locked { get; set; }

    /// <summary>邀请码（短码，便于分享），可选</summary>
    public string? InviteCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum MeetingRoomStatus
{
    /// <summary>已预约（未到开放时间）</summary>
    Scheduled = 0,

    /// <summary>开放中（在预约时间窗口内，允许入会）</summary>
    Open = 1,

    /// <summary>已关闭（预约时间窗口已结束）</summary>
    Closed = 2,

    /// <summary>已取消</summary>
    Cancelled = 3,
}
