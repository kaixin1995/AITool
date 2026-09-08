namespace AITool.Application.Codex;

/// <summary>
/// 远端模型目录刷新结果。失败时保留现状（Success=false），绝不影响调用方主流程。
/// </summary>
public sealed record CodexCatalogRefreshResult
{
    /// <summary>是否成功拉取并通过校验。</summary>
    public bool Success { get; init; }

    /// <summary>相对当前内存目录是否有变化。</summary>
    public bool Changed { get; init; }

    /// <summary>失败原因（Success=false 时）。</summary>
    public string? Error { get; init; }

    /// <summary>数据来源 URL（Success=true 时）。</summary>
    public string? Source { get; init; }
}

/// <summary>
/// Codex 模型目录接口。按订阅计划返回该账号可见的模型名列表（含 builtin 图片模型）。
/// 目录支持从远端（router-for-me/models，CPA 同源）按需刷新，静态内置分层仅作兜底。
/// </summary>
public interface ICodexModelCatalog
{
    /// <summary>
    /// 按 plan（free/plus/team/pro）返回对应分层的模型名列表；未知/空返回 pro 分层（对应 CPA default）。
    /// </summary>
    IReadOnlyList<string> GetModelsForPlan(string? planType);

    /// <summary>
    /// 从远端拉取最新分层目录并通过校验后原子替换内存目录。任何失败保留现状并返回
    /// Success=false（Error 说明原因），不抛异常、不阻断调用方主流程。
    /// </summary>
    Task<CodexCatalogRefreshResult> TryRefreshFromRemoteAsync(CancellationToken cancellationToken = default);
}
