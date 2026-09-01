using Livekit.Server.Sdk.Dotnet;
using ManagerPlatform.Options;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.LiveKit;

/// <summary>
/// 生成 LiveKit 客户端入会 token。
/// 客户端凭此 token 直连 LiveKit Server，完成音视频收发。
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

public sealed class LiveKitTokenService : ILiveKitTokenService
{
    private readonly LiveKitOptions _opt;

    public LiveKitTokenService(IOptions<LiveKitOptions> opt) => _opt = opt.Value;

    public string CreateClientToken(string roomName, string identity, string name, bool isHost, string? metadata = null)
    {
        var token = new AccessToken(_opt.ApiKey, _opt.ApiSecret)
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
