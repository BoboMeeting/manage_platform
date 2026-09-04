using ManagerPlatform.Data;
using ManagerPlatform.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ManagerPlatform.Stores;

// ==================== EF Core (PostgreSQL) 实现 ====================
// 所有 Store 注册为 Scoped，与 DbContext 同生命周期。
// 并发控制：
//   - User/Room 的唯一性由 DB 唯一索引保证，AddAsync 捕获 23505 转为业务语义
//   - Conference "同房间至多一场非终态会议" 由部分唯一索引保证；
//     AddAsync 捕获 23505 重抛 InvalidOperationException，与原 InMemory 契约一致
//     （RoomEndpoints/{roomId}/join 已 catch InvalidOperationException 兜底重试）

public sealed class EfUserStore(AppDbContext db) : IUserStore
{
    private readonly AppDbContext _db = db;

    public Task<User?> GetByIdAsync(string id, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    // citext 列 = 即大小写不敏感比较
    public Task<User?> GetByAccountAsync(string account, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Account == account, ct);

    public async Task<IReadOnlyList<User>> FindAsync(
        string? keyword, UserStatus? status, UserRole? role, CancellationToken ct = default)
    {
        IQueryable<User> q = _db.Users;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            q = q.Where(u => EF.Functions.ILike(u.Account, "%" + kw + "%")
                          || EF.Functions.ILike(u.Nickname, "%" + kw + "%"));
        }
        if (status.HasValue) q = q.Where(u => u.Status == status.Value);
        if (role.HasValue) q = q.Where(u => u.Role == role.Value);
        return await q.OrderByDescending(u => u.CreatedAt).ToListAsync(ct);
    }

    public async Task<bool> AddAsync(User user, CancellationToken ct = default)
    {
        try
        {
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // 账号已存在
            return false;
        }
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        _db.Users.Update(user);
        await _db.SaveChangesAsync(ct);
    }

    public Task<int> CountAsync(CancellationToken ct = default) =>
        _db.Users.CountAsync(ct);
}

public sealed class EfRoomStore(AppDbContext db) : IRoomStore
{
    private readonly AppDbContext _db = db;

    public Task<MeetingRoom?> GetByIdAsync(string id, CancellationToken ct = default) =>
        _db.Rooms.FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<MeetingRoom?> GetByInviteCodeAsync(string inviteCode, CancellationToken ct = default) =>
        _db.Rooms.FirstOrDefaultAsync(r => r.InviteCode == inviteCode, ct);

    public async Task<IReadOnlyList<MeetingRoom>> FindAsync(
        string? hostUserId, MeetingRoomStatus? status, CancellationToken ct = default)
    {
        IQueryable<MeetingRoom> q = _db.Rooms;
        if (!string.IsNullOrEmpty(hostUserId)) q = q.Where(r => r.HostUserId == hostUserId);
        if (status.HasValue) q = q.Where(r => r.Status == status.Value);
        return await q.OrderByDescending(r => r.StartTime).ToListAsync(ct);
    }

    public async Task AddAsync(MeetingRoom room, CancellationToken ct = default)
    {
        try
        {
            _db.Rooms.Add(room);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new InvalidOperationException("房间名或邀请码已存在", ex);
        }
    }

    public async Task UpdateAsync(MeetingRoom room, CancellationToken ct = default)
    {
        _db.Rooms.Update(room);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<MeetingRoom>> GetAllAsync(CancellationToken ct = default) =>
        await _db.Rooms.OrderByDescending(r => r.StartTime).ToListAsync(ct);
}

public sealed class EfParticipantStore(AppDbContext db) : IParticipantStore
{
    private readonly AppDbContext _db = db;

    public Task<Participant?> GetByIdAsync(string id, CancellationToken ct = default) =>
        _db.Participants.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Participant>> GetByConferenceAsync(
        string conferenceId, CancellationToken ct = default) =>
        await _db.Participants
            .Where(p => p.ConferenceId == conferenceId)
            .OrderBy(p => p.JoinTime)
            .ToListAsync(ct);

    public async Task AddAsync(Participant p, CancellationToken ct = default)
    {
        _db.Participants.Add(p);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Participant p, CancellationToken ct = default)
    {
        _db.Participants.Update(p);
        await _db.SaveChangesAsync(ct);
    }

    public Task<int> CountActiveInConferenceAsync(string conferenceId, CancellationToken ct = default) =>
        _db.Participants.CountAsync(p =>
            p.ConferenceId == conferenceId && p.LeaveTime == null && !p.IsAi, ct);
}

public sealed class EfAiRoleStore(AppDbContext db) : IAiRoleStore
{
    private readonly AppDbContext _db = db;

    public Task<AIRole?> GetByIdAsync(string id, CancellationToken ct = default) =>
        _db.AiRoles.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<AIRole>> GetAllAsync(CancellationToken ct = default) =>
        await _db.AiRoles.OrderByDescending(r => r.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(AIRole role, CancellationToken ct = default)
    {
        _db.AiRoles.Add(role);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AIRole role, CancellationToken ct = default)
    {
        _db.AiRoles.Update(role);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        // 少量删除场景：先加载再 Remove，避免全表 DELETE
        var entity = await _db.AiRoles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity is not null)
        {
            _db.AiRoles.Remove(entity);
            await _db.SaveChangesAsync(ct);
        }
    }
}

public sealed class EfConferenceStore(AppDbContext db) : IConferenceStore
{
    private readonly AppDbContext _db = db;

    public Task<Conference?> GetByIdAsync(string id, CancellationToken ct = default) =>
        _db.Conferences.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Conference?> GetActiveByRoomAsync(string roomId, CancellationToken ct = default) =>
        _db.Conferences.FirstOrDefaultAsync(c =>
            c.RoomId == roomId && (
                c.Status == ConferenceStatus.Waiting
                || c.Status == ConferenceStatus.InProgress
                || c.Status == ConferenceStatus.PendingClose), ct);

    public async Task<IReadOnlyList<Conference>> GetByRoomAsync(string roomId, CancellationToken ct = default) =>
        await _db.Conferences
            .Where(c => c.RoomId == roomId)
            .OrderByDescending(c => c.StartedAt)
            .ToListAsync(ct);

    public async Task AddAsync(Conference conference, CancellationToken ct = default)
    {
        try
        {
            _db.Conferences.Add(conference);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // 部分唯一索引冲突：同房间已有非终态会议
            // 保持与 InMemoryConferenceStore 一致的契约（抛 InvalidOperationException）
            // 上层 RoomEndpoints/{roomId}/join 已 catch 兜底重新查询
            throw new InvalidOperationException("该房间已有进行中的会议", ex);
        }
    }

    public async Task UpdateAsync(Conference conference, CancellationToken ct = default)
    {
        _db.Conferences.Update(conference);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Conference>> GetAllNonTerminalAsync(CancellationToken ct = default) =>
        await _db.Conferences
            .Where(c => c.Status == ConferenceStatus.Waiting
                     || c.Status == ConferenceStatus.InProgress
                     || c.Status == ConferenceStatus.PendingClose)
            .ToListAsync(ct);
}

public sealed class EfAiSessionStore(AppDbContext db) : IAiSessionStore
{
    private readonly AppDbContext _db = db;

    public Task<AiSession?> GetAsync(string id, CancellationToken ct = default) =>
        _db.AiSessions.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<AiSessionInfo?> GetInfoAsync(string id, CancellationToken ct = default)
    {
        // JOIN 一次拿到 RoleName，避免 N+1
        var q = from s in _db.AiSessions
                join r in _db.AiRoles on s.AiRoleId equals r.Id into rs
                from r in rs.DefaultIfEmpty()
                where s.Id == id
                select new { s, RoleName = r != null ? r.Name : string.Empty };
        var x = await q.FirstOrDefaultAsync(ct);
        return x is null ? null : ToInfo(x.s, x.RoleName);
    }

    public async Task<IReadOnlyList<AiSessionInfo>> GetByConferenceAsync(
        string conferenceId, CancellationToken ct = default)
    {
        var q = from s in _db.AiSessions
                join r in _db.AiRoles on s.AiRoleId equals r.Id into rs
                from r in rs.DefaultIfEmpty()
                where s.ConferenceId == conferenceId
                orderby s.CreatedAt
                select new { s, RoleName = r != null ? r.Name : string.Empty };
        var list = await q.ToListAsync(ct);
        return list.Select(x => ToInfo(x.s, x.RoleName)).ToArray();
    }

    public async Task AddAsync(AiSession session, CancellationToken ct = default)
    {
        _db.AiSessions.Add(session);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AiSession session, CancellationToken ct = default)
    {
        _db.AiSessions.Update(session);
        await _db.SaveChangesAsync(ct);
    }

    private static AiSessionInfo ToInfo(AiSession s, string roleName) => new(
        s.Id, s.ConferenceId, s.AiRoleId, roleName, s.AgentInstance,
        s.Status, s.CustomPrompt, s.CreatedAt);
}

// ==================== 辅助：PostgreSQL 错误识别 ====================

internal static class PgExceptionExtensions
{
    /// <summary>
    /// 判断 DbUpdateException 是否为 PostgreSQL 唯一约束冲突（SQLSTATE 23505）。
    /// </summary>
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is NpgsqlException pgEx
        && pgEx.SqlState == PostgresErrorCodes.UniqueViolation;
}
