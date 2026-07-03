namespace AITool.Application.Codex;

/// <summary>
/// Codex 账号供给统一入参：OAuth 完成与凭证导入共用同一流程。
/// </summary>
public sealed class CodexProvisionInput
{
    public string DisplayName { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public string IdToken { get; set; } = string.Empty;

    public string? AccountId { get; set; }

    public string? Email { get; set; }

    /// <summary>订阅计划，null 时模型目录按 default(pro) 处理。</summary>
    public string? PlanType { get; set; }

    public DateTimeOffset? TokenExpiresAt { get; set; }
}
