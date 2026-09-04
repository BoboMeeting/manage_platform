using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ManagerPlatform.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ManagerPlatform.Services;

/// <summary>调度服务创建房间后返回的房间凭证。</summary>
public sealed record SchedulerRoomTicket(string RoomName, string Ticket, DateTimeOffset ExpiresAt);

/// <summary>调用调度服务失败（网络异常或非 2xx 响应）。</summary>
public sealed class SchedulerException(string message) : Exception(message);

/// <summary>
/// 调度服务客户端（管理平台 → meet_schedule_server，服务间调用）。
/// 每次请求现签服务 JWT：aud=Service（调度服务内部接口只验签名 + 受众），
/// 签名密钥复用管理平台 Jwt:Secret（与调度服务 Auth:JwtSecret 共享同一密钥）。
/// </summary>
public interface ISchedulerClient
{
    /// <summary>
    /// 调用调度服务内部接口：幂等创建/绑定 LiveKit 媒体房间，并为指定参会者签发房间凭证。
    /// </summary>
    Task<SchedulerRoomTicket> CreateRoomTicketAsync(
        string roomName,
        string conferenceId,
        string identity,
        string name,
        bool isHost,
        CancellationToken ct = default);
}

public sealed class SchedulerClient : ISchedulerClient
{
    /// <summary>服务 JWT 受众（与调度服务 AuthOptions.ServiceAudience 约定一致）。</summary>
    public const string ServiceAudience = "Service";

    /// <summary>服务 JWT 有效期：仅用于单次内部调用，5 分钟足够。</summary>
    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly SchedulerOptions _scheduler;
    private readonly JwtOptions _jwt;

    public SchedulerClient(HttpClient http, IOptions<SchedulerOptions> scheduler, IOptions<JwtOptions> jwt)
    {
        _http = http;
        _scheduler = scheduler.Value;
        _jwt = jwt.Value;

        _http.BaseAddress = new Uri(_scheduler.InternalBaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public async Task<SchedulerRoomTicket> CreateRoomTicketAsync(
        string roomName,
        string conferenceId,
        string identity,
        string name,
        bool isHost,
        CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/internal/rooms")
        {
            Content = JsonContent.Create(new
            {
                roomName,
                conferenceId,
                identity,
                name,
                isHost,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", IssueServiceToken());

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SchedulerException($"无法连接调度服务：{ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SchedulerException("调度服务请求超时");
        }

        if (!resp.IsSuccessStatusCode)
        {
            throw new SchedulerException($"调度服务返回错误：HTTP {(int)resp.StatusCode}");
        }

        var body = await resp.Content.ReadFromJsonAsync<RoomTicketReply>(cancellationToken: ct)
                   ?? throw new SchedulerException("调度服务响应为空");
        if (string.IsNullOrWhiteSpace(body.Ticket))
            throw new SchedulerException("调度服务未返回房间凭证");

        return new SchedulerRoomTicket(body.RoomName ?? roomName, body.Ticket, body.ExpiresAt);
    }

    /// <summary>签发服务间 JWT（aud=Service，HS256，复用用户 JWT 的签名密钥与签发者）。</summary>
    private string IssueServiceToken()
    {
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: ServiceAudience,
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, "manager-platform"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            },
            notBefore: now.UtcDateTime,
            expires: now.Add(TokenTtl).UtcDateTime,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.Secret)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record RoomTicketReply(string? RoomName, string? Ticket, DateTimeOffset ExpiresAt);
}
