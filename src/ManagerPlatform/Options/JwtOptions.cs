namespace ManagerPlatform.Options;

/// <summary>
/// JWT 配置（appsettings.json "Jwt" 节）。
/// 生产环境用 user-secrets / 环境变量注入 Secret。
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "BoboMeet.ManagerPlatform";

    public string Audience { get; set; } = "BoboMeet.Client";

    /// <summary>签名密钥（≥32 字节，HS256）</summary>
    public string Secret { get; set; } = "dev-secret-change-me-please-32bytes-or-more";

    public int ExpiresSeconds { get; set; } = 7 * 24 * 3600;
}
