using AITool.Application.Pricing;
using FluentAssertions;

namespace AITool.ApplicationTests.Pricing;

/// <summary>
/// 公开价格源匹配器契约。用例取自 models.dev / LiteLLM 真实数据形态：
/// 裸 ID 跨厂商撞名（glm-5.3 同时挂在 zai 与数十个转售商下）、聚合前缀不一致
/// （openai/gpt-oss-120b 实际收录在 azure_ai/ 下）、思考/档位后缀（claude-sonnet-4-6-thinking）。
/// </summary>
public sealed class ModelPriceSourceMatcherTests
{
    private static ModelPriceSourceEntry Entry(string key, string provider, decimal input = 1m)
        => new(key, provider, input, input * 2, 0m, 0m);

    private static Dictionary<string, List<ModelPriceSourceEntry>> BuildIndex(
        params (string Key, string Provider)[] items)
    {
        var index = new Dictionary<string, List<ModelPriceSourceEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, provider) in items)
        {
            if (!index.TryGetValue(key, out var list))
            {
                list = [];
                index[key] = list;
            }
            list.Add(Entry(key, provider));
        }
        return index;
    }

    [Fact]
    public void Exact_key_matches_directly()
    {
        var index = BuildIndex(("gpt-4o", "openai"));
        ModelPriceSourceMatcher.TryMatch("gpt-4o", index, out var entry, out var key)
            .Should().BeTrue();
        entry!.Provider.Should().Be("openai");
        key.Should().Be("gpt-4o");
    }

    [Fact]
    public void Prefixed_query_matches_aggregator_suffix_key()
    {
        // openai/gpt-oss-120b 在 LiteLLM 里收录为 azure_ai/gpt-oss-120b。
        var index = BuildIndex(("azure_ai/gpt-oss-120b", "azure_ai"));
        ModelPriceSourceMatcher.TryMatch("openai/gpt-oss-120b", index, out var entry, out _)
            .Should().BeTrue();
        entry!.Provider.Should().Be("azure_ai");
    }

    [Fact]
    public void Prefixed_query_prefers_same_provider_on_exact_key()
    {
        var index = BuildIndex(("openai/gpt-4o", "openai"), ("gpt-4o", "azure"));
        ModelPriceSourceMatcher.TryMatch("openai/gpt-4o", index, out var entry, out _)
            .Should().BeTrue();
        entry!.Provider.Should().Be("openai");
    }

    [Fact]
    public void Bare_id_collision_prefers_first_party_provider_over_resellers()
    {
        // glm-5.3 同时挂在 zai 与 digitalocean/vivgrid 等转售商名下，应取官方 zai。
        var index = BuildIndex(
            ("glm-5.3", "digitalocean"),
            ("glm-5.3", "vivgrid"),
            ("glm-5.3", "zai"));
        ModelPriceSourceMatcher.TryMatch("glm-5.3", index, out var entry, out _)
            .Should().BeTrue();
        entry!.Provider.Should().Be("zai");
    }

    [Fact]
    public void Thinking_suffix_strips_to_base_model()
    {
        var index = BuildIndex(("claude-sonnet-4-6", "anthropic"));
        ModelPriceSourceMatcher.TryMatch("claude-sonnet-4-6-thinking", index, out var entry, out var key)
            .Should().BeTrue();
        entry!.Provider.Should().Be("anthropic");
        key.Should().Be("claude-sonnet-4-6");
    }

    [Fact]
    public void Thinking_variant_with_own_entry_wins_over_base()
    {
        // 命中一层即止：完整 ID 直接收录时不剥后缀。
        var index = BuildIndex(
            ("302ai/claude-sonnet-4-6-thinking", "302ai"),
            ("claude-sonnet-4-6", "anthropic"));
        ModelPriceSourceMatcher.TryMatch("claude-sonnet-4-6-thinking", index, out var entry, out _)
            .Should().BeTrue();
        entry!.Provider.Should().Be("302ai");
    }

    [Fact]
    public void Unknown_model_returns_false()
    {
        var index = BuildIndex(("gpt-4o", "openai"));
        ModelPriceSourceMatcher.TryMatch("totally-unknown-model", index, out _, out _)
            .Should().BeFalse();
        ModelPriceSourceMatcher.TryMatch("", index, out _, out _).Should().BeFalse();
        ModelPriceSourceMatcher.TryMatch(null, index, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Mini_suffix_is_not_stripped()
    {
        // -mini 是独立模型而非档位后缀，不得剥成 base。
        var index = BuildIndex(("gpt-4.1", "openai"));
        ModelPriceSourceMatcher.TryMatch("gpt-4.1-mini", index, out _, out _)
            .Should().BeFalse();
    }
}
