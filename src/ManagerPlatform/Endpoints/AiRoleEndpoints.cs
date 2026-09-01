using ManagerPlatform.Auth;
using ManagerPlatform.Models;
using ManagerPlatform.Stores;

namespace ManagerPlatform.Endpoints;

public static class AiRoleEndpoints
{
    public static IEndpointRouteBuilder MapAiRoleEndpoints(this IEndpointRouteBuilder app)
    {
        // AI 角色模板管理（写操作要求运营及以上，读操作登录用户可用）
        var group = app.MapGroup("/api/ai-roles").WithTags("AI Roles");

        group.MapGet("/", async (IAiRoleStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct)));

        group.MapGet("/{id}", async (string id, IAiRoleStore store, CancellationToken ct) =>
            await store.GetByIdAsync(id, ct) is { } r
                ? Results.Ok(r)
                : Results.NotFound(new { error = "ai role not found" }));

        group.MapPost("/", async (
            AiRoleRequest req,
            HttpContext ctx,
            IAiRoleStore store,
            CancellationToken ct) =>
        {
            if (ctx.User.ToCurrentUser() is not { } cu)
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.PromptTemplate))
                return Results.BadRequest(new { error = "name/promptTemplate 必填" });

            var role = new AIRole
            {
                Name = req.Name.Trim(),
                Description = req.Description,
                PromptTemplate = req.PromptTemplate,
                TtsConfig = req.TtsConfig,
                AvatarUrl = req.AvatarUrl,
                CreatedBy = cu.UserId,
            };
            await store.AddAsync(role, ct);
            return Results.Created($"/api/ai-roles/{role.Id}", role);
        }).RequireAuthorization(Policies.OperatorPlus);

        group.MapPut("/{id}", async (
            string id,
            AiRoleRequest req,
            IAiRoleStore store,
            CancellationToken ct) =>
        {
            var role = await store.GetByIdAsync(id, ct);
            if (role is null) return Results.NotFound(new { error = "ai role not found" });

            role.Name = req.Name.Trim();
            role.Description = req.Description;
            role.PromptTemplate = req.PromptTemplate;
            role.TtsConfig = req.TtsConfig;
            role.AvatarUrl = req.AvatarUrl;
            role.UpdatedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(role, ct);
            return Results.Ok(role);
        }).RequireAuthorization(Policies.OperatorPlus);

        group.MapDelete("/{id}", async (
            string id,
            IAiRoleStore store,
            CancellationToken ct) =>
        {
            await store.DeleteAsync(id, ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(Policies.OperatorPlus);

        return app;
    }
}
