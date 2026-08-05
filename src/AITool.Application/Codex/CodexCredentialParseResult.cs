namespace AITool.Application.Codex;

/// <summary>
/// 解析 Codex 凭证文件（CPA 格式 JSON）的逐项结果。批量导入时每个文件一个结果（含失败项）。
/// </summary>
public sealed class CodexCredentialParseResult
{
    /// <summary>是否解析成功。</summary>
    public bool Success { get; set; }

    /// <summary>失败原因（Success=false 时）。</summary>
    public string? Error { get; set; }

    /// <summary>原始文件名（批量时用于定位）。</summary>
    public string? FileName { get; set; }

    /// <summary>建议默认显示名：email ?? 文件名去 .json ?? "Codex 账号"。</summary>
    public string? DisplayName { get; set; }

    /// <summary>是否因缺少 type 字段而推断为 codex（宽松模式）。</summary>
    public bool TypeInferred { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public string? IdToken { get; set; }

    public string? AccountId { get; set; }

    public string? Email { get; set; }

    public string? PlanType { get; set; }

    /// <summary>access_token 过期时间（优先 expired 字段，其次 JWT exp）。</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }
}
