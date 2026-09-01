namespace ManagerPlatform.Auth;

/// <summary>
/// 声明式授权策略名（定义见 Program.cs AddAuthorization）。
/// 角色 claim 值 = UserRole 枚举名字符串（签发时 Role.ToString()）。
/// RequireRole 为精确集合匹配，"X 及以上"需按当前角色层级展开；
/// 若未来插入新管理角色，需同步更新策略定义。
/// </summary>
public static class Policies
{
    /// <summary>仅用户角色（User）</summary>
    public const string UserOnly = "UserOnly";  

    /// <summary>仅用户角色（User）</summary>
    public const string UserPlus = "User+";


    /// <summary>仅观察角色（Observer）</summary>
    public const string ObserveOnly = "ObserveOnly";  

    /// <summary>观察及以上（Observer、SuperAdmin）</summary>
    public const string ObservePlus = "Observe+";   

    /// <summary>仅运营角色（Operator、SuperAdmin）</summary>
    public const string OperatorOnly = "OperatorOnly";

    /// <summary>运营及以上（Operator、SuperAdmin）</summary>
    public const string OperatorPlus = "Operator+";

    /// <summary>仅超级管理员</summary>
    public const string SuperAdminOnly = "SuperAdminOnly";
}
