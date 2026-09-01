namespace ManagerPlatform.Models;

/// <summary>
/// AI 角色模板（老师、面试官、虚拟闺蜜等）。对应设计文档 AIRole 实体。
/// </summary>
public sealed class AIRole
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>大模型 prompt 模板</summary>
    public string PromptTemplate { get; set; } = string.Empty;

    /// <summary>TTS 配置（音色 id/语速等，JSON 字符串）</summary>
    public string? TtsConfig { get; set; }

    public string? AvatarUrl { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
