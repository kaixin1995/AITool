using System.Linq.Expressions;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.OAuth;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using SqlSugar;

namespace AITool.Web.Services;

/// <summary>Quota transitions change only enablement, never credentials or another disable reason.</summary>
internal static class QuotaAccountStateWriter
{
    public static Task<bool> ChangeAsync(AppDbContext db, string providerKey, Guid accountId, bool enabled,
        IQuotaRecheckScheduleStore? scheduleStore, ILogger logger, CancellationToken ct) => providerKey switch
    {
        "codex" => ChangeAsync<CodexAccount>(db, providerKey, accountId, enabled,
            a => a.Id == accountId && !a.ManuallyDisabled && !a.DisabledByFeatureToggle && !a.IsQuotaCooling,
            a => new CodexAccount { IsEnabled = enabled }, a => a.LinkedSiteId, a => a.LastQuotaCheckedAt, scheduleStore, logger, ct),
        "google" => ChangeAsync<GoogleAccount>(db, providerKey, accountId, enabled,
            a => a.Id == accountId && !a.ManuallyDisabled && !a.DisabledByFeatureToggle && !a.DisabledByUpstream && !a.IsQuotaCooling,
            a => new GoogleAccount { IsEnabled = enabled }, a => a.LinkedSiteId, a => a.LastQuotaCheckedAt, scheduleStore, logger, ct),
        "kimi" => ChangeAsync<KimiAccount>(db, providerKey, accountId, enabled,
            a => a.Id == accountId && !a.ManuallyDisabled && !a.DisabledByFeatureToggle && !a.IsDeleted,
            a => new KimiAccount { IsEnabled = enabled }, a => a.LinkedSiteId, a => a.LastQuotaCheckedAt, scheduleStore, logger, ct),
        "xai" => ChangeAsync<XaiAccount>(db, providerKey, accountId, enabled,
            a => a.Id == accountId && !a.ManuallyDisabled && !a.DisabledByFeatureToggle && !a.IsDeleted && !a.RequiresReauth,
            a => new XaiAccount { IsEnabled = enabled }, a => a.LinkedSiteId, a => a.LastQuotaCheckedAt, scheduleStore, logger, ct),
        "device-oauth" => ChangeAsync<DeviceOAuthAccount>(db, providerKey, accountId, enabled,
            a => a.Id == accountId && !a.ManuallyDisabled && !a.DisabledByFeatureToggle && !a.IsDeleted && !a.RequiresReauth,
            a => new DeviceOAuthAccount { IsEnabled = enabled }, a => a.LinkedSiteId, a => a.LastQuotaCheckedAt, scheduleStore, logger, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(providerKey)),
    };

    private static async Task<bool> ChangeAsync<T>(AppDbContext db, string providerKey, Guid accountId, bool enabled,
        Expression<Func<T, bool>> eligible, Expression<Func<T, T>> update,
        Func<T, Guid> linkedSiteId, Func<T, DateTimeOffset?> lastQuotaSuccess,
        IQuotaRecheckScheduleStore? scheduleStore, ILogger logger, CancellationToken ct) where T : class, new()
    {
        using var client = db.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        DateTimeOffset? lastSuccess;
        try
        {
            // First statement is a conditional write: SQLite serializes this with manual toggles,
            // feature changes and deletes. No stale read-to-write flag window or whole-row writes.
            var command = client.Updateable<T>().SetColumns(update).Where(eligible);
            if (enabled)
            {
                // Evaluate the global feature flag in the same write, not the metadata cache.
                command = command.Where(a => SqlFunc.Subqueryable<SystemRuntimeSettings>()
                    .Where(s => s.Id == 1 && s.OAuthFeaturesEnabled).Any());
            }
            if (await command.ExecuteCommandAsync(ct) == 0)
            {
                await client.Ado.CommitTranAsync();
                return false;
            }

            var current = await client.Queryable<T>().InSingleAsync(accountId);
            var siteId = linkedSiteId(current);
            lastSuccess = lastQuotaSuccess(current);
            if (siteId != Guid.Empty)
            {
                await client.Updateable<Site>().SetColumns(s => new Site { IsEnabled = enabled })
                    .Where(s => s.Id == siteId).ExecuteCommandAsync(ct);
            }
            await client.Ado.CommitTranAsync();
        }
        catch
        {
            await client.Ado.RollbackTranAsync();
            throw;
        }

        // An idempotent event, not a query-success heartbeat. Commit before using the store's connection.
        if (!enabled && scheduleStore is not null)
        {
            // The disable already committed: caller cancellation must not discard its event.
            // A failed schedule write is repaired by coordinator reconciliation, not a failed quota result.
            using var scheduleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var scheduleTask = scheduleStore.EnsureScheduledAsync(providerKey, accountId, lastSuccess, scheduleTimeout.Token);
                // Some database operations ignore cancellation. Bound our wait independently and
                // observe any eventual failure even when the underlying write outlives this request.
                _ = scheduleTask.ContinueWith(static task => { _ = task.Exception; },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                await scheduleTask.WaitAsync(scheduleTimeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Schedule quota recovery failed after disabling {ProviderKey} account {AccountId}; reconciliation will retry", providerKey, accountId);
            }
        }
        return true;
    }
}
