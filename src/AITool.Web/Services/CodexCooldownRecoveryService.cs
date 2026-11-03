using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 后台服务：周期扫描冷却到期的 Codex 账号，自动清除冷却并恢复 Site（若账号未被手动禁用）。
/// <para>
/// 性能（P7）：周期 2 分钟；查询条件 IsQuotaCooling 且 QuotaCoolingUntil 不晚于当前时间（冷却账号极少）；
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

    internal async Task RecoverDueAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        // 尊重 Codex 功能总开关：关闭时跳过本轮（避免恢复被总开关禁用的账号）。
        // 此处在 SerialExecuteAsync 外查询，可能与 Web 请求并发踩 SqlSugarScope 竞态，
        // 用 try-catch 降级：查询失败时跳过本轮（下轮重试）。
        CachedProxyRuntimeSettings runtime;
        try
        {
            runtime = await cache.GetRuntimeSettingsAsync(ct);
        }
        catch
        {
            _logger.LogWarning("GetRuntimeSettingsAsync failed in cooldown recovery, skipping this round");
            return;
        }
        if (!runtime.OAuthFeaturesEnabled)
        {
            return;
        }

        // Use a private writer-first transaction per account so manual disable and recovery
        // serialize in SQLite; never restore a site from a stale pre-transaction snapshot.
        var now = DateTimeOffset.UtcNow;
        var due = await dbContext.CodexAccounts
            .Where(a => a.IsQuotaCooling && a.QuotaCoolingUntil != null && a.QuotaCoolingUntil <= now)
            .Select(a => a.Id).ToListAsync(ct);
        var changed = false;
        foreach (var id in due)
        {
            using var client = dbContext.Client.CopyNew();
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            await client.Ado.BeginTranAsync();
            try
            {
                var updated = await client.Updateable<AITool.Domain.Codex.CodexAccount>()
                    .SetColumns(a => new AITool.Domain.Codex.CodexAccount { IsQuotaCooling = false, QuotaCoolingUntil = null })
                    .Where(a => a.Id == id && a.IsQuotaCooling && a.QuotaCoolingUntil != null && a.QuotaCoolingUntil <= now)
                    .Where(a => SqlSugar.SqlFunc.Subqueryable<AITool.Domain.Operations.SystemRuntimeSettings>()
                        .Where(s => s.Id == 1 && s.OAuthFeaturesEnabled).Any())
                    .ExecuteCommandAsync(ct);
                if (updated != 0)
                {
                    var account = await client.Queryable<AITool.Domain.Codex.CodexAccount>().InSingleAsync(id);
                    if (account is { IsEnabled: true, ManuallyDisabled: false, DisabledByFeatureToggle: false, RequiresReauth: false })
                        await client.Updateable<AITool.Domain.Sites.Site>()
                            .SetColumns(s => new AITool.Domain.Sites.Site { IsEnabled = true })
                            .Where(s => s.Id == account.LinkedSiteId).ExecuteCommandAsync(ct);
                    changed = true;
                }
                await client.Ado.CommitTranAsync();
            }
            catch { await client.Ado.RollbackTranAsync(); throw; }
        }
        if (changed)
        {
            cache.InvalidateRouteTargets();
            cache.InvalidateCodexAccounts();
        }
    }
}
