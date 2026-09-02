using System.Collections.Concurrent;
using ManagerPlatform.Models;

namespace ManagerPlatform.Stores;

/// <summary>用户仓储。生产环境替换为 PostgreSQL 实现。</summary>
public interface IUserStore
{
    Task<User?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<User?> GetByAccountAsync(string account, CancellationToken ct = default);
    Task<IReadOnlyList<User>> FindAsync(string? keyword, UserStatus? status, UserRole? role, CancellationToken ct = default);
    Task<bool> AddAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

public interface IRoomStore
{
    Task<MeetingRoom?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<MeetingRoom?> GetByInviteCodeAsync(string inviteCode, CancellationToken ct = default);
    Task<IReadOnlyList<MeetingRoom>> FindAsync(string? hostUserId, MeetingStatus? status, CancellationToken ct = default);
    Task AddAsync(MeetingRoom room, CancellationToken ct = default);
    Task UpdateAsync(MeetingRoom room, CancellationToken ct = default);
    Task<IReadOnlyList<MeetingRoom>> GetAllAsync(CancellationToken ct = default);
}

public interface IParticipantStore
{
    Task<Participant?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<Participant>> GetByConferenceAsync(string conferenceId, CancellationToken ct = default);
    Task AddAsync(Participant p, CancellationToken ct = default);
    Task UpdateAsync(Participant p, CancellationToken ct = default);
    Task<int> CountActiveInConferenceAsync(string conferenceId, CancellationToken ct = default);
}

public interface IAiRoleStore
{
    Task<AIRole?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<AIRole>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(AIRole role, CancellationToken ct = default);
    Task UpdateAsync(AIRole role, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public interface IConferenceStore
{
    Task<Conference?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<Conference?> GetActiveByRoomAsync(string roomId, CancellationToken ct = default);
    Task<IReadOnlyList<Conference>> GetByRoomAsync(string roomId, CancellationToken ct = default);
    Task AddAsync(Conference conference, CancellationToken ct = default);
    Task UpdateAsync(Conference conference, CancellationToken ct = default);
}

public interface IAiSessionStore
{
    Task<AiSessionInfo?> GetInfoAsync(string id, CancellationToken ct = default);
    Task<AiSession?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<AiSessionInfo>> GetByConferenceAsync(string conferenceId, CancellationToken ct = default);
    Task AddAsync(AiSession session, CancellationToken ct = default);
    Task UpdateAsync(AiSession session, CancellationToken ct = default);
}

// ==================== 内存实现 ====================

public sealed class InMemoryUserStore : IUserStore
{
    private readonly ConcurrentDictionary<string, User> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, User> _byAccount = new(StringComparer.OrdinalIgnoreCase);
    // 跨多索引更新时使用同一锁，保证读改写序列原子，避免并发下账号变更导致索引不一致
    private readonly object _updateLock = new();

    public Task<User?> GetByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var u) ? u : null);

    public Task<User?> GetByAccountAsync(string account, CancellationToken ct = default) =>
        Task.FromResult(_byAccount.TryGetValue(account, out var u) ? u : null);

    public Task<IReadOnlyList<User>> FindAsync(string? keyword, UserStatus? status, UserRole? role, CancellationToken ct = default)
    {
        IEnumerable<User> q = _byId.Values;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            q = q.Where(u => u.Account.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || u.Nickname.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }
        if (status.HasValue) q = q.Where(u => u.Status == status.Value);
        if (role.HasValue) q = q.Where(u => u.Role == role.Value);
        return Task.FromResult<IReadOnlyList<User>>(q.OrderByDescending(u => u.CreatedAt).ToArray());
    }

    public Task<bool> AddAsync(User user, CancellationToken ct = default)
    {
        lock (_updateLock)
        {
            if (_byAccount.ContainsKey(user.Account))
                return Task.FromResult(false);
            _byId[user.Id] = user;
            _byAccount[user.Account] = user;
            return Task.FromResult(true);
        }
    }

    public Task UpdateAsync(User user, CancellationToken ct = default)
    {
        // 原子更新：账号变更时需移除旧账号索引，否则旧账号仍指向该用户，
        // 既造成查找不一致，也会阻止他人注册旧账号
        lock (_updateLock)
        {
            if (_byId.TryGetValue(user.Id, out var existing))
            {
                var oldAccount = existing.Account;
                if (!string.IsNullOrEmpty(oldAccount)
                    && !string.Equals(oldAccount, user.Account, StringComparison.OrdinalIgnoreCase))
                {
                    _byAccount.TryRemove(oldAccount, out _);
                }
            }
            _byId[user.Id] = user;
            _byAccount[user.Account] = user;
        }
        return Task.CompletedTask;
    }

    public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_byId.Count);
}

public sealed class InMemoryRoomStore : IRoomStore
{
    private readonly ConcurrentDictionary<string, MeetingRoom> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MeetingRoom> _byInvite = new(StringComparer.OrdinalIgnoreCase);

    public Task<MeetingRoom?> GetByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_byId.TryGetValue(id, out var r) ? r : null);

    public Task<MeetingRoom?> GetByInviteCodeAsync(string inviteCode, CancellationToken ct = default) =>
        Task.FromResult(_byInvite.TryGetValue(inviteCode, out var r) ? r : null);

    public Task<IReadOnlyList<MeetingRoom>> FindAsync(string? hostUserId, MeetingStatus? status, CancellationToken ct = default)
    {
        IEnumerable<MeetingRoom> q = _byId.Values;
        if (!string.IsNullOrEmpty(hostUserId)) q = q.Where(r => r.HostUserId == hostUserId);
        if (status.HasValue) q = q.Where(r => r.Status == status.Value);
        return Task.FromResult<IReadOnlyList<MeetingRoom>>(q.OrderByDescending(r => r.StartTime).ToArray());
    }

    public Task AddAsync(MeetingRoom room, CancellationToken ct = default)
    {
        _byId[room.Id] = room;
        if (!string.IsNullOrEmpty(room.InviteCode)) _byInvite[room.InviteCode] = room;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(MeetingRoom room, CancellationToken ct = default)
    {
        _byId[room.Id] = room;
        if (!string.IsNullOrEmpty(room.InviteCode)) _byInvite[room.InviteCode] = room;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MeetingRoom>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MeetingRoom>>(_byId.Values.OrderByDescending(r => r.StartTime).ToArray());
}

public sealed class InMemoryParticipantStore : IParticipantStore
{
    private readonly ConcurrentDictionary<string, Participant> _items = new(StringComparer.Ordinal);

    public Task<Participant?> GetByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var p) ? p : null);

    public Task<IReadOnlyList<Participant>> GetByConferenceAsync(string conferenceId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Participant>>(_items.Values.Where(p => p.ConferenceId == conferenceId).ToArray());

    public Task AddAsync(Participant p, CancellationToken ct = default) { _items[p.Id] = p; return Task.CompletedTask; }
    public Task UpdateAsync(Participant p, CancellationToken ct = default) { _items[p.Id] = p; return Task.CompletedTask; }

    public Task<int> CountActiveInConferenceAsync(string conferenceId, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.Count(p => p.ConferenceId == conferenceId && p.LeaveTime is null && !p.IsAi));
}

public sealed class InMemoryAiRoleStore : IAiRoleStore
{
    private readonly ConcurrentDictionary<string, AIRole> _items = new(StringComparer.Ordinal);

    public Task<AIRole?> GetByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var r) ? r : null);

    public Task<IReadOnlyList<AIRole>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AIRole>>(_items.Values.OrderByDescending(r => r.CreatedAt).ToArray());

    public Task AddAsync(AIRole role, CancellationToken ct = default) { _items[role.Id] = role; return Task.CompletedTask; }
    public Task UpdateAsync(AIRole role, CancellationToken ct = default) { _items[role.Id] = role; return Task.CompletedTask; }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        _items.TryRemove(id, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryConferenceStore : IConferenceStore
{
    private readonly ConcurrentDictionary<string, Conference> _items = new(StringComparer.Ordinal);
    // 保证“同一房间至多一场进行中会议”的创建原子性（对应 DB 的部分唯一索引）
    private readonly object _createLock = new();

    public Task<Conference?> GetByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(id, out var c) ? c : null);

    public Task<Conference?> GetActiveByRoomAsync(string roomId, CancellationToken ct = default) =>
        Task.FromResult(_items.Values.FirstOrDefault(c =>
            c.RoomId == roomId && c.Status == ConferenceStatus.InProgress));

    public Task<IReadOnlyList<Conference>> GetByRoomAsync(string roomId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Conference>>(
            _items.Values.Where(c => c.RoomId == roomId)
                .OrderByDescending(c => c.StartedAt)
                .ToArray());

    public Task AddAsync(Conference conference, CancellationToken ct = default)
    {
        lock (_createLock)
        {
            // 模拟 DB 部分唯一索引：同房间已有进行中场次则拒绝创建
            var active = _items.Values.FirstOrDefault(c =>
                c.RoomId == conference.RoomId && c.Status == ConferenceStatus.InProgress);
            if (active is not null && conference.Status == ConferenceStatus.InProgress)
                throw new InvalidOperationException("该房间已有进行中的会议");
            _items[conference.Id] = conference;
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Conference conference, CancellationToken ct = default)
    {
        _items[conference.Id] = conference;
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAiSessionStore : IAiSessionStore
{
    private readonly ConcurrentDictionary<string, AiSession> _items = new(StringComparer.Ordinal);
    private readonly IAiRoleStore _roleStore;

    public InMemoryAiSessionStore(IAiRoleStore roleStore) => _roleStore = roleStore;

    public async Task<AiSession?> GetAsync(string id, CancellationToken ct = default) =>
        _items.TryGetValue(id, out var s) ? s : null;

    public async Task<AiSessionInfo?> GetInfoAsync(string id, CancellationToken ct = default)
    {
        if (_items.TryGetValue(id, out var s) is false) return null;
        var role = await _roleStore.GetByIdAsync(s.AiRoleId, ct);
        return ToInfo(s, role?.Name ?? string.Empty);
    }

    public async Task<IReadOnlyList<AiSessionInfo>> GetByConferenceAsync(string conferenceId, CancellationToken ct = default)
    {
        var list = _items.Values.Where(s => s.ConferenceId == conferenceId).OrderBy(s => s.CreatedAt).ToArray();
        var result = new List<AiSessionInfo>(list.Length);
        foreach (var s in list)
        {
            var role = await _roleStore.GetByIdAsync(s.AiRoleId, ct);
            result.Add(ToInfo(s, role?.Name ?? string.Empty));
        }
        return result;
    }

    public Task AddAsync(AiSession session, CancellationToken ct = default) { _items[session.Id] = session; return Task.CompletedTask; }
    public Task UpdateAsync(AiSession session, CancellationToken ct = default) { _items[session.Id] = session; return Task.CompletedTask; }

    private static AiSessionInfo ToInfo(AiSession s, string roleName) => new(
        s.Id, s.ConferenceId, s.AiRoleId, roleName, s.AgentInstance, s.Status, s.CustomPrompt, s.CreatedAt);
}
