using AITool.Application.Networking;
using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Domain.Operations;
using AITool.Infrastructure.OAuth;

namespace AITool.Web.Services;

/// <summary>
/// Registry vendors are metadata only. Each remote operation gets a fresh vendor with an
/// immutable, account/session-bound client; its handler revalidates policy on every send.
/// </summary>
public sealed class NetworkBoundDeviceOAuthVendorFactory(
    AccountNetworkClientFactory clients,
    IAccountNetworkPolicyResolver resolver,
    DeviceOAuthVendorRegistry registry,
    ILogger<MiniMaxOAuthVendor> miniMaxLogger)
{
    private readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(20);

    internal NetworkBoundDeviceOAuthVendorFactory(AccountNetworkClientFactory clients,
        IAccountNetworkPolicyResolver resolver, DeviceOAuthVendorRegistry registry,
        ILogger<MiniMaxOAuthVendor> miniMaxLogger, TimeSpan requestTimeout)
        : this(clients, resolver, registry, miniMaxLogger)
    {
        if (requestTimeout <= TimeSpan.Zero || requestTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _requestTimeout = requestTimeout;
    }

    public async Task<NetworkOperationContext> CreateOperationAsync(
        AccountNetworkSelection? selection, string isolationKey, CancellationToken ct)
    {
        var operation = new NetworkOperationContext(null, isolationKey,
            selection ?? new AccountNetworkSelection(NetworkMode.Legacy));
        var resolved = await resolver.ResolveAsync(operation, ct);
        return operation with
        {
            ExpectedProfileRevision = resolved.Mode == NetworkMode.ProxyProfile ? resolved.ProfileRevision : null
        };
    }

    public BoundVendor ForOperation(string vendorKey, NetworkOperationContext operation, string purpose)
        => Create(vendorKey, clients.CreateForOperation(operation, purpose), ct => resolver.ResolveAsync(operation, ct));

    public BoundVendor ForAccount(DeviceOAuthAccount account, string purpose)
    {
        if (account.Id == Guid.Empty || account.IsDeleted)
            throw new NetworkPolicyException("account_not_found", "The account does not exist.");
        return Create(account.VendorKey, clients.CreateForAccount(Identity(account.Id), purpose),
            ct => resolver.ResolveForAccountAsync(Identity(account.Id), ct));
    }

    public static AccountIdentity Identity(Guid accountId) => new(DeviceOAuthQuotaService.ProviderKey, accountId);

    private BoundVendor Create(string vendorKey, HttpClient client,
        Func<CancellationToken, Task<ResolvedNetworkConfiguration>> validate)
    {
        try
        {
            // This client is operation-owned, not the pooled transport client. Preserve the
            // existing named vendor clients' 20-second send/body budget, even with ct=None.
            client.Timeout = _requestTimeout;
            if (registry.Find(vendorKey) is null)
                throw new NetworkPolicyException("unknown_vendor", "The OAuth vendor is not registered.");
            IDeviceOAuthVendor vendor = vendorKey.ToLowerInvariant() switch
            {
                "minimax-cn" => new MiniMaxOAuthVendor(client, miniMaxLogger),
                "minimax-global" => new MiniMaxOAuthVendor(client, miniMaxLogger, global: true),
                "workbuddy-cn" => new WorkBuddyOAuthVendor(client),
                "workbuddy-global" => new WorkBuddyOAuthVendor(client, global: true),
                "qoder" => new QoderOAuthVendor(client),
                "qoder-cn" => new QoderOAuthVendor(client, china: true),
                "trae-cn" => new TraeOAuthVendor(client),
                "trae-global" => new TraeOAuthVendor(client, global: true),
                "kiro" => new KiroOAuthVendor(client),
                "copilot" => new CopilotOAuthVendor(client),
                "claude" => new ClaudeOAuthVendor(client),
                "zcode" => new ZCodeOAuthVendor(client),
                _ => throw new NetworkPolicyException("unsupported_bound_vendor", "The OAuth vendor has no network-bound implementation.")
            };
            return new BoundVendor(new ValidatedVendor(vendor, validate, _requestTimeout), client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // Sends revalidate policy in AccountNetworkClientFactory. Once an approved operation succeeds,
    // preserve its result: a rotating refresh may already have consumed the old refresh token.
    // Only failures are rechecked to reclassify policy errors swallowed by vendor adapters.
    private sealed class ValidatedVendor(IDeviceOAuthVendor inner,
        Func<CancellationToken, Task<ResolvedNetworkConfiguration>> validate, TimeSpan requestTimeout) : IDeviceOAuthVendor
    {
        public string VendorKey => inner.VendorKey;
        public string Label => inner.Label;
        public bool SupportsModelFetch => inner.SupportsModelFetch;
        public DeviceOAuthModelCatalogScope ModelCatalogScope => inner.ModelCatalogScope;
        public DeviceOAuthChatSiteConfig? ChatSiteConfig => inner.ChatSiteConfig;
        public IReadOnlyList<(string Slug, string DisplayName)> DefaultModels => inner.DefaultModels;
        public TimeSpan RefreshLead => inner.RefreshLead;
        public DeviceOAuthQuotaResult? ParseCachedQuota(string rawJson) => inner.ParseCachedQuota(rawJson);
        private async Task<T> Run<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct,
            Func<T, bool>? failed = null)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(requestTimeout);
            await validate(budget.Token);
            T result;
            try { result = await action(budget.Token); }
            catch (OperationCanceledException) when (budget.IsCancellationRequested) { throw; }
            catch
            {
                // Reclassify swallowed/wrapped policy errors without ever replacing a
                // successful result (especially freshly rotated credentials).
                budget.Token.ThrowIfCancellationRequested();
                await validate(budget.Token);
                throw;
            }
            if (failed?.Invoke(result) == true)
            {
                budget.Token.ThrowIfCancellationRequested();
                await validate(budget.Token);
            }
            return result;
        }
        public Task<DeviceOAuthStartResult> StartSignInAsync(CancellationToken ct)
            => Run(token => inner.StartSignInAsync(token), ct);
        public Task<DeviceOAuthPollResult> PollSignInAsync(string sessionJson, CancellationToken ct)
            => Run(token => inner.PollSignInAsync(sessionJson, token), ct, FailedPoll);
        public Task<DeviceOAuthPollResult> CompleteWithCallbackAsync(string sessionJson, string callbackUrl, CancellationToken ct)
            => Run(token => inner.CompleteWithCallbackAsync(sessionJson, callbackUrl, token), ct, FailedPoll);
        public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, CancellationToken ct)
            => Run(token => inner.RefreshTokenAsync(refreshToken, token), ct);
        public Task<DeviceOAuthTokenSet> RefreshTokenAsync(string refreshToken, string? extraJson, CancellationToken ct)
            => Run(token => inner.RefreshTokenAsync(refreshToken, extraJson, token), ct);
        public Task<DeviceOAuthTokenSet> RefreshTokenAsync(DeviceOAuthTokenSet current, string? extraJson, CancellationToken ct)
            => Run(token => inner.RefreshTokenAsync(current, extraJson, token), ct);
        public Task<IReadOnlyList<DeviceOAuthRemoteModel>> FetchModelsAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken ct)
            => Run(budget => inner.FetchModelsAsync(token, extraJson, budget), ct, models => models.Count == 0);
        public Task<DeviceOAuthQuotaResult> QueryQuotaAsync(DeviceOAuthTokenSet token, string? extraJson, CancellationToken ct)
            => Run(budget => inner.QueryQuotaAsync(token, extraJson, budget), ct, result => !result.Success);
        private static bool FailedPoll(DeviceOAuthPollResult result)
            => result.Tokens is null && (result.IsDenied || result.IsExpired || !string.IsNullOrEmpty(result.Error));
    }

    public sealed class BoundVendor(IDeviceOAuthVendor vendor, HttpClient client) : IDisposable
    {
        public IDeviceOAuthVendor Vendor { get; } = vendor;
        public void Dispose() => client.Dispose();
    }
}
