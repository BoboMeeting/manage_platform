namespace ManagerPlatform.Options;

/// <summary>
/// LiveKit 连接配置（appsettings.json "LiveKit" 节，可用环境变量 LiveKit__Url 等覆盖）。
/// 与 meet_schedule_server 共享同一套 LiveKit 实例配置。
/// </summary>
public sealed class LiveKitOptions
{
    public const string SectionName = "LiveKit";

    /// <summary>客户端连接地址（ws:// 或 wss://），返回给客户端入会</summary>
    public string Url { get; set; } = "ws://localhost:7880";

    public string ApiKey { get; set; } = "devkey";

    /// <summary>API 密钥（≥32 字节，LiveKit SDK 强制要求 ≥256 bit）。
    /// 必须与 LiveKit Server 配置的 api_secret 一致，否则入会 token 校验不通过。</summary>
    public string ApiSecret { get; set; } = "dev-secret-change-me-to-match-livekit-32b";

    /// <summary>Twirp HTTP 地址（服务端 API 用，ws→http / wss→https）</summary>
    public string HttpUrl =>
        Url.Replace("ws://", "http://").Replace("wss://", "https://");
}
