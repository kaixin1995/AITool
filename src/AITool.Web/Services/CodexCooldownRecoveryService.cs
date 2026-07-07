using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 后台服务：周期扫描冷却到期的 Codex 账号，自动清除冷却并恢复 Site（若账号未被手动禁用）。
/// <para>
/// 性能（P7）：周期 2 分钟；查询条件 IsQuotaCooling && QuotaCoolingUntil<=now（冷却账号极少）；
/// 恢复前检查 account.IsEnabled（手动禁用优先，不被冷却到期自动覆盖）。
/// </para>
/// </summary>
public sealed class CodexCooldownRecoveryService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly ILogger<CodexCooldownRecoveryService> _logger;
    private readonly IHostEnvironment _environment;

    public CodexCooldownRecoveryService(
        IServiceProvider services,
        ILogger<CodexCooldownRecoveryService> logger,
        IHostEnvironment environment)
    {
        _services = services;
        _logger = logger;
        _environment = environment;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverDueAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Codex cooldown recovery loop error");
            }
            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    private async Task RecoverDueAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        // 尊重 Codex 功能总开关：关闭时跳过本轮（避免恢复被总开关禁用的账号）
        var runtime = await cache.GetRuntimeSettingsAsync(ct);
        if (!runtime.CodexFeaturesEnabled)
        {
            return;
        }

        // 用全局 SQLite 串行化锁包裹"查 due → 更新账号/站点"完整块，
        // 避免与巡检/日志写等后台服务并发踩 SqlSugarScope 竞态。
        await dbContext.SerialExecuteAsync(async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var due = await dbContext.CodexAccounts
                .Where(a => a.IsQuotaCooling && a.QuotaCoolingUntil != null && a.QuotaCoolingUntil <= now)
                .ToListAsync(ct);

            if (due.Count == 0) return;

            var anyRecovered = false;
            foreach (var account in due)
            {
                account.IsQuotaCooling = false;
                account.QuotaCoolingUntil = null;
                await dbContext.UpdateAsync(account, ct);

                // 仅当账号本身启用（非手动禁用）才恢复 Site
                if (account.IsEnabled)
                {
                    var site = await dbContext.Sites.InSingleAsync(account.LinkedSiteId);
                    if (site != null && !site.IsEnabled)
                    {
                        site.IsEnabled = true;
                        await dbContext.UpdateAsync(site, ct);
                        anyRecovered = true;
                    }
                }
                _logger.LogInformation("Codex account {Id} cooldown recovered", account.Id);
            }

            if (anyRecovered)
            {
                cache.InvalidateRouteTargets();
                cache.InvalidateCodexAccounts();
            }
        }, ct);
    }
}
