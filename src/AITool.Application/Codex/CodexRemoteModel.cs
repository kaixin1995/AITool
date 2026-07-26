namespace AITool.Application.Codex;

/// <summary>
/// 从上游 codex/models 目录拉取的单个模型条目。
/// 对应 CPA codex_client_models.json 的结构。
/// </summary>
public sealed class CodexRemoteModel
{
    /// <summary>模型 slug，作为映射的 RemoteModelName。</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>展示名。</summary>
    public string DisplayName { get; set; } = string.Empty;
}
