namespace ManagerPlatform.Options;

/// <summary>
/// 调度服务（meet_schedule_server）连接配置（appsettings.json "Scheduler" 节，
/// 可用环境变量 Scheduler__InternalBaseUrl 等覆盖）。
/// 管理平台不再直接连接 LiveKit：入会时经调度服务创建媒体房间并换取房间凭证。
/// </summary>
public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>
    /// 内部地址：管理平台 → 调度服务的服务间调用地址（容器网络内可用服务名）。
    /// 调用 /api/v1/internal/* 接口。
    /// </summary>
    public string InternalBaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>
    /// 外部地址：返回给客户端（App）的可达地址，客户端据此调用 /api/v1/external/rooms/*。
    /// 为空时回退到 <see cref="InternalBaseUrl"/>（本机开发场景两者相同）。
    /// </summary>
    public string? ExternalBaseUrl { get; set; }

    /// <summary>返回给客户端的有效外部地址（去尾部斜杠）。</summary>
    public string EffectiveExternalBaseUrl =>
        string.IsNullOrWhiteSpace(ExternalBaseUrl) ? InternalBaseUrl : ExternalBaseUrl;
}
