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
    MeetingStatus Status,
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
