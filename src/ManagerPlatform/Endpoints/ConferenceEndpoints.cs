using ManagerPlatform.Auth;
using ManagerPlatform.Models;
using ManagerPlatform.Stores;

namespace ManagerPlatform.Endpoints;

/// <summary>
/// 会议场次接口（Conference）。用户进入房间到离开算一场会议。
/// </summary>
public static class ConferenceEndpoints
{
    public static IEndpointRouteBuilder MapConferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conferences").WithTags("Conferences");

        // 会议场次详情（含房间摘要、在会人数）
        group.MapGet("/{id}", async (
            string id,
            IConferenceStore conferences,
            IRoomStore rooms,
            IParticipantStore participants,
            CancellationToken ct) =>
        {
            var conf = await conferences.GetByIdAsync(id, ct);
            if (conf is null) return Results.NotFound(new { error = "conference not found" });

            var room = await rooms.GetByIdAsync(conf.RoomId, ct);
            var count = await participants.CountActiveInConferenceAsync(conf.Id, ct);

            return Results.Ok(new
            {
                Id = conf.Id,
                RoomId = conf.RoomId,
                RoomTitle = room?.Title,
                RoomName = room?.RoomName,
                StartedByUserId = conf.StartedByUserId,
                Status = conf.Status,
                StartedAt = conf.StartedAt,
                EndedAt = conf.EndedAt,
                ActiveParticipantCount = count,
            });
        }).RequireAuthorization();

        // 该场次的参会者列表
        group.MapGet("/{id}/participants", async (
            string id,
            IConferenceStore conferences,
            IParticipantStore participants,
            CancellationToken ct) =>
        {
            var conf = await conferences.GetByIdAsync(id, ct);
            if (conf is null) return Results.NotFound(new { error = "conference not found" });
            var list = await participants.GetByConferenceAsync(conf.Id, ct);
            return Results.Ok(list);
        }).RequireAuthorization();

        // 离会：标记当前用户参会者离会；若为最后一个在会者，进入 PendingClose 宽限期
        group.MapPost("/{id}/leave", async (
            string id,
            HttpContext ctx,
            IConferenceStore conferences,
            IParticipantStore participants,
            IAiSessionStore aiSessions,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();

            var conf = await conferences.GetByIdAsync(id, ct);
            if (conf is null) return Results.NotFound(new { error = "conference not found" });
            if (conf.Status == ConferenceStatus.Ended || conf.Status == ConferenceStatus.Completed)
                return Results.Conflict(new { error = "会议已结束" });

            var ps = await participants.GetByConferenceAsync(conf.Id, ct);
            var me = ps.FirstOrDefault(p => p.UserId == cu.UserId && p.LeaveTime is null && !p.IsAi);
            if (me is null)
            {
                logger.LogWarning("离会失败：用户不在会议中，场次={ConfId}，用户={UserId}", id, cu.UserId);
                return Results.NotFound(new { error = "您未在此会议中" });
            }

            me.LeaveTime = DateTimeOffset.UtcNow;
            await participants.UpdateAsync(me, ct);

            // 若该场次已无在会者，进入 PendingClose 宽限期（60s 内可回场）
            var remaining = await participants.CountActiveInConferenceAsync(conf.Id, ct);
            var enteredPendingClose = false;
            if (remaining == 0 && conf.Status == ConferenceStatus.InProgress)
            {
                conf.Status = ConferenceStatus.PendingClose;
                conf.PendingCloseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60);
                await conferences.UpdateAsync(conf, ct);
                enteredPendingClose = true;
            }

            logger.LogInformation(
                "用户离会：场次={ConfId}，房间={RoomId}，用户={UserId}，剩余在会={Remaining}，进入宽限期={PendingClose}",
                conf.Id, conf.RoomId, cu.UserId, remaining, enteredPendingClose);
            return Results.Ok(new { ok = true, enteredPendingClose });
        }).RequireAuthorization();

        // 主动结束会议（Completed 唯一入口）：仅会议发起人或管理员角色可调用
        group.MapPost("/{id}/end", async (
            string id,
            HttpContext ctx,
            IConferenceStore conferences,
            IParticipantStore participants,
            IAiSessionStore aiSessions,
            ILogger<ApiLog> logger,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();

            var conf = await conferences.GetByIdAsync(id, ct);
            if (conf is null) return Results.NotFound(new { error = "conference not found" });
            if (conf.Status == ConferenceStatus.Ended || conf.Status == ConferenceStatus.Completed)
                return Results.Conflict(new { error = "会议已结束" });

            // 权限：会议发起人（StartedByUserId）或管理员（Operator+）可主动结束
            bool isHost = cu.UserId == conf.StartedByUserId;
            bool isAdmin = cu.IsAtLeast(UserRole.Operator);
            if (!isHost && !isAdmin)
            {
                logger.LogWarning("结束会议被拒：非发起人/管理员，场次={ConfId}，用户={UserId}，发起人={StartedBy}",
                    conf.Id, cu.UserId, conf.StartedByUserId);
                return Results.Forbid();
            }

            // 强制终态：Completed（主持人主动结束）
            var now = DateTimeOffset.UtcNow;
            conf.Status = ConferenceStatus.Completed;
            conf.EndedAt = now;
            conf.PendingCloseExpiresAt = null;
            conf.WaitingExpiresAt = null;

            // 标记所有人类参会者离会（保证计数归零）
            var pList = await participants.GetByConferenceAsync(conf.Id, ct);
            foreach (var p in pList.Where(p => p.LeaveTime is null && !p.IsAi))
            {
                p.LeaveTime = now;
                await participants.UpdateAsync(p, ct);
            }

            // 清理 AI 会话
            var aiList = await aiSessions.GetByConferenceAsync(conf.Id, ct);
            foreach (var info in aiList)
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

            logger.LogInformation("会议被主动结束：场次={ConfId}，房间={RoomId}，操作人={UserId}（{Role}）",
                conf.Id, conf.RoomId, cu.UserId, isHost ? "发起人" : "管理员");
            return Results.Ok(new { ok = true, endedReason = "completed" });
        }).RequireAuthorization();

        return app;
    }
}
