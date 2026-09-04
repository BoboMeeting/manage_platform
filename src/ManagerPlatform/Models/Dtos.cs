namespace ManagerPlatform.Models;

// ==================== 认证 ====================

public sealed record RegisterRequest(
    string Account,
    string Password,
    string Nickname,
    AccountKind? AccountKind = null);

public sealed record LoginRequest(string Account, string Password);

public sealed record AuthResponse(string AccessToken, int ExpiresInSeconds, UserInfo User);

public sealed record UserInfo(
    string Id,
    string Account,
    string Nickname,
    string? AvatarUrl,
    UserRole Role,
    UserStatus Status);

// ==================== 会议 ====================

public sealed record CreateRoomRequest(
    string Title,
    DateTimeOffset? StartTime = null,
    int? DurationSeconds = null,
    int? MaxParticipants = null,
    string? InviteCode = null);

public sealed record JoinRoomRequest(string? Nickname = null);

public sealed record JoinRoomResponse(
    string RoomId,
    string ConferenceId,
    string RoomName,
    string LiveKitToken,
    string LiveKitUrl,
    bool IsHost,
    UserInfo? User);

public sealed record ConferenceSummary(
    string Id,
    string RoomId,
    string StartedByUserId,
    ConferenceStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int ActiveParticipantCount);

public sealed record RoomSummary(
    string Id,
    string Title,
    string RoomName,
    string HostUserId,
    string HostNickname,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    int MaxParticipants,
    MeetingRoomStatus Status,
    bool Locked,
    string? InviteCode,
    DateTimeOffset CreatedAt);

// ==================== AI ====================

public sealed record AddAiRequest(string RoleId, string? CustomPrompt = null);

public sealed record AiSessionInfo(
    string Id,
    string ConferenceId,
    string AiRoleId,
    string RoleName,
    string AgentInstance,
    AISessionStatus Status,
    string? CustomPrompt,
    DateTimeOffset CreatedAt);

public sealed record AiRoleRequest(
    string Name,
    string? Description,
    string PromptTemplate,
    string? TtsConfig,
    string? AvatarUrl);

// ==================== 管理端 ====================

public sealed record AdminUpdateUserRequest(
    string? Nickname,
    UserRole? Role,
    UserStatus? Status);

public sealed record AdminResetPasswordRequest(string NewPassword);

public sealed record AdminCreateUserRequest(
    string Account,
    string Password,
    string Nickname,
    AccountKind? AccountKind = null,
    UserRole Role = UserRole.User);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

// ==================== LiveKit 配置 ====================

public sealed record LiveKitConfigRequest(
    string Url,
    string ApiKey,
    /// <summary>
    /// 若为空字符串则表示"不修改现有 Secret"；仅当值非空时更新。
    /// 新增记录（库中尚无配置）时必填（长度建议 ≥ 32 字节）。
    /// </summary>
    string ApiSecret);

/// <summary>
/// 管理端响应：SuperAdmin 查看/编辑 LiveKit 配置。
/// ApiSecret 返回时脱敏（仅前后各 2 字符可见），前端提交空串表示"保持不变"。
/// </summary>
public sealed record LiveKitConfigResponse(
    string Url,
    string ApiKey,
    string ApiSecretMasked,
    DateTimeOffset UpdatedAt,
    bool FromDatabase);

/// <summary>公共响应：仅返回客户端入会需要的 LiveKit URL（不含任何密钥）。</summary>
public sealed record LiveKitPublicConfig(string Url);
