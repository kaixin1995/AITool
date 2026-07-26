namespace AITool.Application.Codex;

/// <summary>
/// Codex 手动重置额度 credits 信息（来自 wham/rate-limit-reset-credits）。
/// </summary>
public sealed class CodexResetCreditsInfo
{
    /// <summary>剩余可用手动重置次数。</summary>
    public int AvailableCount { get; set; }

    /// <summary>各张 credit 明细（仅保留 available 状态且有过期时间的）。</summary>
    public List<CodexResetCredit> Credits { get; set; } = [];

    /// <summary>查询是否成功。</summary>
    public bool Success { get; set; }

    /// <summary>错误信息（如有）。</summary>
    public string? Error { get; set; }

    /// <summary>原始 JSON（调试/缓存用）。</summary>
    public string? RawJson { get; set; }
}

/// <summary>
/// 单张 reset credit 明细。
/// </summary>
public sealed class CodexResetCredit
{
    /// <summary>Credit ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>状态（available/used/expired）。</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>发放时间（UTC）。</summary>
    public DateTimeOffset? GrantedAt { get; set; }

    /// <summary>过期时间（UTC）。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
