using AITool.Application.Networking;
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

/// <summary>Each call owns a short-lived database connection; no locks cross a transport call.</summary>
public sealed class AccountNetworkPolicyStore(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ProxyProfileSecretService secrets) : IAccountNetworkPolicyStore, IAccountNetworkPolicyResolver
{
    public async Task<AccountNetworkPolicyState> GetAsync(AccountIdentity account, CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        return State(account, await ReadAccountPolicyAsync(client, account, cancellationToken));
    }

    /// <summary>Null expectedRevision is unconditional; a supplied revision must match an existing row.</summary>
    public async Task<AccountNetworkPolicyState> SetAsync(AccountIdentity account, AccountNetworkSelection selection,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        ValidateSelection(selection);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        client.Ado.BeginTran();
        try
        {
            // Acquire SQLite's writer lock before checking accounts/references. Profile deletion
            // uses the same lock, so checking then inserting cannot leave a dangling binding.
            await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
            var state = await SetInTransactionAsync(client, account, selection, expectedRevision,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
            return state;
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    public async Task DeleteAsync(AccountIdentity account, string? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        client.Ado.BeginTran();
        try
        {
            await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
            await DeleteInTransactionAsync(client, account, expectedRevision, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    /// <summary>
    /// Writes on the caller's connection and transaction. Caller must already hold the write
    /// lock and persist the final account first. This method never owns transaction lifetime.
    /// Invoke only for an explicitly supplied selection; omitted selections preserve policy.
    /// Null expectedRevision is unconditional; supplied revisions must match an existing row.
    /// </summary>
    public async Task<AccountNetworkPolicyState> SetInTransactionAsync(ISqlSugarClient client,
        AccountIdentity account, AccountNetworkSelection selection, string? expectedRevision = null,
        long? expectedProfileRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateAccount(account);
        ValidateSelection(selection);
        cancellationToken.ThrowIfCancellationRequested();
        await RequireAccountAsync(client, account, cancellationToken);
        var previous = await FindAsync(client, account, cancellationToken);
        RequireRevision(previous, expectedRevision);
        await ResolveSelectionAsync(client, selection, expectedProfileRevision, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcTicks;
        var row = new AccountNetworkPolicy
        {
            ProviderKey = account.ProviderKey,
            AccountId = account.AccountId,
            Mode = selection.Mode,
            ProxyProfileId = selection.ProxyProfileId,
            Revision = Guid.NewGuid().ToString("D"),
            CreatedAtUtcTicks = previous?.CreatedAtUtcTicks ?? now,
            UpdatedAtUtcTicks = now
        };
        if (previous is null)
            await client.Insertable(row).ExecuteCommandAsync(cancellationToken);
        else
            await client.Updateable(row).ExecuteCommandAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return State(account, row);
    }

    /// <summary>
    /// Deletes on the caller's write-locked connection without beginning, committing or
    /// rolling back a transaction. Delete the policy before deleting/soft-deleting its account.
    /// </summary>
    public async Task DeleteInTransactionAsync(ISqlSugarClient client, AccountIdentity account,
        string? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateAccount(account);
        cancellationToken.ThrowIfCancellationRequested();
        await RequireAccountAsync(client, account, cancellationToken);
        RequireRevision(await FindAsync(client, account, cancellationToken), expectedRevision);
        await client.Deleteable<AccountNetworkPolicy>()
            .Where(p => p.ProviderKey == account.ProviderKey && p.AccountId == account.AccountId)
            .ExecuteCommandAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<ResolvedNetworkConfiguration> ResolveForAccountAsync(AccountIdentity account,
        CancellationToken cancellationToken = default)
    {
        ValidateAccount(account);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        // One SQLite statement establishes the account/policy snapshot. A send that has
        // already obtained it may finish with that selection; the next send reads again.
        var state = State(account, await ReadAccountPolicyAsync(client, account, cancellationToken));
        return await ResolveSelectionAsync(client, state.Selection, null, cancellationToken);
    }

    public async Task<ResolvedNetworkConfiguration> ResolveAsync(NetworkOperationContext operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(operation.IsolationKey))
            throw Error("invalid_isolation_key", "A network isolation key is required.");
        ValidateSelection(operation.Selection);
        if (operation.Account is not null) ValidateAccount(operation.Account);
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        if (operation.Account is not null)
            await RequireAccountAsync(client, operation.Account, cancellationToken);
        return await ResolveSelectionAsync(client, operation.Selection, operation.ExpectedProfileRevision, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, AccountIdentity>> GetLinkedSiteOwnersAsync(
        IReadOnlyCollection<Guid> linkedSiteIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(linkedSiteIds);
        cancellationToken.ThrowIfCancellationRequested();
        var ids = linkedSiteIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var owners = new Dictionary<Guid, AccountIdentity>();
        if (ids.Length == 0) return owners;
        using var scope = scopeFactory.CreateScope();
        using var client = OpenClient(scope);
        // Project IDs only, never token-bearing account entities. Chunk to respect SQLite limits.
        foreach (var chunk in ids.Chunk(400))
        {
            Add("codex", await client.Queryable<CodexAccount>().Where(a => chunk.Contains(a.LinkedSiteId))
                .Select(a => new SiteOwnerRow { AccountId = a.Id, LinkedSiteId = a.LinkedSiteId }).ToListAsync(cancellationToken));
            Add("google", await client.Queryable<GoogleAccount>().Where(a => chunk.Contains(a.LinkedSiteId))
                .Select(a => new SiteOwnerRow { AccountId = a.Id, LinkedSiteId = a.LinkedSiteId }).ToListAsync(cancellationToken));
            Add("kimi", await client.Queryable<KimiAccount>().Where(a => !a.IsDeleted && chunk.Contains(a.LinkedSiteId))
                .Select(a => new SiteOwnerRow { AccountId = a.Id, LinkedSiteId = a.LinkedSiteId }).ToListAsync(cancellationToken));
            Add("xai", await client.Queryable<XaiAccount>().Where(a => !a.IsDeleted && chunk.Contains(a.LinkedSiteId))
                .Select(a => new SiteOwnerRow { AccountId = a.Id, LinkedSiteId = a.LinkedSiteId }).ToListAsync(cancellationToken));
            Add("device-oauth", await client.Queryable<DeviceOAuthAccount>().Where(a => !a.IsDeleted && chunk.Contains(a.LinkedSiteId))
                .Select(a => new SiteOwnerRow { AccountId = a.Id, LinkedSiteId = a.LinkedSiteId }).ToListAsync(cancellationToken));
        }
        return owners;

        void Add(string provider, IEnumerable<SiteOwnerRow> rows)
        {
            foreach (var row in rows)
                if (!owners.TryAdd(row.LinkedSiteId, new AccountIdentity(provider, row.AccountId)))
                    throw Error("ambiguous_account_owner", "A linked site has more than one account owner.");
        }
    }

    private async Task<ResolvedNetworkConfiguration> ResolveSelectionAsync(ISqlSugarClient client,
        AccountNetworkSelection selection, long? expectedProfileRevision, CancellationToken cancellationToken)
    {
        ValidateSelection(selection);
        if (selection.Mode != NetworkMode.ProxyProfile)
        {
            if (expectedProfileRevision is not null)
                throw Error("invalid_profile_revision", "A profile revision requires a proxy profile selection.");
            return selection.Mode == NetworkMode.Direct ? ResolvedNetworkConfiguration.Direct : ResolvedNetworkConfiguration.Legacy;
        }
        if (!await client.Queryable<SystemRuntimeSettings>()
                .AnyAsync(s => s.Id == 1 && s.DeveloperProxyProfilesEnabled, cancellationToken))
            throw Error("proxy_profiles_disabled", "Proxy profiles are disabled.");
        var profile = await client.Queryable<ProxyProfile>()
            .FirstAsync(p => p.Id == selection.ProxyProfileId!.Value, cancellationToken);
        if (profile is null)
            throw Error("proxy_profile_not_found", "The selected proxy profile does not exist.");
        if (!profile.IsEnabled)
            throw Error("proxy_profile_disabled", "The selected proxy profile is disabled.");
        if (profile.Revision < 1)
            throw Error("invalid_proxy_profile", "The selected proxy profile is invalid.");
        if (expectedProfileRevision is not null && expectedProfileRevision != profile.Revision)
            throw Error("proxy_profile_revision_conflict", "The selected proxy profile has changed.");
        if (!ProxyProfileSecretService.TryNormalizeProxyUrl(profile.ProxyUrl, out var sanitized, out var embedded, out _)
            || embedded is not null)
            throw Error("invalid_proxy_profile", "The selected proxy profile endpoint is invalid.");
        var endpoint = new Uri(sanitized, UriKind.Absolute);
        System.Net.NetworkCredential? credential;
        try
        {
            credential = secrets.ReadCredentials(profile);
        }
        catch
        {
            // Do not retain an inner exception: protection providers may include sensitive input.
            throw Error("proxy_credentials_unavailable", "The selected proxy credentials could not be read.");
        }
        return new ResolvedNetworkConfiguration
        {
            Mode = NetworkMode.ProxyProfile,
            ProxyProfileId = profile.Id,
            ProfileRevision = profile.Revision,
            ProxyEndpoint = endpoint,
            Credentials = credential
        };
    }

    // The table names are fixed by ValidateAccount; only values are parameters. A left
    // join distinguishes a live account without a policy from a missing/deleted account
    // in one SQLite statement, even if the deletion transaction commits concurrently.
    private static async Task<AccountNetworkPolicy?> ReadAccountPolicyAsync(ISqlSugarClient client,
        AccountIdentity account, CancellationToken cancellationToken)
    {
        var (table, live) = account.ProviderKey switch
        {
            "codex" => ("CodexAccounts", "1=1"),
            "google" => ("GoogleAccounts", "1=1"),
            "kimi" => ("KimiAccounts", "a.IsDeleted=0"),
            "xai" => ("XaiAccounts", "a.IsDeleted=0"),
            "device-oauth" => ("DeviceOAuthAccounts", "a.IsDeleted=0"),
            _ => throw Error("invalid_account_identity", "The account identity is invalid.")
        };
        cancellationToken.ThrowIfCancellationRequested();
        var rows = await client.Ado.SqlQueryAsync<AccountPolicyRow>($"""
            SELECT COALESCE(p.Mode, 0) AS Mode, p.ProxyProfileId, p.Revision,
                   COALESCE(p.UpdatedAtUtcTicks, 0) AS UpdatedAtUtcTicks
            FROM {table} a
            LEFT JOIN AccountNetworkPolicies p ON p.AccountId=a.Id AND p.ProviderKey=@provider
            WHERE a.Id=@id AND {live}
            """, new { provider = account.ProviderKey, id = account.AccountId }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var row = rows.SingleOrDefault();
        if (row is null) throw Error("account_not_found", "The account does not exist.");
        return row.Revision is null ? null : new AccountNetworkPolicy
        {
            Mode = (NetworkMode)row.Mode,
            ProxyProfileId = row.ProxyProfileId,
            Revision = row.Revision,
            UpdatedAtUtcTicks = row.UpdatedAtUtcTicks
        };
    }

    public sealed class AccountPolicyRow
    {
        public int Mode { get; set; }
        public Guid? ProxyProfileId { get; set; }
        public string? Revision { get; set; }
        public long UpdatedAtUtcTicks { get; set; }
    }

    private static Task<AccountNetworkPolicy> FindAsync(ISqlSugarClient client, AccountIdentity account,
        CancellationToken cancellationToken) => client.Queryable<AccountNetworkPolicy>()
        .FirstAsync(p => p.ProviderKey == account.ProviderKey && p.AccountId == account.AccountId, cancellationToken);

    private static AccountNetworkPolicyState State(AccountIdentity account, AccountNetworkPolicy? row)
    {
        var selection = row is null ? new AccountNetworkSelection(NetworkMode.Legacy)
            : new AccountNetworkSelection(row.Mode, row.ProxyProfileId);
        ValidateSelection(selection);
        return new(account, selection, row?.Revision, row?.UpdatedAtUtcTicks);
    }

    private static async Task RequireAccountAsync(ISqlSugarClient client, AccountIdentity account, CancellationToken ct)
    {
        var exists = account.ProviderKey switch
        {
            "codex" => await client.Queryable<CodexAccount>().AnyAsync(a => a.Id == account.AccountId, ct),
            "google" => await client.Queryable<GoogleAccount>().AnyAsync(a => a.Id == account.AccountId, ct),
            "kimi" => await client.Queryable<KimiAccount>().AnyAsync(a => a.Id == account.AccountId && !a.IsDeleted, ct),
            "xai" => await client.Queryable<XaiAccount>().AnyAsync(a => a.Id == account.AccountId && !a.IsDeleted, ct),
            "device-oauth" => await client.Queryable<DeviceOAuthAccount>().AnyAsync(a => a.Id == account.AccountId && !a.IsDeleted, ct),
            _ => false
        };
        if (!exists) throw Error("account_not_found", "The account does not exist.");
    }

    private static void RequireRevision(AccountNetworkPolicy? row, string? expectedRevision)
    {
        if (expectedRevision is not null && !StringComparer.Ordinal.Equals(row?.Revision, expectedRevision))
            throw Error("account_network_revision_conflict", "The account network policy has changed.");
    }

    private static void ValidateAccount(AccountIdentity account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.AccountId == Guid.Empty || account.ProviderKey is not ("codex" or "google" or "kimi" or "xai" or "device-oauth"))
            throw Error("invalid_account_identity", "The account identity is invalid.");
    }

    private static void ValidateSelection(AccountNetworkSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!Enum.IsDefined(selection.Mode)
            || (selection.Mode == NetworkMode.ProxyProfile ? selection.ProxyProfileId is null || selection.ProxyProfileId == Guid.Empty
                : selection.ProxyProfileId is not null))
            throw Error("invalid_network_selection", "The network selection is invalid.");
    }

    private static ISqlSugarClient OpenClient(IServiceScope scope)
    {
        var client = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        return client;
    }

    private static NetworkPolicyException Error(string code, string message) => new(code, message);

    public sealed class SiteOwnerRow
    {
        public Guid AccountId { get; set; }
        public Guid LinkedSiteId { get; set; }
    }
}
