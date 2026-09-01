using ManagerPlatform.Auth;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Stores;
using Microsoft.Extensions.Options;

namespace ManagerPlatform.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest req,
            IUserStore users,
            JwtTokenService jwt,
            IOptions<JwtOptions> jwtOpt,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Account) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "account/password 必填" });
            if (req.Password.Length < 6)
                return Results.BadRequest(new { error = "密码长度至少 6 位" });

            if (await users.GetByAccountAsync(req.Account, ct) is not null)
                return Results.Conflict(new { error = "账号已存在" });

            var kind = req.AccountKind ?? (req.Account.Contains('@') ? AccountKind.Email : AccountKind.Phone);
            var user = new User
            {
                Account = req.Account,
                AccountKind = kind,
                Nickname = string.IsNullOrWhiteSpace(req.Nickname) ? req.Account : req.Nickname,
                PasswordHash = PasswordHasher.Hash(req.Password),
                Role = UserRole.User,
                Status = UserStatus.Active,
            };
            if (!await users.AddAsync(user, ct))
                return Results.Conflict(new { error = "账号已存在" });

            return Results.Created($"/api/auth/me", new AuthResponse(jwt.Issue(user), jwtOpt.Value.ExpiresSeconds, ToInfo(user)));
        });

        group.MapPost("/login", async (
            LoginRequest req,
            IUserStore users,
            JwtTokenService jwt,
            IOptions<JwtOptions> jwtOpt,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Account) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { error = "account/password 必填" });

            var user = await users.GetByAccountAsync(req.Account, ct);
            if (user is null || !PasswordHasher.Verify(req.Password, user.PasswordHash))
                return Results.Json(new { error = "账号或密码错误" }, statusCode: StatusCodes.Status401Unauthorized);

            if (user.Status == UserStatus.Disabled)
                return Results.Forbid();

            return Results.Ok(new AuthResponse(jwt.Issue(user), jwtOpt.Value.ExpiresSeconds, ToInfo(user)));
        });

        // 当前登录用户信息（需 JWT）
        group.MapGet("/me", (HttpContext ctx, IUserStore users) => HandleMe(ctx, users));

        return app;
    }

    private static async Task<IResult> HandleMe(HttpContext ctx, IUserStore users)
    {
        if (ctx.User.ToCurrentUser() is not { } cu)
            return Results.Unauthorized();
        var user = await users.GetByIdAsync(cu.UserId);
        return user is null ? Results.NotFound(new { error = "user not found" }) : Results.Ok(ToInfo(user));
    }

    internal static UserInfo ToInfo(User u) => new(u.Id, u.Account, u.Nickname, u.AvatarUrl, u.Role, u.Status);
}
