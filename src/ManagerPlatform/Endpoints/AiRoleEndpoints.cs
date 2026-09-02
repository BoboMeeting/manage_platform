using ManagerPlatform.Auth;
using ManagerPlatform.Models;
using ManagerPlatform.Stores;

namespace ManagerPlatform.Endpoints;

public static class AiRoleEndpoints
{
    public static IEndpointRouteBuilder MapAiRoleEndpoints(this IEndpointRouteBuilder app)
    {
        // ===== 客户端可访问：浏览 AI 角色（入会时选择角色） =====
        var read = app.MapGroup("/api/ai-roles").WithTags("AI Roles");

        read.MapGet("/", async (IAiRoleStore store, CancellationToken ct) =>
            Results.Ok(await store.GetAllAsync(ct)));

        read.MapGet("/{id}", async (string id, IAiRoleStore store, CancellationToken ct) =>
            await store.GetByIdAsync(id, ct) is { } r
                ? Results.Ok(r)
                : Results.NotFound(new { error = "ai role not found" }));

        
        return app;
    }
}
