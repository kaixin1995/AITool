using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.OAuth;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>Persists only a rejection of the credential actually sent, not a stale response.</summary>
public sealed class ForwardOAuthInvalidationService(AppDbContext db, ProxyRequestMetadataCache cache)
{
    public async Task InvalidateAsync(string sentToken, object? state, CancellationToken ct)
    {
        if (state is not ForwardOAuthCredentialSnapshot s || !string.Equals(sentToken, s.AccessToken, StringComparison.Ordinal)) return;
        using var client = db.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        try
        {
            var changed = s.Provider.ToLowerInvariant() switch
            {
                "codex" => await client.Updateable<CodexAccount>().SetColumns(a => new CodexAccount { RequiresReauth = true, IsEnabled = false })
                    .Where(a => a.Id == s.AccountId && a.LinkedSiteId == s.SiteId && a.AccessToken == s.AccessToken && a.RefreshToken == s.RefreshToken && a.LastRefreshAt == s.LastRefreshAt).ExecuteCommandAsync(ct),
                "google" => await client.Updateable<GoogleAccount>().SetColumns(a => new GoogleAccount { RequiresReauth = true, IsEnabled = false })
                    .Where(a => a.Id == s.AccountId && a.LinkedSiteId == s.SiteId && a.AccessToken == s.AccessToken && a.RefreshToken == s.RefreshToken && a.LastRefreshAt == s.LastRefreshAt).ExecuteCommandAsync(ct),
                "kimi" => await client.Updateable<KimiAccount>().SetColumns(a => new KimiAccount { RequiresReauth = true, IsEnabled = false })
                    .Where(a => a.Id == s.AccountId && a.LinkedSiteId == s.SiteId && !a.IsDeleted && a.AccessToken == s.AccessToken && a.RefreshToken == s.RefreshToken && a.LastRefreshAt == s.LastRefreshAt).ExecuteCommandAsync(ct),
                "xai" => await client.Updateable<XaiAccount>().SetColumns(a => new XaiAccount { RequiresReauth = true, IsEnabled = false })
                    .Where(a => a.Id == s.AccountId && a.LinkedSiteId == s.SiteId && !a.IsDeleted && a.AccessToken == s.AccessToken && a.RefreshToken == s.RefreshToken && a.LastRefreshAt == s.LastRefreshAt).ExecuteCommandAsync(ct),
                "device-oauth" => await client.Updateable<DeviceOAuthAccount>().SetColumns(a => new DeviceOAuthAccount { RequiresReauth = true, IsEnabled = false })
                    .Where(a => a.Id == s.AccountId && a.LinkedSiteId == s.SiteId && !a.IsDeleted && a.AccessToken == s.AccessToken && a.RefreshToken == s.RefreshToken && a.LastRefreshAt == s.LastRefreshAt).ExecuteCommandAsync(ct),
                _ => 0
            };
            if (changed != 0)
                await client.Updateable<Site>().SetColumns(a => new Site { IsEnabled = false }).Where(a => a.Id == s.SiteId).ExecuteCommandAsync(ct);
            await client.Ado.CommitTranAsync();
            if (changed != 0)
            {
                cache.InvalidateRouteTargets();
                cache.InvalidateCodexAccounts();
                cache.InvalidateGoogleAccounts();
                cache.InvalidateKimiAccounts();
            }
        }
        catch
        {
            await client.Ado.RollbackTranAsync();
            throw;
        }
    }
}
