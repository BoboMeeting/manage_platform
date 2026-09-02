using ManagerPlatform.Models;
using ManagerPlatform.Stores;

namespace ManagerPlatform.Services;

/// <summary>
/// 后台定时清理 Conference：
///   - Waiting 超时（30s）无人入会 → Ended
///   - PendingClose 宽限期超时（60s）无人回场 → Ended
/// 扫描间隔：10s
/// </summary>
public sealed class ConferenceCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConferenceCleanupService> _logger;

    public ConferenceCleanupService(IServiceProvider serviceProvider, ILogger<ConferenceCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ConferenceCleanupService 启动，扫描间隔 10s");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                // 使用 scope，避免 singleton store 在 hosted service 中长期持有
                using var scope = _serviceProvider.CreateScope();
                var sp = scope.ServiceProvider;
                var conferences = sp.GetRequiredService<IConferenceStore>();
                var aiSessions = sp.GetRequiredService<IAiSessionStore>();

                var all = await conferences.GetAllNonTerminalAsync(stoppingToken);
                var now = DateTimeOffset.UtcNow;

                foreach (var conf in all)
                {
                    if (conf.Status == ConferenceStatus.Waiting
                        && conf.WaitingExpiresAt.HasValue
                        && now > conf.WaitingExpiresAt.Value)
                    {
                        conf.Status = ConferenceStatus.Ended;
                        conf.EndedAt = now;
                        await conferences.UpdateAsync(conf, stoppingToken);
                        _logger.LogInformation("Conference {Id} 超时（Waiting 无人入会）→ Ended", conf.Id);
                    }
                    else if (conf.Status == ConferenceStatus.PendingClose
                             && conf.PendingCloseExpiresAt.HasValue
                             && now > conf.PendingCloseExpiresAt.Value)
                    {
                        conf.Status = ConferenceStatus.Ended;
                        conf.EndedAt = now;

                        // 清理挂起的 AI 会话
                        var aiList = await aiSessions.GetByConferenceAsync(conf.Id, stoppingToken);
                        foreach (var info in aiList)
                        {
                            var s = await aiSessions.GetAsync(info.Id, stoppingToken);
                            if (s is not null && s.Status != AISessionStatus.Ended)
                            {
                                s.Status = AISessionStatus.Ended;
                                s.EndedAt = now;
                                await aiSessions.UpdateAsync(s, stoppingToken);
                            }
                        }

                        await conferences.UpdateAsync(conf, stoppingToken);
                        _logger.LogInformation("Conference {Id} 宽限期超时（PendingClose 无人回场）→ Ended", conf.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ConferenceCleanupService 扫描异常");
            }
        }
    }
}
