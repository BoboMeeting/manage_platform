namespace ManagerPlatform.Models;

/// <summary>
/// 用户账号。覆盖普通参会者、主持人、管理员等角色。
/// </summary>
public sealed class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>登录账号（手机号或邮箱）</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>账号类型：phone / email</summary>
    public AccountKind AccountKind { get; set; } = AccountKind.Email;

    /// <summary>登录密码（PBKDF2 哈希，格式: iterations.salt.base64hash）</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public string Nickname { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    /// <summary>系统角色：普通用户 / 超级管理员 / 运营 / 观察员</summary>
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>账号状态：启用/禁用</summary>
    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AccountKind
{
    Email,
    Phone,
}

public enum UserRole
{
    /// <summary>普通用户（参会者/主持人）</summary>
    User = 0,

    /// <summary>观察员：只读管理后台</summary>
    Observer = 1,

    /// <summary>运营：用户/会议/AI 角色管理</summary>
    Operator = 2,

    /// <summary>超级管理员：全部权限，含权限管理</summary>
    SuperAdmin = 3,
}

public enum UserStatus
{
    Active = 0,
    Disabled = 1,
}
