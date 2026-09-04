namespace ManagerPlatform.Models;

/// <summary>
/// LiveKit 服务配置（单例记录）。用于在管理后台设置 LiveKit 服务地址、API Key / Secret，
/// 替代仅依赖 appsettings.json / 环境变量的方式，便于运维人员通过页面调整而无需重启服务。
/// 约定：表中至多 1 条记录；Id 固定为 "default"。
/// </summary>
public sealed class LiveKitConfig
{
    /// <summary>固定主键 "default"，保证单例。</summary>
    public string Id { get; set; } = "default";

    /// <summary>客户端连接地址（ws:// 或 wss://），返回给客户端入会。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>LiveKit API Key。</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>LiveKit API Secret（≥32 字节）。</summary>
    public string ApiSecret { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
