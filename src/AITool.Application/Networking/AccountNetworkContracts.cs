using System.Net;
using AITool.Domain.Operations;

namespace AITool.Application.Networking;

public sealed record AccountIdentity(string ProviderKey, Guid AccountId);

public sealed record AccountNetworkSelection(NetworkMode Mode, Guid? ProxyProfileId = null);

/// <summary>An operation freezes selection and, optionally, the profile generation it expects.</summary>
public sealed record NetworkOperationContext(
    AccountIdentity? Account,
    string IsolationKey,
    AccountNetworkSelection Selection,
    long? ExpectedProfileRevision = null);

public sealed record AccountNetworkPolicyState(
    AccountIdentity Account,
    AccountNetworkSelection Selection,
    string? Revision,
    long? UpdatedAtUtcTicks);

/// <summary>Transport-only material. Never serialize, log or include credentials in pool keys.</summary>
public sealed class ResolvedNetworkConfiguration
{
    public NetworkMode Mode { get; init; }
    public Guid? ProxyProfileId { get; init; }
    public long ProfileRevision { get; init; }
    public Uri? ProxyEndpoint { get; init; }
    public NetworkCredential? Credentials { get; init; }

    public static ResolvedNetworkConfiguration Legacy => new() { Mode = NetworkMode.Legacy };
    public static ResolvedNetworkConfiguration Direct => new() { Mode = NetworkMode.Direct };
}

/// <summary>Messages and codes are safe for API errors; upstream URLs and secrets are never attached.</summary>
public sealed class NetworkPolicyException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public interface IAccountNetworkPolicyStore
{
    Task<AccountNetworkPolicyState> GetAsync(AccountIdentity account, CancellationToken cancellationToken = default);
    Task<AccountNetworkPolicyState> SetAsync(AccountIdentity account, AccountNetworkSelection selection,
        string? expectedRevision = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(AccountIdentity account, string? expectedRevision = null, CancellationToken cancellationToken = default);

    /// <summary>Only owned nonempty sites appear. Multiple live owners are an error, never first-wins.</summary>
    Task<IReadOnlyDictionary<Guid, AccountIdentity>> GetLinkedSiteOwnersAsync(
        IReadOnlyCollection<Guid> linkedSiteIds, CancellationToken cancellationToken = default);
}

public interface IAccountNetworkPolicyResolver
{
    Task<ResolvedNetworkConfiguration> ResolveAsync(NetworkOperationContext operation,
        CancellationToken cancellationToken = default);
    Task<ResolvedNetworkConfiguration> ResolveForAccountAsync(AccountIdentity account,
        CancellationToken cancellationToken = default);
}
