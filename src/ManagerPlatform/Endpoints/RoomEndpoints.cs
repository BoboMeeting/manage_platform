using ManagerPlatform.Auth;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Services;
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
                RoomName = Random.Shared.Next(100000000, 1000000000).ToString(), // 9 位随机会议号（参考腾讯会议号）
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
        group.MapGet("/", async (HttpContext ctx, IRoomStore rooms, IConferenceStore conferences, CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu) return Results.Unauthorized();
            var list = await rooms.FindAsync(cu.UserId, status: null, ct);
            var now = DateTimeOffset.UtcNow;
            var summaries = new List<RoomSummary>(list.Count);
            foreach (var room in list)
            {
                var conf = await conferences.GetActiveByRoomAsync(room.Id, ct);
                summaries.Add(ToSummary(room, now, conf));
            }
            return Results.Ok(summaries);
        }).RequireAuthorization();

        group.MapGet("/{id}", async (string id, IRoomStore rooms, IConferenceStore conferences, CancellationToken ct) =>
        {
            var room = await rooms.GetByIdAsync(id, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            var conf = await conferences.GetActiveByRoomAsync(room.Id, ct);
            return Results.Ok(ToSummary(room, DateTimeOffset.UtcNow, conf));
        }).RequireAuthorization();

        // 通过邀请码查询
        group.MapGet("/invite/{code}", async (string code, IRoomStore rooms, IConferenceStore conferences, CancellationToken ct) =>
        {
            var room = await rooms.GetByInviteCodeAsync(code, ct);
            if (room is null) return Results.NotFound(new { error = "invite code invalid" });
            var conf = await conferences.GetActiveByRoomAsync(room.Id, ct);
            return Results.Ok(ToSummary(room, DateTimeOffset.UtcNow, conf));
        });

        // 获取入会凭证（核心接口：客户端入会）
        // 设计文档: GET /api/rooms/{roomId}/join
        // v3.0：管理平台只做校验/场次管理，LiveKit 媒体层由调度服务负责：
        //       调调度服务内部接口创建媒体房间并换取房间凭证(ticket)，
        //       客户端再凭【用户 JWT + ticket】调调度服务外部接口换取 LiveKit Token。
        group.MapGet("/{roomId}/join", async (
            string roomId,
            string? nickname,
            HttpContext ctx,
            IRoomStore rooms,
            IConferenceStore conferences,
            IParticipantStore participants,
            IUserStore users,
            IAiSessionStore aiSessions,
            ISchedulerClient scheduler,
            IOptions<SchedulerOptions> schedulerOpt,
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

            // 获取/创建当前非终态会议场次：无则开启一场新会议
            var conf = await conferences.GetActiveByRoomAsync(room.Id, ct);

            // 懒超时检查：若取到的场次已经超时，先终态化后丢弃重开
            if (conf is not null)
            {
                var stale = false;
                if (conf.Status == ConferenceStatus.Waiting
                    && conf.WaitingExpiresAt.HasValue
                    && now > conf.WaitingExpiresAt.Value)
                {
                    conf.Status = ConferenceStatus.Ended;
                    conf.EndedAt = now;
                    await conferences.UpdateAsync(conf, ct);
                    stale = true;
                }
                else if (conf.Status == ConferenceStatus.PendingClose
                         && conf.PendingCloseExpiresAt.HasValue
                         && now > conf.PendingCloseExpiresAt.Value)
                {
                    conf.Status = ConferenceStatus.Ended;
                    conf.EndedAt = now;
                    // 清理挂起 AI 会话
                    var staleAiList = await aiSessions.GetByConferenceAsync(conf.Id, ct);
                    foreach (var info in staleAiList)
                    {
                        var s = await aiSessions.GetAsync(info.Id, ct);
                        if (s is not null && s.Status != AISessionStatus.Ended)
                        {
                            s.Status = AISessionStatus.Ended;
                            s.EndedAt = now;
                            await aiSessions.UpdateAsync(s, ct);
                        }
                    }
                    await conferences.UpdateAsync(conf, ct);
                    stale = true;
                }

                if (stale) conf = null;
            }

            if (conf is null)
            {
                conf = new Conference
                {
                    RoomId = room.Id,
                    StartedByUserId = user.Id,
                    Status = ConferenceStatus.Waiting,
                    StartedAt = now,
                    WaitingExpiresAt = now.AddSeconds(30), // 30s 内无人入会 → 后台 Ended
                };
                try
                {
                    await conferences.AddAsync(conf, ct);
                }
                catch (InvalidOperationException)
                {
                    // 并发下另一请求已为该房间创建非终态场次，复用之（对应 DB 唯一约束 23505）
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

            // 调用调度服务内部接口：幂等创建 LiveKit 媒体房间 + 签发房间凭证(ticket)。
            // 放在场次状态落库/参会者登记之前：调度失败时不产生脏数据，
            // 场次保持 Waiting（30s 超时）/PendingClose（60s 宽限）由清理任务终态化。
            SchedulerRoomTicket ticket;
            try
            {
                ticket = await scheduler.CreateRoomTicketAsync(
                    room.RoomName, conf.Id, identity, displayName,
                    isHost: role == ParticipantRole.Host, ct: ct);
            }
            catch (SchedulerException ex)
            {
                return Results.Json(
                    new { error = $"调度服务暂不可用，请稍后重试（{ex.Message}）" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            //TODO: 应该通过调度的回调来检测用户真正入会或者离会
            // 状态修复：根据当前场次状态恢复到可入会的 InProgress
            // 1) Waiting → 首位用户加入，切换到 InProgress
            // 2) PendingClose → 用户在宽限期内回来，恢复到 InProgress
            var wasWaiting = conf.Status == ConferenceStatus.Waiting;
            if (wasWaiting || conf.Status == ConferenceStatus.PendingClose)
            {
                conf.Status = ConferenceStatus.InProgress;
                if (wasWaiting)
                    conf.StartedAt = now; // 真正的会议开始时间以首次入会为准
                conf.PendingCloseExpiresAt = null;
                await conferences.UpdateAsync(conf, ct);
            }

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

            // 返回调度服务外部地址 + 房间凭证；客户端凭【用户 JWT + ticket】
            // 调调度服务 /api/v1/external/rooms/join 换取 LiveKit Url + Token
            return Results.Ok(new JoinRoomResponse(
                room.Id,
                conf.Id,
                room.RoomName,
                schedulerOpt.Value.EffectiveExternalBaseUrl,
                ticket.Ticket,
                role == ParticipantRole.Host,
                userInfo));
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
            IConferenceStore conferences,
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
            var conf = await conferences.GetActiveByRoomAsync(room.Id, ct);
            return Results.Ok(ToSummary(room, DateTimeOffset.UtcNow, conf));
        }).RequireAuthorization();

        group.MapPost("/{roomId}/unlock", async (
            string roomId,
            HttpContext ctx,
            IRoomStore rooms,
            IConferenceStore conferences,
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
            var conf = await conferences.GetActiveByRoomAsync(room.Id, ct);
            return Results.Ok(ToSummary(room, DateTimeOffset.UtcNow, conf));
        }).RequireAuthorization();

        return app;
    }

    private static RoomSummary ToSummary(MeetingRoom r) => ToSummary(r, DateTimeOffset.UtcNow, null);

    private static RoomSummary ToSummary(MeetingRoom r, DateTimeOffset now, Conference? activeConf)
    {
        var status = ComputeEffectiveStatus(r, now);
        var statusStr = ComputeStatusStr(status, activeConf);
        return new RoomSummary(
            r.Id, r.Title, r.RoomName, r.HostUserId, r.HostNickname,
            r.StartTime, r.EndTime, r.MaxParticipants, status, statusStr, r.Locked, r.InviteCode, r.CreatedAt);
    }

    /// <summary>
    /// 根据当前时间虚算会议状态：Cancelled 为终态不变；超过 EndTime → Closed；
    /// 已到 StartTime 但仍未被join（仍为 Scheduled）→ Open。不落库，仅影响显示。
    /// </summary>
    private static MeetingRoomStatus ComputeEffectiveStatus(MeetingRoom r, DateTimeOffset now)
    {
        if (r.Status == MeetingRoomStatus.Cancelled) return r.Status;
        if (now > r.EndTime) return MeetingRoomStatus.Closed;
        if (r.Status == MeetingRoomStatus.Scheduled && now >= r.StartTime)
            return MeetingRoomStatus.Open;
        return r.Status;
    }

    /// <summary>
    /// 计算 StatusStr 显示文案：
    /// Scheduled=已预约 / Closed=会议已结束 / Cancelled=会议已取消；
    /// Open 时根据是否存在进行中的 Conference 区分 待开始 / 进行中。
    /// </summary>
    private static string ComputeStatusStr(MeetingRoomStatus status, Conference? activeConf)
    {
        return status switch
        {
            MeetingRoomStatus.Scheduled => "已预约",
            MeetingRoomStatus.Closed => "会议已结束",
            MeetingRoomStatus.Cancelled => "会议已取消",
            MeetingRoomStatus.Open => activeConf is { Status: ConferenceStatus.InProgress } ? "进行中" : "待开始",
            _ => string.Empty,
        };
    }

    private static ConferenceSummary ToConfSummary(Conference c, int activeCount) => new(
        c.Id, c.RoomId, c.StartedByUserId, c.Status, c.StartedAt, c.EndedAt, activeCount);
}
