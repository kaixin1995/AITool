using System.Text.Json.Serialization;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// chatgpt.com/backend-api/wham/usage 接口原始响应负载。
/// 移植自 codex-patrol Models/CodexQuotaModels.cs，同时兼容 snake_case 与 camelCase 两种字段命名。
/// </summary>
public sealed class CodexUsagePayload
{
    [JsonPropertyName("plan_type")]
    public string? Plan_Type { get; set; }
    public string? PlanType { get; set; }

    [JsonPropertyName("rate_limit")]
    public CodexRateLimitInfo? Rate_Limit { get; set; }
    public CodexRateLimitInfo? RateLimit { get; set; }

    [JsonPropertyName("code_review_rate_limit")]
    public CodexRateLimitInfo? Code_Review_Rate_Limit { get; set; }
    public CodexRateLimitInfo? CodeReviewRateLimit { get; set; }

    [JsonPropertyName("additional_rate_limits")]
    public List<CodexAdditionalRateLimit>? Additional_Rate_Limits { get; set; }
    public List<CodexAdditionalRateLimit>? AdditionalRateLimits { get; set; }
}

/// <summary>
/// 速率限制详情：是否允许、是否达上限、主/次窗口。
/// </summary>
public sealed class CodexRateLimitInfo
{
    public bool? Allowed { get; set; }

    [JsonPropertyName("limit_reached")]
    public bool? Limit_Reached { get; set; }
    public bool? LimitReached { get; set; }

    [JsonPropertyName("primary_window")]
    public CodexUsageWindow? Primary_Window { get; set; }
    public CodexUsageWindow? PrimaryWindow { get; set; }

    [JsonPropertyName("secondary_window")]
    public CodexUsageWindow? Secondary_Window { get; set; }
    public CodexUsageWindow? SecondaryWindow { get; set; }
}

/// <summary>
/// 单个额度窗口的使用情况。注意：上游只暴露 used_percent（百分比），
/// 没有 used/limit/remaining 绝对值。
/// </summary>
public sealed class CodexUsageWindow
{
    [JsonPropertyName("used_percent")]
    public double? Used_Percent { get; set; }
    public double? UsedPercent { get; set; }

    [JsonPropertyName("limit_window_seconds")]
    public double? Limit_Window_Seconds { get; set; }
    public double? LimitWindowSeconds { get; set; }

    [JsonPropertyName("reset_after_seconds")]
    public double? Reset_After_Seconds { get; set; }
    public double? ResetAfterSeconds { get; set; }

    [JsonPropertyName("reset_at")]
    public double? Reset_At { get; set; }
    public double? ResetAt { get; set; }
}

/// <summary>
/// 附加速率限制条目（如 Premium 系列模型等的独立限制）。
/// </summary>
public sealed class CodexAdditionalRateLimit
{
    [JsonPropertyName("limit_name")]
    public string? Limit_Name { get; set; }
    public string? LimitName { get; set; }

    [JsonPropertyName("metered_feature")]
    public string? Metered_Feature { get; set; }
    public string? MeteredFeature { get; set; }

    [JsonPropertyName("rate_limit")]
    public CodexRateLimitInfo? Rate_Limit { get; set; }
    public CodexRateLimitInfo? RateLimit { get; set; }
}
