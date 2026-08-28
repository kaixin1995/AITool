namespace AITool.Application.Codex;

/// <summary>
/// Codex 上游（chatgpt.com/backend-api）的客户端伪装配置。
/// 上游会校验客户端版本号，提取到配置便于不发版调整。
/// </summary>
public sealed class CodexUpstreamOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "CodexUpstream";

    /// <summary>
    /// 伪装的 Codex 客户端版本号（用于 models/usage 等端点的 client_version 参数与 User-Agent）。
    /// </summary>
    public string ClientVersion { get; set; } = "0.133.0";
}
