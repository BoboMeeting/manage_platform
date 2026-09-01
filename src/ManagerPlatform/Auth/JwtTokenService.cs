using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ManagerPlatform.Auth;

/// <summary>
/// 颁发业务系统 JWT（鉴权管理平台 API）。
/// 与 LiveKit 入会 token（由 LiveKitTokenService 颁发）是两套不同的 token。
/// </summary>
public sealed class JwtTokenService
{
    private readonly JwtOptions _opt;

    public JwtTokenService(IOptions<JwtOptions> opt) => _opt = opt.Value;

    public string Issue(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Account),
            new Claim("nickname", user.Nickname),
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.Account),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.AddSeconds(_opt.ExpiresSeconds).UtcDateTime,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>
/// 从当前 HttpContext 提取登录用户信息。仅在已认证请求中使用。
/// </summary>
public sealed class CurrentUser
{
    public string UserId { get; init; } = string.Empty;
    public string Account { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public UserRole Role { get; init; } = UserRole.User;
}

public static class CurrentUserExtensions
{
    /// <summary>从 ClaimsPrincipal 构造 CurrentUser；未登录返回 null。</summary>
    public static CurrentUser? ToCurrentUser(this ClaimsPrincipal principal)
    {
        if (!(principal.Identity?.IsAuthenticated ?? false)) return null;

        var id = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(id)) return null;

        var account = principal.FindFirst(ClaimTypes.Name)?.Value
                      ?? principal.FindFirst(JwtRegisteredClaimNames.UniqueName)?.Value
                      ?? string.Empty;
        var nickname = principal.FindFirst("nickname")?.Value ?? account;
        var roleStr = principal.FindFirst(ClaimTypes.Role)?.Value;
        Enum.TryParse(roleStr, out UserRole role);

        return new CurrentUser { UserId = id, Account = account, Nickname = nickname, Role = role };
    }

    /// <summary>是否拥有至少 <paramref name="minRole"/> 的管理角色。</summary>
    public static bool IsAtLeast(this CurrentUser? user, UserRole minRole) =>
        user is not null && user.Role >= minRole;
}
