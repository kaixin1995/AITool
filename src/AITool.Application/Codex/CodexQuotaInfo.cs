namespace AITool.Application.Codex;

/// <summary>
/// Codex 额度查询结果。字段尽量宽松（上游返回结构需实测），解析失败时仅保留状态信息，
/// 不影响账号可用性。对应 new-api 的 CodexUsageResponse 概念。
/// </summary>
public sealed class CodexQuotaInfo
{
    public bool Success { get; set; }

    /// <summary>失败原因（Success=false 时）。</summary>
    public string? Error { get; set; }

    /// <summary>剩余额度（单位由上游决定，如 credits/requests）；上游不返回则为 null。</summary>
    public decimal? RemainingQuota { get; set; }

    /// <summary>已用额度。</summary>
    public decimal? UsedQuota { get; set; }

    /// <summary>总额度。</summary>
    public decimal? TotalQuota { get; set; }

    /// <summary>额度单位描述。</summary>
    public string? QuotaUnit { get; set; }

    /// <summary>额度重置时间（若上游返回）。</summary>
    public DateTimeOffset? ResetAt { get; set; }

    /// <summary>原始响应 JSON（存入 LastQuotaRawJson 供面板展示）。</summary>
    public string? RawJson { get; set; }

    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}
