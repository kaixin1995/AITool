namespace AITool.Application.Pricing;

/// <summary>
/// 公开价格源中一条模型的价格（已统一换算为 USD / 百万 tokens）。
/// </summary>
public sealed record ModelPriceSourceEntry(
    string Key,
    string Provider,
    decimal Input,
    decimal Output,
    decimal CacheRead,
    decimal CacheWrite);

/// <summary>
/// 公开价格源（models.dev / LiteLLM）的模型 ID 匹配器。纯静态、无状态。
/// <para>
/// 匹配链（命中一层即止，逐层放宽）：
/// 1. 完整 ID（如 openai/gpt-oss-120b）；
/// 2. 去掉厂商前缀的裸 ID（如 gpt-oss-120b——聚合源常把同一模型挂在别的厂商名下，如 azure_ai/gpt-oss-120b）；
/// 3. 裸 ID 剥掉思考/档位后缀（-thinking/-high/-low/-medium，如 claude-sonnet-4-6-thinking → claude-sonnet-4-6）。
/// 每层先试精确键，再试「任意厂商/候选」后缀键；同一候选命中多个厂商时，
/// 优先取与查询前缀相同的厂商，其次按第一方厂商表取官方源（裸 ID 会被大量转售商收录，价格以官方为准）。
/// </para>
/// </summary>
public static class ModelPriceSourceMatcher
{
    /// <summary>裸 ID 撞名时的第一方厂商优先级表（越小越优先）。顺序即典型官方源的可靠性顺序。</summary>
    private static readonly string[] PreferredProviders =
    [
        "openai", "anthropic", "google", "google-vertex", "deepseek", "zai", "zhipuai",
        "alibaba", "qwen", "dashscope", "moonshotai", "moonshot", "xai", "mistral",
        "minimax", "meta", "sensenova", "baidu", "bytedance", "doubao", "cohere",
        "azure", "azure_ai", "amazon-bedrock", "vertex", "openrouter"
    ];

    /// <summary>可剥离的推理档位后缀（剥一层，仅当剥离后仍非空）。</summary>
    private static readonly string[] StripSuffixes = ["-thinking", "-high", "-low", "-medium"];

    /// <summary>
    /// 在价格源索引中匹配一个模型 ID。索引键为小写；值是同键下各厂商的候选（厂商优先级取最优）。
    /// </summary>
    public static bool TryMatch(
        string? modelId,
        IReadOnlyDictionary<string, List<ModelPriceSourceEntry>> index,
        out ModelPriceSourceEntry? entry,
        out string matchedKey)
    {
        entry = null;
        matchedKey = string.Empty;
        var id = modelId?.Trim();
        if (string.IsNullOrWhiteSpace(id)) return false;

        var prefix = id.Contains('/') ? id[..id.IndexOf('/')] : null;
        var bare = id.Contains('/') ? id[(id.LastIndexOf('/') + 1)..] : id;
        var baseName = StripTrailingSuffix(bare);

        foreach (var candidate in CandidateChain(id, bare, baseName))
        {
            var best = PickBest(index, candidate, prefix);
            if (best is null) continue;
            entry = best.Value.Entry;
            matchedKey = best.Value.Key;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> CandidateChain(string full, string bare, string baseName)
    {
        yield return full;
        if (!string.Equals(bare, full, StringComparison.OrdinalIgnoreCase)) yield return bare;
        if (!string.IsNullOrWhiteSpace(baseName)
            && !string.Equals(baseName, bare, StringComparison.OrdinalIgnoreCase))
        {
            yield return baseName;
        }
    }

    private static string StripTrailingSuffix(string name)
    {
        foreach (var suffix in StripSuffixes)
        {
            if (name.Length > suffix.Length
                && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^suffix.Length];
            }
        }
        return string.Empty;
    }

    private static (ModelPriceSourceEntry Entry, string Key)? PickBest(
        IReadOnlyDictionary<string, List<ModelPriceSourceEntry>> index,
        string candidate,
        string? queryPrefix)
    {
        (ModelPriceSourceEntry, string)? best = null;
        var bestRank = int.MaxValue;

        if (index.TryGetValue(candidate, out var exactList))
        {
            foreach (var item in exactList)
            {
                var rank = ProviderRank(item.Provider, queryPrefix);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = (item, candidate);
                }
            }
        }

        var suffixKey = "/" + candidate.ToLowerInvariant();
        foreach (var (key, list) in index)
        {
            if (!key.EndsWith(suffixKey, StringComparison.Ordinal)) continue;
            foreach (var item in list)
            {
                var rank = ProviderRank(item.Provider, queryPrefix) + 1; // 后缀命中略降级，优先精确键
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = (item, key);
                }
            }
        }

        return best;
    }

    private static int ProviderRank(string provider, string? queryPrefix)
    {
        if (!string.IsNullOrEmpty(queryPrefix)
            && string.Equals(provider, queryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var idx = Array.FindIndex(PreferredProviders, p => string.Equals(p, provider, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx + 1 : 100;
    }
}
