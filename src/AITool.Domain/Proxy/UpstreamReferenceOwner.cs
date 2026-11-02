using SqlSugar;

namespace AITool.Domain.Proxy;

public enum UpstreamReferenceKind
{
    Response = 0,
    Conversation = 1,
    Item = 2
}

public enum UpstreamReferenceExposureStatus
{
    // Committed before exposure. Delivery acknowledgement cannot be made atomic with network I/O.
    CommittedForExposure = 0
}

/// <summary>Ownership metadata only. Never contains identifiers, request/response bodies or credentials.</summary>
[SugarTable("UpstreamReferenceOwners")]
[SugarIndex("IX_UpstreamReferenceOwners_Expires", nameof(ExpiresAtUtcTicks), OrderByType.Asc)]
public sealed class UpstreamReferenceOwner
{
    [SugarColumn(IsPrimaryKey = true, Length = 256)]
    public string ClientScope { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true)]
    public UpstreamReferenceKind Kind { get; set; }

    [SugarColumn(IsPrimaryKey = true, Length = 64)]
    public string LookupDigest { get; set; } = string.Empty;

    [SugarColumn(Length = 100)]
    public string OwnerProviderKey { get; set; } = string.Empty;

    public Guid OwnerAccountId { get; set; }

    [SugarColumn(Length = 2048)]
    public string UpstreamNamespace { get; set; } = string.Empty;

    public bool IsNative { get; set; }
    public UpstreamReferenceExposureStatus ExposureStatus { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long ExpiresAtUtcTicks { get; set; }
    public bool IsConflict { get; set; }
}
