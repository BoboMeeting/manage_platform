using System.Text;
using ManagerPlatform.Auth;
using ManagerPlatform.Endpoints;
using ManagerPlatform.LiveKit;
using ManagerPlatform.Models;
using ManagerPlatform.Options;
using ManagerPlatform.Stores;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ===== 配置绑定 =====
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<LiveKitOptions>(builder.Configuration.GetSection(LiveKitOptions.SectionName));

// ===== 认证 / 授权 =====
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
// 声明式授权策略：角色要求集中在策略定义处，端点通过 .RequireAuthorization(Policies.Xxx) 引用
builder.Services.AddAuthorization(options =>
{
    // 角色 claim 值 = UserRole 枚举名（签发时 Role.ToString()）；RequireRole 为精确匹配，
    // "Operator 及以上"需按当前层级展开（层级变化时须同步此处）

    /// <summary>仅用户角色（User）</summary>
    options.AddPolicy(Policies.UserOnly, p =>
        p.RequireRole(nameof(UserRole.User)));
    /// <summary>用户角色（User）及以上角色</summary>
    options.AddPolicy(Policies.UserPlus, p =>
        p.RequireRole(nameof(UserRole.User),nameof(UserRole.Observer),nameof(UserRole.Operator) ,nameof(UserRole.SuperAdmin)));
    
    /// <summary>仅观察角色（Observer）</summary>
    options.AddPolicy(Policies.ObserveOnly, p =>
        p.RequireRole(nameof(UserRole.Observer)));
    /// <summary>观察角色（Observer）及以上角色</summary>
    options.AddPolicy(Policies.ObservePlus, p =>
        p.RequireRole(nameof(UserRole.Observer),nameof(UserRole.Operator) ,nameof(UserRole.SuperAdmin)));
    
    /// <summary>仅运营角色（Operator）</summary>
    options.AddPolicy(Policies.OperatorOnly, p =>
        p.RequireRole(nameof(UserRole.Operator)));
    /// <summary>运营角色（Operator）及以上角色</summary>
    options.AddPolicy(Policies.OperatorPlus, p =>
        p.RequireRole(nameof(UserRole.Operator), nameof(UserRole.SuperAdmin)));
    
    /// <summary>仅超级管理员角色（SuperAdmin）</summary>
    options.AddPolicy(Policies.SuperAdminOnly, p =>
        p.RequireRole(nameof(UserRole.SuperAdmin)));
});

// 本服务为纯 JWT 无状态鉴权，不依赖 DataProtection 持久密钥；
// 将密钥持久化到项目本地目录，避免在受限环境（如只读用户目录）下启动失败。
var dpKeys = Path.Combine(builder.Environment.ContentRootPath, ".dp-keys");
Directory.CreateDirectory(dpKeys);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dpKeys));

// ===== 业务服务注册（内存存储，生产替换为 PostgreSQL 实现）=====
builder.Services.AddSingleton<IUserStore, InMemoryUserStore>();
builder.Services.AddSingleton<IRoomStore, InMemoryRoomStore>();
builder.Services.AddSingleton<IConferenceStore, InMemoryConferenceStore>();
builder.Services.AddSingleton<IParticipantStore, InMemoryParticipantStore>();
builder.Services.AddSingleton<IAiRoleStore, InMemoryAiRoleStore>();
builder.Services.AddSingleton<IAiSessionStore, InMemoryAiSessionStore>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<ILiveKitTokenService, LiveKitTokenService>();

// ===== API 文档 =====
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

var app = builder.Build();

// 初始化种子数据：超级管理员 + 预置 AI 角色
await SeedAsync(app.Services);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json
    // Scalar 文档 UI：访问 /scalar
    app.MapScalarApiReference();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { ok = true, ts = DateTimeOffset.UtcNow }))
   .WithTags("System");

app.MapAuthEndpoints();
app.MapRoomEndpoints();
app.MapConferenceEndpoints();
app.MapAiRoleEndpoints();
app.MapAdminEndpoints();

// 启动期守护：所有要求管理角色(Observer+)的端点必须位于 /api/admin 下，
// 否则 Nginx 无法按前缀对外隔离管理端接口。违规则拒绝启动（CI/部署即失败）。
EnsureAdminEndpointsAreIsolated(app.Services);

app.Run();

static void EnsureAdminEndpointsAreIsolated(IServiceProvider services)
{
    // 凡要求观察者(Observer)及以上角色的策略，均为管理端专用
    var adminPolicies = new HashSet<string>(StringComparer.Ordinal)
    {
        Policies.ObserveOnly, Policies.ObservePlus,
        Policies.OperatorOnly, Policies.OperatorPlus, Policies.SuperAdminOnly,
    };

    var endpoints = services.GetRequiredService<EndpointDataSource>();
    var offenders = new List<string>();
    foreach (var ep in endpoints.Endpoints.OfType<RouteEndpoint>())
    {
        var route = ep.RoutePattern.RawText ?? string.Empty;
        foreach (var attr in ep.Metadata.GetOrderedMetadata<AuthorizeAttribute>())
        {
            if (!string.IsNullOrEmpty(attr.Policy)
                && adminPolicies.Contains(attr.Policy)
                && !route.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add($"  [{attr.Policy}] {route}");
            }
        }
    }

    if (offenders.Count > 0)
        throw new InvalidOperationException(
            "管理端专用端点必须位于 /api/admin 前缀下，否则 Nginx 无法按前缀对外隔离。违规端点：\n"
            + string.Join("\n", offenders));
}

// ===== 种子数据（local function，须位于类型声明之前）=====
static async Task SeedAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var sp = scope.ServiceProvider;
    var users = sp.GetRequiredService<IUserStore>();
    var aiRoles = sp.GetRequiredService<IAiRoleStore>();
    var logger = sp.GetRequiredService<ILogger<Program>>();

    // 默认超级管理员：account=admin@bobomeet.io / password=admin123
    if (await users.GetByAccountAsync("admin@bobomeet.io") is null)
    {
        await users.AddAsync(new User
        {
            Account = "admin@bobomeet.io",
            AccountKind = AccountKind.Email,
            Nickname = "超级管理员",
            PasswordHash = PasswordHasher.Hash("admin123"),
            Role = UserRole.SuperAdmin,
            Status = UserStatus.Active,
        });
        logger.LogInformation("已初始化超级管理员 admin@bobomeet.io / admin123（请尽快修改密码）");
    }

    // 预置 AI 角色模板
    var existing = await aiRoles.GetAllAsync();
    if (existing.Count == 0)
    {
        await aiRoles.AddAsync(new AIRole
        {
            Name = "英语老师",
            Description = "耐心友好的英语口语陪练",
            PromptTemplate = "你是一名专业的英语口语老师，用鼓励的方式引导学员开口说英语，及时纠正语法错误并给出地道表达。",
            TtsConfig = """{"voice":"en-US-AriaNeural","rate":"1.0"}""",
            AvatarUrl = null,
            CreatedBy = "system",
        });
        await aiRoles.AddAsync(new AIRole
        {
            Name = "面试官",
            Description = "按预设提纲提问并对回答追问",
            PromptTemplate = "你是一名资深技术面试官，按照岗位要求逐题提问，根据候选人的回答进行深度追问，最后给出结构化反馈。",
            TtsConfig = """{"voice":"zh-CN-YunxiNeural","rate":"1.0"}""",
            AvatarUrl = null,
            CreatedBy = "system",
        });
        await aiRoles.AddAsync(new AIRole
        {
            Name = "虚拟闺蜜",
            Description = "社交场景中主动分享趣事活跃气氛",
            PromptTemplate = "你是用户的虚拟闺蜜，性格开朗活泼，会主动分享生活趣事、关心对方情绪，用轻松聊天的语气交流。",
            TtsConfig = """{"voice":"zh-CN-XiaoyiNeural","rate":"1.05"}""",
            AvatarUrl = null,
            CreatedBy = "system",
        });
        logger.LogInformation("已初始化 3 个预置 AI 角色");
    }
}

// 供 WebApplicationFactory<Program> 集成测试使用
public partial class Program { }

