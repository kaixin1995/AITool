namespace AITool.Application.Codex;

/// <summary>
/// 解析 Codex id_token JWT 后得到的关键 claim 信息。
/// 对应 CPA 的 JWTClaims + CodexAuthInfo（claim key 为 "https://api.openai.com/auth"）。
/// </summary>
public sealed class CodexIdTokenClaims
{
    /// <summary>chatgpt_account_id，作为账号去重的首选依据。</summary>
    public string? AccountId { get; set; }

    /// <summary>用户邮箱。</summary>
    public string? Email { get; set; }

    /// <summary>订阅计划类型：free / plus / team / pro。</summary>
    public string? PlanType { get; set; }

    /// <summary>chatgpt_user_id。</summary>
    public string? UserId { get; set; }

    /// <summary>订阅窗口开始时间（展示用）。</summary>
    public DateTimeOffset? SubscriptionWindowStart { get; set; }

    /// <summary>订阅窗口结束时间（展示用）。</summary>
    public DateTimeOffset? SubscriptionWindowEnd { get; set; }
}
