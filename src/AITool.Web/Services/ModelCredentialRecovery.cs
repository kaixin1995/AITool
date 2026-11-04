using AITool.Application.Networking;
using AITool.Application.OAuth;
using AITool.Application.Xai;
using AITool.Domain.Sites;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

internal sealed class DeviceOAuthModelRefreshUnavailableException : Exception;

/// <summary>Retries a model catalog request once after recovering the rejected credential.</summary>
internal static class ModelCredentialRecovery
{
    public static async Task<T> FetchAsync<T>(AppDbContext db, IServiceProvider services,
        ForwardOAuthCredentialSnapshot snapshot, Func<string, Task<T>> fetch, CancellationToken ct, bool alreadyRefreshed = false)
    {
        try { return await fetch(snapshot.AccessToken ?? string.Empty); }
        catch (ModelAccessTokenUnauthorizedException)
        {
            var invalidation = services.GetRequiredService<ForwardOAuthInvalidationService>();
            if (alreadyRefreshed || string.IsNullOrWhiteSpace(snapshot.RefreshToken))
            {
                await invalidation.InvalidateAsync(snapshot.AccessToken ?? string.Empty, snapshot, ct);
                throw;
            }

            string? token;
            try
            {
                token = snapshot.Provider switch
                {
                    "codex" => await services.GetRequiredService<CodexCredentialRefreshService>().RefreshForAccountAsync(snapshot.AccountId, snapshot.AccessToken ?? string.Empty, ct),
                    "google" => await services.GetRequiredService<GoogleCredentialRefreshService>().RefreshForAccountAsync(snapshot.AccountId, snapshot.AccessToken ?? string.Empty, ct),
                    "kimi" => await services.GetRequiredService<KimiCredentialRefreshService>().RefreshForAccountAsync(snapshot.AccountId, snapshot.AccessToken ?? string.Empty, ct),
                    "device-oauth" => await services.GetRequiredService<DeviceOAuthCredentialRefreshService>().RefreshForAccountAsync(snapshot.AccountId, snapshot.AccessToken ?? string.Empty, ct),
                    "xai" => await RefreshXaiAsync(db, services, snapshot, ct),
                    _ => null
                };
            }
            catch (Exception ex) when (ex is OAuthRefreshRefusedException or DeviceOAuthRefreshRefusedException or XaiRefreshTokenInvalidException)
            {
                await invalidation.InvalidateAsync(snapshot.AccessToken ?? string.Empty, snapshot, ct);
                throw new ModelAccessTokenUnauthorizedException();
            }
            catch (Exception ex) when (snapshot.Provider == "device-oauth" && ex is not OperationCanceledException && ex is not NetworkPolicyException)
            {
                // Do not expose vendor response bodies which may include credentials.
                throw new DeviceOAuthModelRefreshUnavailableException();
            }

            // A missing result or a transient refresh failure is not proof of invalid credentials.
            if (string.IsNullOrWhiteSpace(token)) throw;
            var refreshed = await ReadSnapshotAsync(db, snapshot, token);
            try { return await fetch(token); }
            catch (ModelAccessTokenUnauthorizedException)
            {
                if (refreshed is not null) await invalidation.InvalidateAsync(token, refreshed, ct);
                throw;
            }
        }
    }

    private static async Task<ForwardOAuthCredentialSnapshot?> ReadSnapshotAsync(AppDbContext db, ForwardOAuthCredentialSnapshot old, string token)
    {
        ForwardOAuthCredentialSnapshot? current = old.Provider switch
        {
            "codex" => (await db.CodexAccounts.InSingleAsync(old.AccountId)) is { } a ? new(old.Provider, a.Id, a.LinkedSiteId, a.AccessToken, a.RefreshToken, a.LastRefreshAt) : null,
            "google" => (await db.GoogleAccounts.InSingleAsync(old.AccountId)) is { } a ? new(old.Provider, a.Id, a.LinkedSiteId, a.AccessToken, a.RefreshToken, a.LastRefreshAt) : null,
            "kimi" => (await db.KimiAccounts.InSingleAsync(old.AccountId)) is { IsDeleted: false } a ? new(old.Provider, a.Id, a.LinkedSiteId, a.AccessToken, a.RefreshToken, a.LastRefreshAt) : null,
            "xai" => (await db.XaiAccounts.InSingleAsync(old.AccountId)) is { IsDeleted: false } a ? new(old.Provider, a.Id, a.LinkedSiteId, a.AccessToken, a.RefreshToken, a.LastRefreshAt) : null,
            "device-oauth" => (await db.DeviceOAuthAccounts.InSingleAsync(old.AccountId)) is { IsDeleted: false } a ? new(old.Provider, a.Id, a.LinkedSiteId, a.AccessToken, a.RefreshToken, a.LastRefreshAt) : null,
            _ => null
        };
        return current?.SiteId == old.SiteId && string.Equals(current.AccessToken, token, StringComparison.Ordinal) ? current : null;
    }

    private static async Task<string?> RefreshXaiAsync(AppDbContext db, IServiceProvider services, ForwardOAuthCredentialSnapshot old, CancellationToken ct)
    {
        using var lease = await NetworkBoundOAuthClientFactory.LockCredentialsAsync("xai", old.AccountId, ct);
        var account = await db.XaiAccounts.InSingleAsync(old.AccountId);
        if (account is null || account.IsDeleted || account.LinkedSiteId != old.SiteId) return null;
        if (!string.Equals(account.AccessToken, old.AccessToken, StringComparison.Ordinal)) return account.AccessToken;
        if (string.IsNullOrWhiteSpace(account.RefreshToken)) return null;
        var factory = services.GetService<NetworkBoundOAuthClientFactory>();
        using var network = factory?.CreateForAccount(new AccountIdentity("xai", account.Id), "credential-refresh");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var tokens = await (network?.XaiOAuth ?? services.GetRequiredService<IXaiOAuthClient>()).RefreshTokenAsync(account.RefreshToken, safety.Token);
        if (string.IsNullOrWhiteSpace(tokens.AccessToken)) return null;
        account.AccessToken = tokens.AccessToken;
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) account.RefreshToken = tokens.RefreshToken;
        account.Scope = tokens.Scope ?? account.Scope;
        account.TokenExpiresAt = tokens.ExpiresAt;
        account.LastRefreshAt = DateTimeOffset.UtcNow;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        using var client = db.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        try
        {
            await client.Updateable(account).UpdateColumns(a => new { a.AccessToken, a.RefreshToken, a.Scope, a.TokenExpiresAt, a.LastRefreshAt, a.UpdatedAt }).ExecuteCommandAsync(safety.Token);
            await client.Updateable<Site>().SetColumns(s => new Site { ApiKey = tokens.AccessToken }).Where(s => s.Id == account.LinkedSiteId).ExecuteCommandAsync(safety.Token);
            await client.Ado.CommitTranAsync();
        }
        catch { await client.Ado.RollbackTranAsync(); throw; }
        services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        return tokens.AccessToken;
    }
}
