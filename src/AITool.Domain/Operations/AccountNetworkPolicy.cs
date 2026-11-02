using SqlSugar;

namespace AITool.Domain.Operations;

public enum NetworkMode
{
    Legacy = 0,
    Direct = 1,
    ProxyProfile = 2
}

/// <summary>Independent account egress policy; never stores account or proxy credentials.</summary>
[SugarTable("AccountNetworkPolicies")]
[SugarIndex("IX_AccountNetworkPolicies_ProxyProfileId", nameof(ProxyProfileId), OrderByType.Asc)]
public sealed class AccountNetworkPolicy
{
    [SugarColumn(IsPrimaryKey = true, Length = 100)]
    public string ProviderKey { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true)]
    public Guid AccountId { get; set; }

    public NetworkMode Mode { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? ProxyProfileId { get; set; }

    [SugarColumn(Length = 36, IsNullable = false)]
    public string Revision { get; set; } = string.Empty;

    public long CreatedAtUtcTicks { get; set; }
    public long UpdatedAtUtcTicks { get; set; }
}
