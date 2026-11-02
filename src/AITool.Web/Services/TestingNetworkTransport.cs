using AITool.Application.Networking;

namespace AITool.Web.Services;

/// <summary>Tests must explicitly provide a recording or loopback transport; never contact real suppliers by default.</summary>
internal sealed class TestingNetworkTransport : INetworkTransport
{
    public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration,
        string isolationScope, string purpose, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NetworkPolicyException("test_network_not_configured", "The test host has no explicitly configured upstream transport.");
    }
}
