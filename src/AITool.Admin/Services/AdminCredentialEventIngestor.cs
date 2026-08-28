using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// 摄取 Core 侧发布的托管凭证事件（credential-refreshed / credential-disabled）。
/// <para>
/// 双宿主语义：Core 无库，401 即刷只更新本地快照并回传事件；本摄取器是事件的落库端——
/// 刷新事件写回对应账号表（token/时间戳）与隐藏站点 ApiKey；禁用事件禁用账号与站点。
/// 持久化后触发全量配置同步，把新凭证下发回 Core（闭环）。
/// </para>
/// </summary>
public sealed class AdminCredentialEventIngestor
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AdminCredentialEventIngestor> _logger;

    public AdminCredentialEventIngestor(
        IServiceScopeFactory scopeFactory,
        ILogger<AdminCredentialEventIngestor> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// 消费一批事件信封中的凭证事件；返回是否有凭证事件被处理（用于决定是否推送配置同步）。
    /// </summary>
    public async Task<bool> IngestCredentialEventsAsync(IReadOnlyList<CoreAdminEventEnvelope> envelopes, CancellationToken cancellationToken)
    {
        var handled = false;
        foreach (var envelope in envelopes)
        {
            if (string.Equals(envelope.EventType, "credential-refreshed", StringComparison.Ordinal))
            {
                CoreCredentialRefreshedEvent? payload = null;
                try { payload = JsonSerializer.Deserialize<CoreCredentialRefreshedEvent>(envelope.PayloadJson, SerializerOptions); }
                catch (Exception ex) { _logger.LogWarning(ex, "credential-refreshed 事件载荷解析失败，已跳过。SequenceId={SequenceId}", envelope.SequenceId); }
                if (payload is not null)
                {
                    await ApplyRefreshAsync(payload, cancellationToken);
                    handled = true;
                }
            }
            else if (string.Equals(envelope.EventType, "credential-disabled", StringComparison.Ordinal))
            {
                CoreCredentialDisabledEvent? payload = null;
                try { payload = JsonSerializer.Deserialize<CoreCredentialDisabledEvent>(envelope.PayloadJson, SerializerOptions); }
                catch (Exception ex) { _logger.LogWarning(ex, "credential-disabled 事件载荷解析失败，已跳过。SequenceId={SequenceId}", envelope.SequenceId); }
                if (payload is not null)
                {
                    await ApplyDisableAsync(payload, cancellationToken);
                    handled = true;
                }
            }
        }
        return handled;
    }

    private async Task ApplyRefreshAsync(CoreCredentialRefreshedEvent payload, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var site = await db.Sites.FirstAsync(x => x.Id == payload.LinkedSiteId, cancellationToken);
        if (site is not null && !string.Equals(site.ApiKey, payload.NewAccessToken, StringComparison.Ordinal))
        {
            site.ApiKey = payload.NewAccessToken;
            await db.UpdateAsync(site, cancellationToken);
        }

        switch (payload.Provider)
        {
            case "Codex":
                var codex = await db.CodexAccounts.FirstAsync(x => x.Id == payload.AccountId, cancellationToken);
                if (codex is not null)
                {
                    codex.AccessToken = payload.NewAccessToken;
                    if (!string.IsNullOrWhiteSpace(payload.NewRefreshToken))
                    {
                        codex.RefreshToken = payload.NewRefreshToken;
                    }
                    codex.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(55);
                    codex.LastRefreshAt = payload.RefreshedAt;
                    await db.UpdateAsync(codex, cancellationToken);
                }
                break;
            case "Google":
                var google = await db.GoogleAccounts.FirstAsync(x => x.Id == payload.AccountId, cancellationToken);
                if (google is not null)
                {
                    google.AccessToken = payload.NewAccessToken;
                    if (!string.IsNullOrWhiteSpace(payload.NewRefreshToken))
                    {
                        google.RefreshToken = payload.NewRefreshToken;
                    }
                    google.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(55);
                    google.LastRefreshAt = payload.RefreshedAt;
                    await db.UpdateAsync(google, cancellationToken);
                }
                break;
            default:
                var kimi = await db.KimiAccounts.FirstAsync(x => x.Id == payload.AccountId, cancellationToken);
                if (kimi is not null)
                {
                    kimi.AccessToken = payload.NewAccessToken;
                    if (!string.IsNullOrWhiteSpace(payload.NewRefreshToken))
                    {
                        kimi.RefreshToken = payload.NewRefreshToken;
                    }
                    kimi.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(55);
                    kimi.LastRefreshAt = payload.RefreshedAt;
                    await db.UpdateAsync(kimi, cancellationToken);
                }
                break;
        }

        _logger.LogInformation(
            "已持久化 Core 侧凭证刷新。Provider={Provider}, AccountId={AccountId}, SiteId={SiteId}",
            payload.Provider, payload.AccountId, payload.LinkedSiteId);
    }

    private async Task ApplyDisableAsync(CoreCredentialDisabledEvent payload, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var site = await db.Sites.FirstAsync(x => x.Id == payload.LinkedSiteId, cancellationToken);
        if (site is not null && site.IsEnabled)
        {
            site.IsEnabled = false;
            await db.UpdateAsync(site, cancellationToken);
        }

        switch (payload.Provider)
        {
            case "Codex":
                var codex = await db.CodexAccounts.FirstAsync(x => x.Id == payload.AccountId, cancellationToken);
                if (codex is not null && codex.IsEnabled)
                {
                    // Codex 无 DisabledByUpstream 字段（走额度冷却/巡检体系），这里只置禁用。
                    codex.IsEnabled = false;
                    await db.UpdateAsync(codex, cancellationToken);
                }
                break;
            case "Google":
                var google = await db.GoogleAccounts.FirstAsync(x => x.Id == payload.AccountId, cancellationToken);
                if (google is not null && google.IsEnabled)
                {
                    google.IsEnabled = false;
                    google.DisabledByUpstream = true;
                    await db.UpdateAsync(google, cancellationToken);
                }
                break;
            default:
                var kimi = await db.KimiAccounts.FirstAsync(x => x.Id == payload.AccountId, cancellationToken);
                if (kimi is not null && kimi.IsEnabled)
                {
                    // Kimi 无 DisabledByUpstream 字段，这里只置禁用。
                    kimi.IsEnabled = false;
                    await db.UpdateAsync(kimi, cancellationToken);
                }
                break;
        }

        _logger.LogWarning(
            "已按 Core 侧凭证禁用事件停用托管账号。Provider={Provider}, AccountId={AccountId}, Reason={Reason}",
            payload.Provider, payload.AccountId, payload.Reason);
    }
}
