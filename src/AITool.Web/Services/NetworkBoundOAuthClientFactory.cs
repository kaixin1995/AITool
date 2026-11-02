using AITool.Application.Codex;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Networking;
using AITool.Application.Xai;
using AITool.Domain.Operations;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Xai;

namespace AITool.Web.Services;

/// <summary>Creates operation-local vendor clients. Never changes an injected or shared client.</summary>
public sealed class NetworkBoundOAuthClientFactory(
    AccountNetworkClientFactory clients,
    ILoggerFactory loggers,
    ICodexClientVersionResolver versions,
    IAccountNetworkPolicyResolver resolver)
{
    private static readonly AITool.Infrastructure.Common.KeyedAsyncLock CredentialLocks = new();

    internal static Task<IDisposable> LockCredentialsAsync(string provider, Guid accountId, CancellationToken ct)
        => CredentialLocks.WaitAsync($"{provider}:{accountId:D}", ct);

    public BoundClients CreateForAccount(AccountIdentity account, string purpose)
        => new(clients.CreateForAccount(account, purpose), loggers, versions);

    public BoundClients CreateForOperation(NetworkOperationContext operation, string purpose)
        => new(clients.CreateForOperation(operation, purpose), loggers, versions);

    public async Task<NetworkOperationContext> BeginOperationAsync(AccountNetworkSelection? selection,
        CancellationToken cancellationToken = default)
    {
        var operation = new NetworkOperationContext(null, Guid.NewGuid().ToString("N"),
            selection ?? new AccountNetworkSelection(NetworkMode.Legacy));
        var resolved = await resolver.ResolveAsync(operation, cancellationToken);
        return operation with
        {
            ExpectedProfileRevision = resolved.Mode == NetworkMode.ProxyProfile ? resolved.ProfileRevision : null
        };
    }

    public static Task<NetworkOperationContext> PrepareOperationAsync(NetworkBoundOAuthClientFactory? factory,
        AccountNetworkSelection? selection, CancellationToken cancellationToken = default)
    {
        if (factory is not null) return factory.BeginOperationAsync(selection, cancellationToken);
        if (selection is not null)
            throw new NetworkPolicyException("network_policy_unavailable", "Network policy services are unavailable.");
        return Task.FromResult(new NetworkOperationContext(null, Guid.NewGuid().ToString("N"),
            new AccountNetworkSelection(NetworkMode.Legacy)));
    }

    public static AccountNetworkSelection? ImportSelection(int? networkMode, Guid? proxyProfileId)
    {
        if (networkMode is null)
        {
            if (proxyProfileId is not null)
                throw new NetworkPolicyException("invalid_network_selection", "A network mode is required.");
            return null;
        }
        return new AccountNetworkSelection((NetworkMode)networkMode.Value, proxyProfileId);
    }

    /// <summary>Owns only the immutable routing wrapper, not the pooled transport underneath it.</summary>
    public sealed class BoundClients : IDisposable
    {
        private readonly ILoggerFactory _loggers;
        private readonly ICodexClientVersionResolver _versions;
        internal BoundClients(HttpClient httpClient, ILoggerFactory loggers, ICodexClientVersionResolver versions)
        {
            HttpClient = httpClient;
            HttpClient.Timeout = TimeSpan.FromSeconds(30);
            _loggers = loggers;
            _versions = versions;
        }

        private ICodexOAuthClient? _codexOAuth;
        public HttpClient HttpClient { get; }
        public ICodexOAuthClient CodexOAuth
        {
            get
            {
                if (_codexOAuth is not null) return _codexOAuth;
                HttpClient.Timeout = TimeSpan.FromSeconds(20);
                return _codexOAuth = new CodexOAuthClient(HttpClient);
            }
        }
        public IGoogleOAuthClient GoogleOAuth => new GoogleOAuthClient(HttpClient);
        public IKimiOAuthClient KimiOAuth => new KimiOAuthClient(HttpClient, _loggers.CreateLogger<KimiOAuthClient>());
        public IXaiOAuthClient XaiOAuth => new XaiOAuthClient(HttpClient, _loggers.CreateLogger<XaiOAuthClient>());
        public ICodexModelFetcher CodexModels => new CodexModelFetcher(HttpClient, _versions, _loggers.CreateLogger<CodexModelFetcher>());
        public IGoogleModelFetcher GoogleModels => new GoogleModelFetcher(HttpClient);
        public IKimiModelFetcher KimiModels => new KimiModelFetcher(HttpClient, _loggers.CreateLogger<KimiModelFetcher>());
        public IXaiModelFetcher XaiModels => new XaiModelFetcher(HttpClient, _loggers.CreateLogger<XaiModelFetcher>());
        public void Dispose() => HttpClient.Dispose();
    }
}
