namespace AITool.Application.Codex;

/// <summary>
/// Codex 上游（chatgpt.com/backend-api）的客户端伪装配置。
/// 版本号的正常运行来源是请求头模板库（CodexCli 档案 User-Agent），此处仅作解析失败时的兜底。
/// </summary>
public sealed class CodexUpstreamOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "CodexUpstream";

    /// <summary>
    /// 伪装的 Codex 客户端版本号（用于 models/usage 等端点的 client_version 参数与 User-Agent 兜底）。
    /// 需不低于上游新模型的 minimal_client_version 门槛（如 gpt-6-astra 要求 0.153.0）。
    /// </summary>
    public string ClientVersion { get; set; } = "0.153.3";
}
