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

        // 预约访谈室（需登录）：主持人创建房间
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
                Status = MeetingRoomStatus.Scheduled,
                InviteCode = string.IsNullOrWhiteSpace(req.InviteCode)
                    ? Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()
                    : req.InviteCode,
            };
            await rooms.AddAsync(room, ct);

            return Results.Created($"/api/rooms/{room.Id}", ToSummary(room));
        }).RequireAuthorization();

        // 列出我的房间
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
        // v2.0：获取/创建当前进行中的会议场次，参会者归属场次
        group.MapGet("/{roomId}/join", async (
            string roomId,
            string? nickname,
            HttpContext ctx,
            IRoomStore rooms,
            IConferenceStore conferences,
            IParticipantStore participants,
            IUserStore users,
            ILiveKitTokenService liveKit,
            IOptions<LiveKitOptions> lkOpt,
            CancellationToken ct) =>
        {
            var room = await rooms.GetByIdAsync(roomId, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            if (room.Status == MeetingRoomStatus.Cancelled || room.Status == MeetingRoomStatus.Closed)
                return Results.Conflict(new { error = "会议已结束或取消" });
            if (room.Locked) return Results.Conflict(new { error = "房间已锁定" });

            // 入会必须登录系统；匿名访客不再允许加入会议
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();

            var now = DateTimeOffset.UtcNow;
            // 时间窗口校验：提前 5 分钟可进入（用于主持人准备）
            if (now < room.StartTime.AddMinutes(-5) && room.Status == MeetingRoomStatus.Scheduled)
                return Results.Conflict(new { error = "会议尚未到开放时间" });
            // 预约时间窗口已结束 → 懒关闭房间并拒绝入会
            if (now > room.EndTime)
            {
                if (room.Status == MeetingRoomStatus.Open || room.Status == MeetingRoomStatus.Scheduled)
                {
                    room.Status = MeetingRoomStatus.Closed;
                    room.UpdatedAt = now;
                    await rooms.UpdateAsync(room, ct);
                }
                return Results.Conflict(new { error = "预约时间已结束" });
            }

            var user = await users.GetByIdAsync(cu.UserId, ct);
            if (user is null) return Results.NotFound(new { error = "user not found" });
            if (user.Status == UserStatus.Disabled) return Results.Forbid();

            string identity = user.Id;
            string displayName = string.IsNullOrWhiteSpace(nickname) ? user.Nickname : nickname;
            UserInfo? userInfo = AuthEndpoints.ToInfo(user);
            bool isHost = user.Id == room.HostUserId;

            // 房间 Scheduled → Open（窗口已开放）
            if (room.Status == MeetingRoomStatus.Scheduled)
            {
                room.Status = MeetingRoomStatus.Open;
                room.UpdatedAt = now;
                await rooms.UpdateAsync(room, ct);
            }

            // 获取/创建当前进行中会议场次：无则开启一场新会议
            var conf = await conferences.GetActiveByRoomAsync(room.Id, ct);
            if (conf is null)
            {
                conf = new Conference
                {
                    RoomId = room.Id,
                    StartedByUserId = user.Id,
                    Status = ConferenceStatus.InProgress,
                    StartedAt = now,
                };
                try
                {
                    await conferences.AddAsync(conf, ct);
                }
                catch (InvalidOperationException)
                {
                    // 并发下另一请求已为该房间创建进行中场次，复用之（对应 DB 唯一约束 23505）
                    conf = await conferences.GetActiveByRoomAsync(room.Id, ct);
                    if (conf is null) return Results.Conflict(new { error = "会议创建失败，请重试" });
                }
            }

            // 容量校验（仅统计人类在线参会者，按场次）
            var activeCount = await participants.CountActiveInConferenceAsync(conf.Id, ct);
            if (activeCount >= room.MaxParticipants)
                return Results.Conflict(new { error = "房间人数已满" });

            // 第一个入会者自动成为主持人（若与预约主持人不同，则以预约为准）
            var role = (isHost || activeCount == 0) ? ParticipantRole.Host : ParticipantRole.Member;

            var participant = new Participant
            {
                ConferenceId = conf.Id,
                UserId = cu.UserId,
                Nickname = displayName,
                JoinTime = now,
                IsAi = false,
                Role = role,
            };
            await participants.AddAsync(participant, ct);

            var token = liveKit.CreateClientToken(room.RoomName, identity, displayName, isHost: role == ParticipantRole.Host);

            return Results.Ok(new JoinRoomResponse(
                room.Id, conf.Id, room.RoomName, token, lkOpt.Value.Url, role == ParticipantRole.Host, userInfo));
        }).RequireAuthorization();

        // 列出该房间的所有会议场次（历史 + 当前）
        group.MapGet("/{id}/conferences", async (
            string id,
            HttpContext ctx,
            IRoomStore rooms,
            IConferenceStore conferences,
            IParticipantStore participants,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            var room = await rooms.GetByIdAsync(id, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            var list = await conferences.GetByRoomAsync(id, ct);
            var result = new List<ConferenceSummary>(list.Count);
            foreach (var c in list)
            {
                var count = await participants.CountActiveInConferenceAsync(c.Id, ct);
                result.Add(ToConfSummary(c, count));
            }
            return Results.Ok(result);
        }).RequireAuthorization();

        // 获取当前进行中的会议场次（无则 404）
        group.MapGet("/{id}/conferences/active", async (
            string id,
            HttpContext ctx,
            IRoomStore rooms,
            IConferenceStore conferences,
            IParticipantStore participants,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null) return Results.Unauthorized();
            var room = await rooms.GetByIdAsync(id, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            var conf = await conferences.GetActiveByRoomAsync(id, ct);
            if (conf is null) return Results.NotFound(new { error = "当前无进行中的会议" });
            var count = await participants.CountActiveInConferenceAsync(conf.Id, ct);
            return Results.Ok(ToConfSummary(conf, count));
        }).RequireAuthorization();

        // 添加 AI 角色（仅主持人）
        group.MapPost("/{roomId}/ai/add", async (
            string roomId,
            AddAiRequest req,
            HttpContext ctx,
            IRoomStore rooms,
            IConferenceStore conferences,
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

            // AI 归属当前进行中的会议场次；会议未开始则拒绝
            var conf = await conferences.GetActiveByRoomAsync(room.Id, ct);
            if (conf is null) return Results.Conflict(new { error = "会议尚未开始，请先入会开启会议" });

            var role = await aiRoles.GetByIdAsync(req.RoleId, ct);
            if (role is null) return Results.BadRequest(new { error = "AI 角色不存在" });

            var now = DateTimeOffset.UtcNow;
            var session = new AiSession
            {
                ConferenceId = conf.Id,
                AiRoleId = role.Id,
                AgentInstance = "agent-" + Guid.NewGuid().ToString("N")[..8],
                CustomPrompt = req.CustomPrompt,
                Status = AISessionStatus.Pending,
            };
            await aiSessions.AddAsync(session, ct);

            // 预登记 AI 参会者，便于客户端参会者列表展示
            var aiParticipant = new Participant
            {
                ConferenceId = conf.Id,
                Nickname = role.Name,
                IsAi = true,
                Role = ParticipantRole.Member,
                AiSessionId = session.Id,
                JoinTime = now,
            };
            await participants.AddAsync(aiParticipant, ct);

            // 真正的 Agent 入会由 Agent Service 异步处理；此处仅返回调度结果
            return Results.Created($"/api/rooms/{roomId}/ai/{session.Id}",
                new AiSessionInfo(session.Id, session.ConferenceId, session.AiRoleId, role.Name,
                    session.AgentInstance, session.Status, session.CustomPrompt, session.CreatedAt));
        }).RequireAuthorization();

        // 移除 AI（仅主持人）
        group.MapDelete("/{roomId}/ai/{aiSessionId}", async (
            string roomId,
            string aiSessionId,
            HttpContext ctx,
            IRoomStore rooms,
            IConferenceStore conferences,
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
            if (session is null) return Results.NotFound(new { error = "ai session not found" });

            // 校验该 AI 会话属于本房间（经由场次关联）
            var conf = await conferences.GetByIdAsync(session.ConferenceId, ct);
            if (conf is null || conf.RoomId != roomId)
                return Results.NotFound(new { error = "ai session not found" });

            session.Status = AISessionStatus.Ended;
            session.EndedAt = DateTimeOffset.UtcNow;
            await aiSessions.UpdateAsync(session, ct);

            // 标记 AI 参会者离会
            var ps = await participants.GetByConferenceAsync(conf.Id, ct);
            var aiP = ps.FirstOrDefault(p => p.AiSessionId == aiSessionId);
            if (aiP is not null)
            {
                aiP.LeaveTime = DateTimeOffset.UtcNow;
                await participants.UpdateAsync(aiP, ct);
            }

            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        // 查询当前会议中的 AI 列表
        group.MapGet("/{roomId}/ai", async (
            string roomId,
            IConferenceStore conferences,
            IAiSessionStore aiSessions,
            CancellationToken ct) =>
        {
            var conf = await conferences.GetActiveByRoomAsync(roomId, ct);
            if (conf is null) return Results.Ok(Array.Empty<AiSessionInfo>());
            var list = await aiSessions.GetByConferenceAsync(conf.Id, ct);
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

    private static ConferenceSummary ToConfSummary(Conference c, int activeCount) => new(
        c.Id, c.RoomId, c.StartedByUserId, c.Status, c.StartedAt, c.EndedAt, activeCount);
}
