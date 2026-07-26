using System.Text.Json;

namespace AITool.Domain.Proxy;

/// <summary>
/// 兼容规则集的解析与查询工具。
/// <para>
/// 抽到 Domain 层供 Admin（构建快照/patch）与 Infrastructure 缓存层共用，
/// 避免解析逻辑在多处重复实现导致行为漂移。
/// </para>
/// </summary>
public static class CompatibilityRuleParser
{
    /// <summary>
    /// 解析规则集的 RulesJson 为规则列表。解析失败返回空列表，不影响转发。
    /// </summary>
    public static IReadOnlyList<CompatibilityRule> Parse(string? rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson)) return Array.Empty<CompatibilityRule>();
        try
        {
            var rules = JsonSerializer.Deserialize<List<CompatibilityRule>>(rulesJson);
            return rules is null || rules.Count == 0 ? Array.Empty<CompatibilityRule>() : rules;
        }
        catch
        {
            return Array.Empty<CompatibilityRule>();
        }
    }

    /// <summary>
    /// 取模型关联的兼容规则集（按 CompatibilityProfileId 查字典）。
    /// profileId 为空、Guid.Empty 或字典里没有对应项则返回空列表。
    /// </summary>
    /// <param name="profileId">模型关联的兼容规则集 Id（来自 ModelLibraryItem.CompatibilityProfileId）。</param>
    /// <param name="profileRules">已解析的 Id→规则列表字典（仅含启用的规则集）。</param>
    public static IReadOnlyList<CompatibilityRule> GetRulesForModel(
        Guid? profileId,
        Dictionary<Guid, IReadOnlyList<CompatibilityRule>> profileRules)
    {
        if (profileId is null || profileId == Guid.Empty) return Array.Empty<CompatibilityRule>();
        return profileRules.TryGetValue(profileId.Value, out var rules) ? rules : Array.Empty<CompatibilityRule>();
    }

    /// <summary>
    /// 把规则集列表构建为 Id→规则列表字典（仅含启用的规则集）。
    /// 供路由目标构建时按 model 的 CompatibilityProfileId 快速查规则，避免 N+1 解析。
    /// </summary>
    public static Dictionary<Guid, IReadOnlyList<CompatibilityRule>> BuildProfileRuleMap(
        IEnumerable<CompatibilityProfile> profiles)
    {
        return profiles
            .Where(p => p.IsEnabled)
            .ToDictionary(
                p => p.Id,
                p => Parse(p.RulesJson));
    }
}
