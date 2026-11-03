using SqlSugar;

namespace AITool.Domain.Operations;

/// <summary>
/// Deployment-local session derivation secret, stored raw in the database (v2): the database
/// file is the deployment's persistence unit, so the key travels with every redeploy/restore.
/// This HMAC key encrypts nothing sensitive — OAuth tokens already live in the same database
/// in plaintext — so wrapping it in a Data Protection key ring outside the database added a
/// loss-prone dependency instead of protection. Version 1 rows (DP-encrypted ciphertext) are
/// upgraded in place at startup without rotating the key material.
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
