using AITool.Domain.Sites;
using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 客户端特征模拟的三层解析与 HeaderProfile 模板合并的单元级回归测试。
/// </summary>
public sealed class ClientEmulationResolutionTests
{
    // ============ ResolveClientEmulation：内置预设归一化 / 自定义 Key 透传 ============

    [Fact]
    public void ResolveClientEmulation_prefers_mapping_and_normalizes_builtin_presets()
    {
        ProxyRequestMetadataCache.ResolveClientEmulation("claude-code", null, "OpenCode")
            .Should().Be("ClaudeCode", "映射层优先且内置预设应归一化");
        ProxyRequestMetadataCache.ResolveClientEmulation(null, "gemini", null)
            .Should().Be("GeminiCli", "模型层级识别别名并归一化");
        ProxyRequestMetadataCache.ResolveClientEmulation(null, null, "antigravity")
            .Should().Be("Antigravity");
    }

    [Fact]
    public void ResolveClientEmulation_passes_through_custom_profile_keys()
    {
        // 自定义 HeaderProfile Key 不能被归一化吞掉——否则模板在运行时永远无法命中。
        ProxyRequestMetadataCache.ResolveClientEmulation("my-custom-1", "OpenCode", null)
            .Should().Be("my-custom-1", "映射层的自定义 Key 应原样透传供模板解析");
        ProxyRequestMetadataCache.ResolveClientEmulation(null, null, "hk-claude-profile")
            .Should().Be("hk-claude-profile");
    }

    [Fact]
    public void ResolveClientEmulation_returns_none_when_all_levels_empty()
    {
        ProxyRequestMetadataCache.ResolveClientEmulation(null, "  ", "")
            .Should().Be(ClientEmulationConstants.None);
        ProxyRequestMetadataCache.ResolveClientEmulation("None", "none", "None")
            .Should().Be(ClientEmulationConstants.None);
    }

    [Fact]
    public void ResolveClientEmulation_falls_back_when_higher_levels_are_none()
    {
        ProxyRequestMetadataCache.ResolveClientEmulation("None", "OpenCode", null)
            .Should().Be("OpenCode", "映射层为 None 时应回退至模型层");
        ProxyRequestMetadataCache.ResolveClientEmulation("None", "None", "antigravity")
            .Should().Be("Antigravity", "映射层与模型层均为 None 时应回退至站点层");
        ProxyRequestMetadataCache.ResolveClientEmulation("None", "None", "my-custom-profile")
            .Should().Be("my-custom-profile", "映射层与模型层均为 None 时应回退至站点层自定义 Key");
    }

    // ============ BuildEffectiveExtraHeaders：模板最底层 + 显式配置覆盖 ============

    private static Dictionary<string, Dictionary<string, string>> ProfileMap(params (string Key, string Json)[] profiles)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, json) in profiles)
        {
            // 复用生产解析器语义：手写最小 JSON 展开即可。
            var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is { Count: > 0 })
            {
                map[key] = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
        }
        return map;
    }

    [Fact]
    public void BuildEffectiveExtraHeaders_injects_custom_profile_template()
    {
        var map = ProfileMap(("my-custom-1", "{\"User-Agent\":\"MyAgent/1.0\",\"X-Trace\":\"${guid}\"}"));

        var headers = ProxyRequestMetadataCache.BuildEffectiveExtraHeaders(
            "my-custom-1", map,
            siteJson: null, modelJson: null, mappingJson: null);

        headers["User-Agent"].Should().Be("MyAgent/1.0");
        headers["X-Trace"].Should().Be("${guid}", "占位符保留原文，由引擎在请求时求值");
    }

    [Fact]
    public void BuildEffectiveExtraHeaders_explicit_layers_override_profile_template()
    {
        var map = ProfileMap(("ClaudeCode", "{\"User-Agent\":\"EditedAgent/9.9\",\"anthropic-beta\":\"edited\"}"));

        var headers = ProxyRequestMetadataCache.BuildEffectiveExtraHeaders(
            "ClaudeCode", map,
            siteJson: "{\"User-Agent\":\"SiteAgent/2.0\"}",
            modelJson: null,
            mappingJson: "{\"anthropic-version\":\"2023-06-01\"}");

        headers["User-Agent"].Should().Be("SiteAgent/2.0", "显式站点头应覆盖被编辑的内置模板");
        headers["anthropic-beta"].Should().Be("edited", "未被覆盖的模板字段应保留（编辑内置预设生效）");
        headers["anthropic-version"].Should().Be("2023-06-01");
    }

    [Fact]
    public void BuildEffectiveExtraHeaders_without_profile_map_keeps_legacy_merge()
    {
        var headers = ProxyRequestMetadataCache.BuildEffectiveExtraHeaders(
            "None", headerProfileMap: null,
            siteJson: "{\"A\":\"1\"}", modelJson: "{\"B\":\"2\"}", mappingJson: "{\"A\":\"3\"}");

        headers["A"].Should().Be("3", "映射层覆盖站点层（与历史合并语义一致）");
        headers["B"].Should().Be("2");
    }

    [Fact]
    public void BuildEffectiveExtraHeaders_ignores_unknown_profile_key()
    {
        var map = ProfileMap(("other-key", "{\"User-Agent\":\"X\"}"));

        var headers = ProxyRequestMetadataCache.BuildEffectiveExtraHeaders(
            "missing-key", map, siteJson: "{\"S\":\"1\"}", modelJson: null, mappingJson: null);

        headers.Should().NotContainKey("User-Agent");
        headers["S"].Should().Be("1", "未知 Key 不注入任何模板，仅保留显式配置");
    }
}
