using ManagerPlatform.Auth;
using ManagerPlatform.LiveKit;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Stores;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.Endpoints;

public static class RoomEndpoints
{
    public static IEndpointRouteBuilder MapRoomEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/rooms").WithTags("Rooms");

        // 预约访谈室（需登录）：主持人创建会议
        group.MapPost("/create", async (
            CreateRoomRequest req,
            HttpContext ctx,
            IUserStore users,
            IRoomStore rooms,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "title 必填" });

            var start = req.StartTime ?? DateTimeOffset.UtcNow;
            var duration = req.DurationSeconds ?? 3600;
            if (duration <= 0) return Results.BadRequest(new { error = "durationSeconds 必须大于 0" });
            var max = req.MaxParticipants ?? 50;
            if (max <= 0) return Results.BadRequest(new { error = "maxParticipants 必须大于 0" });

            var user = await users.GetByIdAsync(cu.UserId, ct);
            if (user is null) return Results.NotFound(new { error = "user not found" });
            if (user.Status == UserStatus.Disabled) return Results.Forbid();

            var room = new MeetingRoom
            {
                Title = req.Title.Trim(),
                HostUserId = user.Id,
                HostNickname = user.Nickname,
                RoomName = Guid.NewGuid().ToString("N"), // LiveKit 房间名
                StartTime = start,
                DurationSeconds = duration,
                MaxParticipants = max,
                Status = MeetingStatus.Scheduled,
                InviteCode = string.IsNullOrWhiteSpace(req.InviteCode)
                    ? Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()
                    : req.InviteCode,
            };
            await rooms.AddAsync(room, ct);

            return Results.Created($"/api/rooms/{room.Id}", ToSummary(room));
        }).RequireAuthorization();

        // 列出我的会议
        group.MapGet("/", async (HttpContext ctx, IRoomStore rooms, CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var list = await rooms.FindAsync(cu.UserId, status: null, ct);
            return Results.Ok(list.Select(ToSummary).ToArray());
        }).RequireAuthorization();

        group.MapGet("/{id}", async (string id, IRoomStore rooms, CancellationToken ct) =>
        {
            var room = await rooms.GetByIdAsync(id, ct);
            return room is null ? Results.NotFound(new { error = "room not found" }) : Results.Ok(ToSummary(room));
        }).RequireAuthorization();

        // 通过邀请码查询
        group.MapGet("/invite/{code}", async (string code, IRoomStore rooms, CancellationToken ct) =>
        {
            var room = await rooms.GetByInviteCodeAsync(code, ct);
            return room is null ? Results.NotFound(new { error = "invite code invalid" }) : Results.Ok(ToSummary(room));
        });

        // 获取入会 token（核心接口：客户端入会）
        // 设计文档: GET /api/rooms/{roomId}/join
        group.MapGet("/{roomId}/join", async (
            string roomId,
            string? nickname,
            HttpContext ctx,
            IRoomStore rooms,
            IParticipantStore participants,
            IUserStore users,
            ILiveKitTokenService liveKit,
            IOptions<LiveKitOptions> lkOpt,
            CancellationToken ct) =>
        {
            var room = await rooms.GetByIdAsync(roomId, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            if (room.Status == MeetingStatus.Cancelled || room.Status == MeetingStatus.Ended)
                return Results.Conflict(new { error = "会议已结束或取消" });
            if (room.Locked) return Results.Conflict(new { error = "房间已锁定" });

            // 入会必须登录系统；匿名访客不再允许加入会议
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();

            var now = DateTimeOffset.UtcNow;
            // 仅在到达开始时间附近允许进入；提前 5 分钟可加入（用于主持人准备）
            if (now < room.StartTime.AddMinutes(-5) && room.Status == MeetingStatus.Scheduled)
                return Results.Conflict(new { error = "会议尚未到开放时间" });

            var user = await users.GetByIdAsync(cu.UserId, ct);
            if (user is null) return Results.NotFound(new { error = "user not found" });
            if (user.Status == UserStatus.Disabled) return Results.Forbid();

            string identity = user.Id;
            string displayName = string.IsNullOrWhiteSpace(nickname) ? user.Nickname : nickname;
            UserInfo? userInfo = AuthEndpoints.ToInfo(user);
            bool isHost = user.Id == room.HostUserId;

            // 容量校验（仅统计人类在线参会者）
            var activeCount = await participants.CountActiveInRoomAsync(room.Id, ct);
            if (activeCount >= room.MaxParticipants)
                return Results.Conflict(new { error = "房间人数已满" });

            // 第一个入会者自动成为主持人（若与预约主持人不同，则以预约为准）
            var role = (isHost || activeCount == 0) ? ParticipantRole.Host : ParticipantRole.Member;

            // 房间进入"进行中"
            if (room.Status == MeetingStatus.Scheduled)
            {
                room.Status = MeetingStatus.InProgress;
                room.UpdatedAt = now;
                await rooms.UpdateAsync(room, ct);
            }

            var participant = new Participant
            {
                RoomId = room.Id,
                UserId = cu?.UserId,
                Nickname = displayName,
                JoinTime = now,
                IsAi = false,
                Role = role,
            };
            await participants.AddAsync(participant, ct);

            var token = liveKit.CreateClientToken(room.RoomName, identity, displayName, isHost: role == ParticipantRole.Host);

            return Results.Ok(new JoinRoomResponse(
                room.Id, room.RoomName, token, lkOpt.Value.Url, role == ParticipantRole.Host, userInfo));
        }).RequireAuthorization();

        // 添加 AI 角色（仅主持人）
        group.MapPost("/{roomId}/ai/add", async (
            string roomId,
            AddAiRequest req,
            HttpContext ctx,
            IRoomStore rooms,
            IAiRoleStore aiRoles,
            IAiSessionStore aiSessions,
            IParticipantStore participants,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var room = await rooms.GetByIdAsync(roomId, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });

            // 校验权限：必须是会议主持人或管理员
            if (cu.UserId != room.HostUserId && !cu.IsAtLeast(UserRole.Operator))
                return Results.Forbid();

            var role = await aiRoles.GetByIdAsync(req.RoleId, ct);
            if (role is null) return Results.BadRequest(new { error = "AI 角色不存在" });

            var now = DateTimeOffset.UtcNow;
            var session = new AiSession
            {
                RoomId = room.Id,
                AiRoleId = role.Id,
                AgentInstance = "agent-" + Guid.NewGuid().ToString("N")[..8],
                CustomPrompt = req.CustomPrompt,
                Status = AISessionStatus.Pending,
            };
            await aiSessions.AddAsync(session, ct);

            // 预登记 AI 参会者，便于客户端参会者列表展示
            var aiParticipant = new Participant
            {
                RoomId = room.Id,
                Nickname = role.Name,
                IsAi = true,
                Role = ParticipantRole.Member,
                AiSessionId = session.Id,
                JoinTime = now,
            };
            await participants.AddAsync(aiParticipant, ct);

            // 真正的 Agent 入会由 Agent Service 异步处理；此处仅返回调度结果
            return Results.Created($"/api/rooms/{roomId}/ai/{session.Id}",
                new AiSessionInfo(session.Id, session.RoomId, session.AiRoleId, role.Name,
                    session.AgentInstance, session.Status, session.CustomPrompt, session.CreatedAt));
        }).RequireAuthorization();

        // 移除 AI（仅主持人）
        group.MapDelete("/{roomId}/ai/{aiSessionId}", async (
            string roomId,
            string aiSessionId,
            HttpContext ctx,
            IRoomStore rooms,
            IAiSessionStore aiSessions,
            IParticipantStore participants,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var room = await rooms.GetByIdAsync(roomId, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            if (cu.UserId != room.HostUserId && !cu.IsAtLeast(UserRole.Operator))
                return Results.Forbid();

            var session = await aiSessions.GetAsync(aiSessionId, ct);
            if (session is null || session.RoomId != roomId)
                return Results.NotFound(new { error = "ai session not found" });

            session.Status = AISessionStatus.Ended;
            session.EndedAt = DateTimeOffset.UtcNow;
            await aiSessions.UpdateAsync(session, ct);

            // 标记 AI 参会者离会
            var ps = await participants.GetByRoomAsync(roomId, ct);
            var aiP = ps.FirstOrDefault(p => p.AiSessionId == aiSessionId);
            if (aiP is not null)
            {
                aiP.LeaveTime = DateTimeOffset.UtcNow;
                await participants.UpdateAsync(aiP, ct);
            }

            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // 查询会议中的 AI 列表
        group.MapGet("/{roomId}/ai", async (
            string roomId,
            IAiSessionStore aiSessions,
            CancellationToken ct) =>
        {
            var list = await aiSessions.GetByRoomAsync(roomId, ct);
            return Results.Ok(list);
        }).RequireAuthorization();

        // 主持人锁定/解锁房间
        group.MapPost("/{roomId}/lock", async (
            string roomId,
            HttpContext ctx,
            IRoomStore rooms,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var room = await rooms.GetByIdAsync(roomId, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            if (cu.UserId != room.HostUserId && !cu.IsAtLeast(UserRole.Operator))
                return Results.Forbid();
            room.Locked = true;
            room.UpdatedAt = DateTimeOffset.UtcNow;
            await rooms.UpdateAsync(room, ct);
            return Results.Ok(ToSummary(room));
        }).RequireAuthorization();

        group.MapPost("/{roomId}/unlock", async (
            string roomId,
            HttpContext ctx,
            IRoomStore rooms,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var room = await rooms.GetByIdAsync(roomId, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            if (cu.UserId != room.HostUserId && !cu.IsAtLeast(UserRole.Operator))
                return Results.Forbid();
            room.Locked = false;
            room.UpdatedAt = DateTimeOffset.UtcNow;
            await rooms.UpdateAsync(room, ct);
            return Results.Ok(ToSummary(room));
        }).RequireAuthorization();

        return app;
    }

    private static RoomSummary ToSummary(MeetingRoom r) => new(
        r.Id, r.Title, r.RoomName, r.HostUserId, r.HostNickname,
        r.StartTime, r.EndTime, r.MaxParticipants, r.Status, r.Locked, r.InviteCode, r.CreatedAt);
}
