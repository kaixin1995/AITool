using SqlSugar;

namespace AITool.Domain.Operations;

/// <summary>
/// Deployment-local session derivation secret. The default compatibility mode stores v1
/// Data Protection ciphertext; opt-in v2 stores raw bytes in the database after a coordinated
/// cutover. Switching formats preserves key material when the original key ring is available.
/// </summary>
[SugarTable("SessionIsolationKeys")]
public sealed class SessionIsolationKey
{
    public const int SingletonId = 1;
    public const int CurrentVersion = 2;
    /// <summary>Legacy rows whose ProtectedKey is Data Protection ciphertext.</summary>
    public const int LegacyCiphertextVersion = 1;

    [SugarColumn(IsPrimaryKey = true)]
    public int Id { get; set; } = SingletonId;

    public int Version { get; set; } = CurrentVersion;

    [SugarColumn(IsNullable = false, ColumnDataType = "TEXT")]
    public string ProtectedKey { get; set; } = string.Empty;
}

/// <summary>
/// Durable initialization marker, committed with the key. Its non-secret storage-string
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
