using AITool.Application.Proxy;

namespace AITool.Web.Services;

/// <summary>Reuses the normal forwarding credential lifecycle for managed health probes.</summary>
public sealed class HealthCredentialCallbacks(IServiceProvider services) : IHealthCredentialCallbacks
{
    public void Configure(ProxyForwardRequest request)
    {
        if (request.SiteId is not { } siteId || siteId == Guid.Empty) return;
        var source = request.ManagedSource;
        if (string.Equals(source, "Codex", StringComparison.OrdinalIgnoreCase))
            request.RefreshTargetApiKeyAsync = (token, ct) => services.GetRequiredService<CodexCredentialRefreshService>().RefreshAsync(siteId, token, ct);
        else if (string.Equals(source, "Google", StringComparison.OrdinalIgnoreCase))
            request.RefreshTargetApiKeyAsync = (token, ct) => services.GetRequiredService<GoogleCredentialRefreshService>().RefreshAsync(siteId, token, ct);
        else if (string.Equals(source, "kimi_oauth", StringComparison.OrdinalIgnoreCase))
            request.RefreshTargetApiKeyAsync = (token, ct) => services.GetRequiredService<KimiCredentialRefreshService>().RefreshAsync(siteId, token, ct);
        else if (string.Equals(source, "device_oauth", StringComparison.OrdinalIgnoreCase))
            request.RefreshTargetApiKeyAsync = (token, ct) => services.GetRequiredService<DeviceOAuthCredentialRefreshService>().RefreshAsync(siteId, token, ct);

        if (new[] { "Codex", "Google", "kimi_oauth", "xai_oauth", "device_oauth" }
            .Contains(source, StringComparer.OrdinalIgnoreCase))
            request.InvalidateTargetCredentialAsync = (token, snapshot, ct) => services.GetRequiredService<ForwardOAuthInvalidationService>()
                .InvalidateAsync(token, snapshot, ct);
    }
}
