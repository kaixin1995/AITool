using SqlSugar;

namespace AITool.Domain.Operations;

/// <summary>Persistent quota recovery work, independent of account credentials and status.</summary>
[SugarTable("QuotaRecheckSchedules")]
[SugarIndex("IX_QuotaRecheckSchedules_NextCheckAtUtcTicks", nameof(NextCheckAtUtcTicks), OrderByType.Asc)]
public sealed class QuotaRecheckSchedule
{
    [SugarColumn(IsPrimaryKey = true, Length = 100)]
    public string ProviderKey { get; set; } = string.Empty;

    [SugarColumn(IsPrimaryKey = true)]
    public Guid AccountId { get; set; }

    // Opaque generation changes on Ensure, claim and retained completion. A pre-listing
    // observation may only remove a row if this token still matches (including after recreation).
    [SugarColumn(Length = 36, IsNullable = false, DefaultValue = "''")]
    public string Generation { get; set; } = string.Empty;

    // UTC ticks avoid SQLite/SqlSugar DateTimeOffset offset and ordering conversions.
    public long NextCheckAtUtcTicks { get; set; }
    public long CreatedAtUtcTicks { get; set; }
    public long UpdatedAtUtcTicks { get; set; }

    [SugarColumn(IsNullable = true, Length = 36)]
    public string? LeaseToken { get; set; }

    [SugarColumn(IsNullable = true)]
    public long? LeaseExpiresAtUtcTicks { get; set; }
}
