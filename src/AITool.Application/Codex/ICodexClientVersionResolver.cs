namespace AITool.Application.Codex;

/// <summary>
/// 解析后的 Codex 客户端伪装信息。版本号与 User-Agent 出自同一来源，保证两者一致。
/// </summary>
public sealed record CodexClientVersionInfo
{
    /// <summary>客户端版本号（如 0.153.3），用于 models 等端点的 client_version 参数。</summary>
    public string ClientVersion { get; init; } = string.Empty;

    /// <summary>完整 User-Agent 串（含同版本号）。</summary>
    public string UserAgent { get; init; } = string.Empty;

    /// <summary>来源说明：profile:CodexCli（请求头模板库）或 fallback（配置兜底）。诊断日志用。</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>
/// Codex 客户端版本解析器：优先从请求头模板库的 CodexCli 档案 User-Agent 中解析版本号
/// （用户在「请求头模板库」UI 更新 UA 版本后，拉模型/查额度随之生效），失败回落配置兜底值。
/// </summary>
public interface ICodexClientVersionResolver
{
    /// <summary>
    /// 解析当前应使用的客户端版本与 User-Agent。模板库读取失败时返回配置兜底，不抛异常。
    /// </summary>
    Task<CodexClientVersionInfo> ResolveAsync(CancellationToken cancellationToken = default);
}
