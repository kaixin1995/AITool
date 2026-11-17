using AITool.Application.Networking;

namespace AITool.Application.Proxy;

/// <summary>Prepares managed-account session fields after protocol conversion and header merging.</summary>
public interface IAuxiliarySessionPreparation
{
    Task<AuxiliaryPreparedSession> PrepareAsync(
        string trustedScope,
        AccountIdentity account,
        string protocolType,
        bool isAntigravity,
        string originalRequestBody,
        string preparedRequestBody,
        IReadOnlyDictionary<string, string> mergedHeaders,
        CancellationToken cancellationToken = default);
}

public sealed record AuxiliaryPreparedSession(string PreparedRequestBody, Dictionary<string, string> ForwardHeaders);
