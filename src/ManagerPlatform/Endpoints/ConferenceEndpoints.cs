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

        // 离会：标记当前用户参会者离会；若为最后一个在会者，自动结束该场次
        group.MapPost("/{id}/leave", async (
            string id,
            HttpContext ctx,
            IConferenceStore conferences,
            IParticipantStore participants,
            IAiSessionStore aiSessions,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();

            var conf = await conferences.GetByIdAsync(id, ct);
            if (conf is null) return Results.NotFound(new { error = "conference not found" });
            if (conf.Status == ConferenceStatus.Ended)
                return Results.Conflict(new { error = "会议已结束" });

            var ps = await participants.GetByConferenceAsync(conf.Id, ct);
            var me = ps.FirstOrDefault(p => p.UserId == cu.UserId && p.LeaveTime is null && !p.IsAi);
            if (me is null) return Results.NotFound(new { error = "您未在此会议中" });

            me.LeaveTime = DateTimeOffset.UtcNow;
            await participants.UpdateAsync(me, ct);

            // 若该场次已无在会者，结束会议并清理其下 AI 会话
            var remaining = await participants.CountActiveInConferenceAsync(conf.Id, ct);
            var conferenceEnded = false;
            if (remaining == 0)
            {
                conf.Status = ConferenceStatus.Ended;
                conf.EndedAt = DateTimeOffset.UtcNow;
                await conferences.UpdateAsync(conf, ct);

                var aiList = await aiSessions.GetByConferenceAsync(conf.Id, ct);
                foreach (var info in aiList)
                {
                    var s = await aiSessions.GetAsync(info.Id, ct);
                    if (s is not null && s.Status != AISessionStatus.Ended)
                    {
                        s.Status = AISessionStatus.Ended;
                        s.EndedAt = DateTimeOffset.UtcNow;
                        await aiSessions.UpdateAsync(s, ct);
                    }
                }
                conferenceEnded = true;
            }

            return Results.Ok(new { ok = true, conferenceEnded });
        }).RequireAuthorization();

        return app;
    }
}
