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

    public ConferenceStatus Status { get; set; } = ConferenceStatus.Waiting;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Waiting 状态的过期时间，超过则变为 Ended（超时无人入会）</summary>
    public DateTimeOffset? WaitingExpiresAt { get; set; }

    /// <summary>PendingClose 宽限期过期时间，超过则变为 Ended（超时无人重连）</summary>
    public DateTimeOffset? PendingCloseExpiresAt { get; set; }
}

public enum ConferenceStatus
{
    /// <summary>会议已创建，等待首位用户入会</summary>
    Waiting = 0,

    /// <summary>有用户在会，进行中</summary>
    InProgress = 1,

    /// <summary>最后一位用户已离开，重连宽限期内允许回到本场会议</summary>
    PendingClose = 2,

    /// <summary>主持人主动结束会议，正常完成（唯一入口：主动 end 接口）</summary>
    Completed = 3,

    /// <summary>超时被动结束：Waiting 超时无人 / PendingClose 超时无人回</summary>
    Ended = 4,
}
