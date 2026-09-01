namespace ManagerPlatform.Models;

/// <summary>
/// AI 在某次会议中的运行实例。对应设计文档 AISession 实体。
/// </summary>
public sealed class AiSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string RoomId { get; set; } = string.Empty;

    public string AiRoleId { get; set; } = string.Empty;

    /// <summary>实例标识（Agent 端可识别，便于上下线调度）</summary>
    public string AgentInstance { get; set; } = string.Empty;

    /// <summary>用户自定义 prompt 覆盖（可选）</summary>
    public string? CustomPrompt { get; set; }

    public AISessionStatus Status { get; set; } = AISessionStatus.Pending;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? EndedAt { get; set; }
}

public enum AISessionStatus
{
    /// <summary>已创建，待 Agent 入会</summary>
    Pending = 0,

    /// <summary>在线</summary>
    Active = 1,

    /// <summary>已移除</summary>
    Ended = 2,
}
