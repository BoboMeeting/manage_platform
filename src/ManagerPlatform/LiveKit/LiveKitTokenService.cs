using Livekit.Server.Sdk.Dotnet;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Stores;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.LiveKit;

/// <summary>
/// 生成 LiveKit 客户端入会 token。
/// 客户端凭此 token 直连 LiveKit Server，完成音视频收发。
/// 配置读取顺序：数据库 > appsettings.json（兜底）。
/// </summary>
public interface ILiveKitTokenService
{
    /// <param name="roomName">LiveKit 房间名</param>
    /// <param name="identity">参会者唯一标识（用户 Id 或访客临时 Id）</param>
    /// <param name="name">展示昵称</param>
    /// <param name="isHost">是否主持人（影响发布权限粒度，当前均授予发布权以便发言）</param>
    /// <param name="metadata">自定义元数据（可放 AI 标识等）</param>
    string CreateClientToken(string roomName, string identity, string name, bool isHost, string? metadata = null);
}

/// <summary>
/// 运行时 LiveKit 配置解析器。提供"从数据库读取，无则使用 IOptions 兜底"的统一入口，
/// 保证管理后台修改配置后，下一次请求即可生效（无需重启服务）。
/// 同时返回 LiveKit Url 给入会响应。
/// </summary>
public interface ILiveKitConfigProvider
{
    /// <summary>返回当前生效的配置（数据库优先，IOptions 兜底）。</summary>
    Task<ResolvedLiveKitConfig> ResolveAsync(CancellationToken ct = default);
}

/// <summary>运行时解析后的 LiveKit 配置。</summary>
public sealed record ResolvedLiveKitConfig(
    string Url,
    string ApiKey,
    string ApiSecret,
    bool FromDatabase);

public sealed class LiveKitConfigProvider(
    ILiveKitConfigStore store,
    IOptions<LiveKitOptions> fallback) : ILiveKitConfigProvider
{
    public async Task<ResolvedLiveKitConfig> ResolveAsync(CancellationToken ct = default)
    {
        var cfg = await store.GetAsync(ct);
        if (cfg is not null
            && !string.IsNullOrWhiteSpace(cfg.Url)
            && !string.IsNullOrWhiteSpace(cfg.ApiKey)
            && !string.IsNullOrWhiteSpace(cfg.ApiSecret))
        {
            return new ResolvedLiveKitConfig(cfg.Url, cfg.ApiKey, cfg.ApiSecret, FromDatabase: true);
        }

        var opt = fallback.Value;
        return new ResolvedLiveKitConfig(opt.Url, opt.ApiKey, opt.ApiSecret, FromDatabase: false);
    }
}

/// <summary>
/// LiveKit token 签发服务。
/// 改为 Scoped：因运行时需从 DB（Scoped Store）读取最新配置，保证后台修改后立即可用。
/// </summary>
public sealed class LiveKitTokenService : ILiveKitTokenService
{
    private readonly ILiveKitConfigProvider _configProvider;

    public LiveKitTokenService(ILiveKitConfigProvider configProvider)
    {
        _configProvider = configProvider;
    }

    public string CreateClientToken(string roomName, string identity, string name, bool isHost, string? metadata = null)
    {
        // 同步阻塞等待异步结果（LiveKit SDK 接口为同步 string 返回；Scoped 下 DB 查询很快）
        var cfg = _configProvider.ResolveAsync(default).GetAwaiter().GetResult();

        var token = new AccessToken(cfg.ApiKey, cfg.ApiSecret)
            .WithIdentity(identity)
            .WithName(name)
            .WithMetadata(metadata ?? string.Empty)
            .WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = roomName,
                CanPublish = true,
                CanSubscribe = true,
                CanPublishData = true,
            })
            .WithTtl(TimeSpan.FromHours(6));

        return token.ToJwt();
    }
}
