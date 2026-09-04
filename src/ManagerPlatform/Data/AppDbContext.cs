using ManagerPlatform.Models;
using Microsoft.EntityFrameworkCore;

namespace ManagerPlatform.Data;

/// <summary>
/// EF Core 数据上下文。所有实体使用 string 主键（Guid N 32 位）。
/// 关键约束：
///   - User.Account 使用 citext，保持与原 InMemory 大小写不敏感语义
///   - Conference 部分唯一索引：同房间至多一场非终态会议（替代原内存锁）
///   - MeetingRoom.InviteCode 部分唯一索引（NULL 不冲突）
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<MeetingRoom> Rooms => Set<MeetingRoom>();
    public DbSet<Conference> Conferences => Set<Conference>();
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<AIRole> AiRoles => Set<AIRole>();
    public DbSet<AiSession> AiSessions => Set<AiSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // ===== User =====
        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasMaxLength(32);
            // citext：账号大小写不敏感（需在迁移中 CREATE EXTENSION citext）
            e.Property(u => u.Account).HasColumnType("citext").HasMaxLength(128).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
            e.Property(u => u.Nickname).HasMaxLength(128).IsRequired();
            e.Property(u => u.AvatarUrl).HasMaxLength(512);
            e.HasIndex(u => u.Account).IsUnique();
            e.HasIndex(u => u.Status);
            e.HasIndex(u => u.Role);
        });

        // ===== MeetingRoom =====
        b.Entity<MeetingRoom>(e =>
        {
            e.ToTable("meeting_rooms");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(32);
            e.Property(r => r.Title).HasMaxLength(256).IsRequired();
            e.Property(r => r.HostUserId).HasMaxLength(32).IsRequired();
            e.Property(r => r.HostNickname).HasMaxLength(128).IsRequired();
            e.Property(r => r.RoomName).HasMaxLength(64).IsRequired();
            e.Property(r => r.InviteCode).HasMaxLength(32);
            // RoomName 唯一（LiveKit 房间名不可重复）
            e.HasIndex(r => r.RoomName).IsUnique();
            // InviteCode 部分唯一索引：NULL 不参与唯一性约束
            e.HasIndex(r => r.InviteCode)
                .IsUnique()
                .HasFilter("\"InviteCode\" IS NOT NULL");
            e.HasIndex(r => r.HostUserId);
            e.HasIndex(r => r.Status);
            e.HasIndex(r => r.StartTime);
        });

        // ===== Conference =====
        b.Entity<Conference>(e =>
        {
            e.ToTable("conferences");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasMaxLength(32);
            e.Property(c => c.RoomId).HasMaxLength(32).IsRequired();
            e.Property(c => c.StartedByUserId).HasMaxLength(32).IsRequired();
            e.HasIndex(c => c.RoomId);
            e.HasIndex(c => c.StartedByUserId);
            e.HasIndex(c => c.Status);
            // 部分唯一索引：同房间至多一场非终态会议
            // ConferenceStatus: Waiting=0, InProgress=1, PendingClose=2
            // 替代 InMemoryConferenceStore._createLock 的并发控制
            e.HasIndex(c => c.RoomId)
                .IsUnique()
                .HasFilter("\"Status\" IN (0, 1, 2)")
                .HasDatabaseName("ix_conferences_active_per_room");
        });

        // ===== Participant =====
        b.Entity<Participant>(e =>
        {
            e.ToTable("participants");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasMaxLength(32);
            e.Property(p => p.ConferenceId).HasMaxLength(32).IsRequired();
            e.Property(p => p.UserId).HasMaxLength(32);
            e.Property(p => p.Nickname).HasMaxLength(128).IsRequired();
            e.Property(p => p.AiSessionId).HasMaxLength(32);
            e.HasIndex(p => p.ConferenceId);
            e.HasIndex(p => p.UserId);
            e.HasIndex(p => p.AiSessionId);
            // 复合索引：用于 CountActiveInConferenceAsync 等按场次过滤的查询
            e.HasIndex(p => new { p.ConferenceId, p.IsAi, p.LeaveTime });
        });

        // ===== AIRole =====
        b.Entity<AIRole>(e =>
        {
            e.ToTable("ai_roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasMaxLength(32);
            e.Property(r => r.Name).HasMaxLength(64).IsRequired();
            e.Property(r => r.PromptTemplate).IsRequired();
            e.Property(r => r.TtsConfig).HasMaxLength(1024);
            e.Property(r => r.AvatarUrl).HasMaxLength(512);
            e.Property(r => r.CreatedBy).HasMaxLength(32).IsRequired();
            e.HasIndex(r => r.Name);
        });

        // ===== AiSession =====
        b.Entity<AiSession>(e =>
        {
            e.ToTable("ai_sessions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasMaxLength(32);
            e.Property(s => s.ConferenceId).HasMaxLength(32).IsRequired();
            e.Property(s => s.AiRoleId).HasMaxLength(32).IsRequired();
            e.Property(s => s.AgentInstance).HasMaxLength(64).IsRequired();
            e.Property(s => s.CustomPrompt).HasMaxLength(4000);
            e.HasIndex(s => s.ConferenceId);
            e.HasIndex(s => s.AiRoleId);
            e.HasIndex(s => s.Status);
        });
    }
}
