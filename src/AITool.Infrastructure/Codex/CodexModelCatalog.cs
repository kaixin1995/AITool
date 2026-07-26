using System.Collections.Concurrent;
using AITool.Application.Codex;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// Codex 静态模型目录实现。分层列表数据移植自 CPA
/// （reference-projects/CLIProxyAPI/internal/registry/models/models.json 的 codex-free/-team/-plus/-pro 键）。
/// 进程内只读，零 IO；按 plan 选择分层并注入 builtin 图片模型。
/// </summary>
public sealed class CodexModelCatalog : ICodexModelCatalog
{
    // —— 静态分层目录（快照自 CPA models.json，2026-07-03 校对）——
    private static readonly IReadOnlyList<string> Free =
        ["gpt-5.4-mini", "gpt-5.5", "codex-auto-review"];

    private static readonly IReadOnlyList<string> Team =
        ["gpt-5.4", "gpt-5.4-mini", "gpt-5.5", "codex-auto-review"];

    private static readonly IReadOnlyList<string> Plus =
        ["gpt-5.3-codex-spark", "gpt-5.4", "gpt-5.4-mini", "gpt-5.5", "codex-auto-review"];

    private static readonly IReadOnlyList<string> Pro =
        ["gpt-5.3-codex-spark", "gpt-5.4", "gpt-5.4-mini", "gpt-5.5", "codex-auto-review"];

    // —— builtin 图片模型（CPA WithCodexBuiltins 注入逻辑）——
    private static readonly IReadOnlyList<string> Builtins =
        ["gpt-image-1.5", "gpt-image-2"];

    // —— 按 plan 缓存拼接结果，避免每次调用都 Concat 新建 List ——
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> Cache = new();

    /// <inheritdoc />
    public IReadOnlyList<string> GetModelsForPlan(string? planType)
    {
        var key = (planType ?? string.Empty).ToLowerInvariant();
        return Cache.GetOrAdd(key, ComputeModels);
    }

    private static IReadOnlyList<string> ComputeModels(string key)
    {
        // 分层选择规则照搬 CPA sdk/cliproxy/service.go:1984-2009
        var tier = key switch
        {
            "pro" => Pro,
            "plus" => Plus,
            "team" or "business" or "go" => Team,
            "free" => Free,
            _ => Pro, // 未知/空 default = pro
        };
        return tier.Concat(Builtins).Distinct().ToList();
    }
}
