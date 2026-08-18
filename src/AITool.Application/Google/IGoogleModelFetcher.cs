namespace AITool.Application.Google;

/// <summary>
/// Google 上游模型清单拉取：Antigravity 走 v1internal:fetchAvailableModels 动态获取
/// （对齐 gcli2api fetch_available_models，含 claude-*-thinking 补齐）；GeminiCli 用静态清单。
/// </summary>
public interface IGoogleModelFetcher
{
    /// <summary>
    /// 拉取指定接入方式的可用模型（slug 与展示名）。
    /// </summary>
    Task<IReadOnlyList<(string Slug, string DisplayName)>> FetchAsync(string accountKind, string accessToken, CancellationToken ct);
}
