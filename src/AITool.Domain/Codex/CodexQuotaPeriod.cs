using SqlSugar;

namespace AITool.Domain.Codex;

/// <summary>真实额度查询观察到的常规窗口及其最近一次用量汇总。</summary>
[SugarTable("CodexQuotaPeriods")]
[SugarIndex("IX_CodexQuotaPeriods_Account", nameof(AccountId), OrderByType.Asc)]
public sealed class CodexQuotaPeriod
{
    [SugarColumn(IsPrimaryKey = true)] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid SiteId { get; set; }
    [SugarColumn(Length = 80)] public string WindowId { get; set; } = "";
    [SugarColumn(Length = 100)] public string Label { get; set; } = "";
    public double DurationSeconds { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset ResetAt { get; set; }
    [SugarColumn(IsNullable = true)] public DateTimeOffset? EndAt { get; set; }
    public bool BoundaryUncertain { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastObservedAt { get; set; }
    [SugarColumn(IsNullable = true)] public double? UsedPercent { get; set; }
    public long InputTokens { get; set; }
    public long CachedTokens { get; set; }
    public long OutputTokens { get; set; }
    public int RequestCount { get; set; }
    [SugarColumn(IsNullable = true)] public int? SuccessCount { get; set; }
    [SugarColumn(IsNullable = true)] public DateTimeOffset? LastUsageAt { get; set; }
    public int UnknownCount { get; set; }
    [SugarColumn(IsNullable = true)] public decimal? EstimatedCostUsd { get; set; }
    [SugarColumn(IsNullable = true)] public int? UnpricedCount { get; set; }
    [SugarColumn(IsNullable = true, ColumnDataType = "TEXT")] public string? ModelsJson { get; set; }
    public DateTimeOffset AggregatedAt { get; set; }
}
