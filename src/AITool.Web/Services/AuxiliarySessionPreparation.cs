using AITool.Application.Networking;
using AITool.Application.Proxy;
using Microsoft.Extensions.Primitives;

namespace AITool.Web.Services;

/// <summary>Bridges auxiliary operations to the account session isolator without web dependencies in infrastructure.</summary>
public sealed class AuxiliarySessionPreparation(AccountSessionIsolationService isolation) : IAuxiliarySessionPreparation
{
    public async Task<AuxiliaryPreparedSession> PrepareAsync(
        string trustedScope, AccountIdentity account, string protocolType, bool isAntigravity,
        string originalRequestBody, string preparedRequestBody, IReadOnlyDictionary<string, string> mergedHeaders,
        CancellationToken cancellationToken = default)
    {
        var originalHeaders = new HeaderDictionary();
        foreach (var (name, value) in mergedHeaders)
            originalHeaders[name] = new StringValues(value);
        // The scope is selected by the server, never from a request header. A fresh fallback
        // is created once per operation, before any forwarding retry.
        var context = AccountSessionIsolationService.Capture(trustedScope, originalHeaders, originalRequestBody,
            Guid.NewGuid().ToString("N"));
        var result = await isolation.ApplyAsync(context, account, protocolType, isAntigravity,
            preparedRequestBody, mergedHeaders, cancellationToken);
        return new(result.PreparedRequestBody, result.ForwardHeaders);
    }
}
