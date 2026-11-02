namespace AITool.Application.Proxy;

/// <summary>Attaches managed credential refresh and rejection handling to health probes.</summary>
public interface IHealthCredentialCallbacks
{
    void Configure(ProxyForwardRequest request);
}
