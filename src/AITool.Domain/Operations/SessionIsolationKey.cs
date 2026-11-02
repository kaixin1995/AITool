using SqlSugar;

namespace AITool.Domain.Operations;

/// <summary>
/// Deployment-local session derivation secret. Only the Data Protection ciphertext is stored.
/// Checkpoint 1 supports one fixed row and one version, not key rotation.
/// </summary>
[SugarTable("SessionIsolationKeys")]
public sealed class SessionIsolationKey
{
    public const int SingletonId = 1;
    public const int CurrentVersion = 1;

    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = SingletonId;

    public int Version { get; set; } = CurrentVersion;

    [SugarColumn(IsNullable = false, ColumnDataType = "TEXT")]
    public string ProtectedKey { get; set; } = string.Empty;
}

/// <summary>
/// Durable initialization marker, committed with the key. Its non-secret ciphertext
/// fingerprint detects a lost/replaced key row even after a process restart.
/// </summary>
[SugarTable("SessionIsolationKeyMetadata")]
public sealed class SessionIsolationKeyMetadata
{
    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = SessionIsolationKey.SingletonId;

    public int Version { get; set; } = SessionIsolationKey.CurrentVersion;

    [SugarColumn(IsNullable = false, Length = 64)]
    public string ProtectedKeyFingerprint { get; set; } = string.Empty;
}
