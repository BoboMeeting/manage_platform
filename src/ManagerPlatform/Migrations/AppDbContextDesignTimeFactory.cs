using ManagerPlatform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ManagerPlatform.Migrations;

/// <summary>
/// 设计时 DbContext 工厂。供 dotnet ef migrations 命令在没有 Host 启动环境时创建 AppDbContext。
/// 迁移生成阶段并不真正连接数据库（仅分析模型），因此连接字符串可使用占位。
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseNpgsql("Host=localhost;Database=bobomeet_design;Username=postgres;Password=design");
        return new AppDbContext(builder.Options);
    }
}
