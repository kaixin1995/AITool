using SqlSugar;

namespace AITool.Domain.Proxy;

[SugarTable("GatewayRequestMetrics")]
[SugarIndex("IX_GatewayRequestMetrics_Time", nameof(StartedUtcTicks), OrderByType.Asc)]
[SugarIndex("IX_GatewayRequestMetrics_KeyTime", nameof(AccessKeyId), OrderByType.Asc, nameof(StartedUtcTicks), OrderByType.Asc)]
public sealed class GatewayRequestMetric
{
    [SugarColumn(IsPrimaryKey = true)] public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccessKeyId { get; set; }
    [SugarColumn(Length = 64)] public string SessionDigest { get; set; } = "";
    [SugarColumn(Length = 64)] public string ParentSessionDigest { get; set; } = "";
    public long StartedUtcTicks { get; set; }
    public long RequestBytes { get; set; }
    public long ResponseBytes { get; set; }
    public long DurationMs { get; set; }
    public int StatusCode { get; set; }
    public long Tokens { get; set; }
    [SugarColumn(DecimalDigits = 10)] public decimal CostUsd { get; set; }
    public bool Unpriced { get; set; }
    [SugarColumn(Length = 64)] public string Outcome { get; set; } = "";
}
