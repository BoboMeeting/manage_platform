using ManagerPlatform.Auth;
using ManagerPlatform.Models;
using ManagerPlatform.Stores;

namespace ManagerPlatform.Endpoints;

/// <summary>
/// 管理后台接口：用户/会议/权限管理。
/// 组级策略 OperatorPlus（运营及以上，见 Program.cs）；
/// 角色变更（权限管理）在端点内额外要求超级管理员。
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin")
            .RequireAuthorization(Policies.OperatorPlus);

        // ===== 用户管理 =====

        group.MapGet("/users", async (
            string? keyword,
            UserStatus? status,
            UserRole? role,
            HttpContext ctx,
            IUserStore users,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is null)
                return Results.Unauthorized();
            var list = await users.FindAsync(keyword, status, role, ct);
            return Results.Ok(new PagedResult<UserInfo>(list.Select(AuthEndpoints.ToInfo).ToArray(), list.Count, 1, list.Count));
        });

        // 创建用户（策略已保证 Operator+）
        group.MapPost("/users/create", async (
            AdminCreateUserRequest req,
            HttpContext ctx,
            IUserStore users,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Account) || string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 6)
                return Results.BadRequest(new { error = "account 必填，密码长度至少 6 位" });
            if (await users.GetByAccountAsync(req.Account, ct) is not null)
                return Results.Conflict(new { error = "账号已存在" });

            // 校验角色枚举合法性
            if (!Enum.IsDefined(req.Role))
                return Results.BadRequest(new { error = "无效的角色值" });

            // 只能创建权限低于自身的用户
            if (req.Role >= cu.Role)
                return Results.Forbid();

            var kind = req.AccountKind ?? (req.Account.Contains('@') ? AccountKind.Email : AccountKind.Phone);
            var user = new User
            {
                Account = req.Account,
                AccountKind = kind,
                Nickname = string.IsNullOrWhiteSpace(req.Nickname) ? req.Account : req.Nickname,
                PasswordHash = PasswordHasher.Hash(req.Password),
                Role = req.Role,
                Status = UserStatus.Active,
            };
            if (!await users.AddAsync(user, ct))
                return Results.Conflict(new { error = "账号已存在" });
            return Results.Created($"/api/admin/users/{user.Id}", AuthEndpoints.ToInfo(user));
        });

        // 更新用户（昵称/状态）；角色变更是请求相关条件，需在端点内要求超管
        group.MapPatch("/users/{id}", async (
            string id,
            AdminUpdateUserRequest req,
            HttpContext ctx,
            IUserStore users,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();

            var user = await users.GetByIdAsync(id, ct);
            if (user is null) return Results.NotFound(new { error = "user not found" });

            // 非自身时，目标角色必须低于自身
            if (cu.UserId != id && user.Role >= cu.Role)
                return Results.Forbid();

            if (req.Nickname is not null) user.Nickname = req.Nickname;
            if (req.Status.HasValue) user.Status = req.Status.Value;
            if (req.Role.HasValue)
            {
                // 角色变更（权限管理）仅超级管理员
                if (cu.Role != UserRole.SuperAdmin) return Results.Forbid();
                user.Role = req.Role.Value;
            }
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await users.UpdateAsync(user, ct);
            return Results.Ok(AuthEndpoints.ToInfo(user));
        });

        // 重置密码（策略已保证 Operator+）
        group.MapPost("/users/{id}/reset-password", async (
            string id,
            AdminResetPasswordRequest req,
            HttpContext ctx,
            IUserStore users,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
                return Results.BadRequest(new { error = "新密码长度至少 6 位" });

            var user = await users.GetByIdAsync(id, ct);
            if (user is null) return Results.NotFound(new { error = "user not found" });

            // 超管可重置任何人密码；Operator可重置自身及低等级用户；其他角色只能重置自己的密码
            if (cu.Role == UserRole.SuperAdmin)
            {
                // 超管可重置任何人
            }
            else if (cu.Role == UserRole.Operator)
            {
                // Operator：自身或低等级用户
                if (cu.UserId != id && user.Role >= cu.Role)
                    return Results.Forbid();
            }
            else
            {
                // 其他角色只能重置自己的密码
                if (cu.UserId != id)
                    return Results.Forbid();
            }

            user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
            user.UpdatedAt = DateTimeOffset.UtcNow;
            await users.UpdateAsync(user, ct);
            return Results.Ok(new { ok = true });
        });

        // 禁用/启用账户（策略已保证 Operator+）
        group.MapPost("/users/{id}/disable", async (string id, HttpContext ctx, IUserStore users, CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();
            return await SetStatus(id, UserStatus.Disabled, cu, users, ct);
        });
        group.MapPost("/users/{id}/enable", async (string id, HttpContext ctx, IUserStore users, CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();
            return await SetStatus(id, UserStatus.Active, cu, users, ct);
        });

        // ===== 会议管理 =====

        group.MapGet("/rooms", async (MeetingStatus? status, IRoomStore rooms, CancellationToken ct) =>
        {
            var list = status.HasValue
                ? await rooms.FindAsync(hostUserId: null, status, ct)
                : await rooms.GetAllAsync(ct);
            return Results.Ok(list);
        });

        group.MapDelete("/rooms/{id}", async (
            string id,
            IRoomStore rooms,
            CancellationToken ct) =>
        {
            var room = await rooms.GetByIdAsync(id, ct);
            if (room is null) return Results.NotFound(new { error = "room not found" });
            room.Status = MeetingStatus.Cancelled;
            room.UpdatedAt = DateTimeOffset.UtcNow;
            await rooms.UpdateAsync(room, ct);
            return Results.Ok(new { ok = true });
        });

        return app;
    }

    private static async Task<IResult> SetStatus(
        string id, UserStatus status, CurrentUser cu, IUserStore users, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(id, ct);
        if (user is null) return Results.NotFound(new { error = "user not found" });

        // 不能管理自己
        if (cu.UserId == id)
            return Results.Forbid();
        // 超管可管理所有人；其他角色只能管理低权限用户
        if (cu.Role != UserRole.SuperAdmin && user.Role >= cu.Role)
            return Results.Forbid();

        user.Status = status;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await users.UpdateAsync(user, ct);
        return Results.Ok(AuthEndpoints.ToInfo(user));
    }
}
