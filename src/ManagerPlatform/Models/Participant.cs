namespace ManagerPlatform.Models;

/// <summary>
/// 参会者记录（人类用户与 AI 用户共用）。对应设计文档 Participant 实体。
/// </summary>
public sealed class Participant
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>所属会议场次 Id</summary>
    public string ConferenceId { get; set; } = string.Empty;

    public string? UserId { get; set; }

    /// <summary>参会昵称（访客可无账号）</summary>
    public string Nickname { get; set; } = string.Empty;

    public DateTimeOffset? JoinTime { get; set; }

    public DateTimeOffset? LeaveTime { get; set; }

    public bool IsAi { get; set; }

    public ParticipantRole Role { get; set; } = ParticipantRole.Member;

    /// <summary>关联的 AI 会话 Id（IsAi=true 时填充）</summary>
    public string? AiSessionId { get; set; }
}

public enum ParticipantRole
{
    Member = 0,
    Host = 1,
}
