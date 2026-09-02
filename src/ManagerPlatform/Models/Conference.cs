namespace ManagerPlatform.Models;

/// <summary>
/// 会议场次：用户进入房间到离开算一场。
/// 预约时间窗口内可反复进入，每次进入开启一场新会议；
/// 同一房间同一时刻至多一场进行中的会议。
/// </summary>
public sealed class Conference
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string RoomId { get; set; } = string.Empty;

    /// <summary>开启该场会议的用户（首个入会者）</summary>
    public string StartedByUserId { get; set; } = string.Empty;

    public ConferenceStatus Status { get; set; } = ConferenceStatus.InProgress;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ConferenceStatus
{
    /// <summary>进行中</summary>
    InProgress = 0,

    /// <summary>已结束</summary>
    Ended = 1,
}
