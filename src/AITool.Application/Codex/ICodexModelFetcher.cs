namespace AITool.Application.Codex;

/// <summary>
/// 动态拉取 Codex 上游模型目录的客户端接口。
/// 对应 CPA cmd/fetch_codex_models/main.go，请求 chatgpt.com/backend-api/codex/models。
/// </summary>
public interface ICodexModelFetcher
{
    /// <summary>
    /// 拉取上游 Codex 模型目录。需有效 accessToken 与 accountId。
    /// </summary>
    Task<IReadOnlyList<CodexRemoteModel>> FetchAsync(string accessToken, string accountId, CancellationToken cancellationToken);
}
