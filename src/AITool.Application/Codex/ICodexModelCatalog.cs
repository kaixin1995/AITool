namespace AITool.Application.Codex;

/// <summary>
/// Codex 静态模型目录接口。按订阅计划返回该账号可见的模型名列表（含 builtin 图片模型）。
/// </summary>
public interface ICodexModelCatalog
{
    /// <summary>
    /// 按 plan（free/plus/team/pro）返回对应分层的模型名列表；未知/空返回 pro 分层（对应 CPA default）。
    /// </summary>
    IReadOnlyList<string> GetModelsForPlan(string? planType);
}
