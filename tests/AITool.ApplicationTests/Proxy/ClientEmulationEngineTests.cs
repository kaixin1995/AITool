using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AITool.Domain.Sites;
using AITool.Infrastructure.Proxy;
using Xunit;

namespace AITool.ApplicationTests.Proxy;

public sealed class ClientEmulationEngineTests
{
    [Fact]
    public void EvaluatePlaceholders_ReplacesGuidAndNanoIdAndTimestamp()
    {
        var template = "req_${guid}_ses_${nanoid:10}_time_${timestamp}_hex_${random_hex:8}_model_${model}";
        var result = ClientEmulationEngine.EvaluatePlaceholders(template, "gpt-4o", "proj-123");

        Assert.DoesNotContain("${guid}", result);
        Assert.DoesNotContain("${nanoid:10}", result);
        Assert.DoesNotContain("${timestamp}", result);
        Assert.DoesNotContain("${random_hex:8}", result);
        Assert.DoesNotContain("${model}", result);

        Assert.Contains("model_gpt-4o", result);
        Assert.StartsWith("req_", result);
        Assert.Contains("_ses_", result);
        Assert.Contains("_time_", result);
        Assert.Contains("_hex_", result);
    }

    [Fact]
    public void EvaluatePlaceholders_ReplacesGuidNFormat()
    {
        var template = "${guid:N}";
        var result = ClientEmulationEngine.EvaluatePlaceholders(template, null, null);

        Assert.Equal(32, result.Length);
        Assert.Matches("^[0-9a-f]{32}$", result);
    }

    [Fact]
    public void EvaluatePlaceholders_ReplacesProjectId()
    {
        var template = "projects/${project_id}";
        var result = ClientEmulationEngine.EvaluatePlaceholders(template, null, "my-gcp-project");

        Assert.Equal("projects/my-gcp-project", result);
    }

    [Fact]
    public void ResolveHeaders_OpenCode_GeneratesExpectedHeaders()
    {
        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.OpenCode,
            null,
            "claude-3-5-sonnet",
            null,
            false);

        Assert.StartsWith("opencode/1.18.18", headers["User-Agent"]);
        Assert.Equal("cli", headers["x-opencode-client"]);
        Assert.StartsWith("ses_", headers["x-session-affinity"]);
        Assert.StartsWith("ses_", headers["x-session-id"]);
        Assert.StartsWith("msg_", headers["x-opencode-request"]);
    }

    [Fact]
    public void ResolveHeaders_ClaudeCode_GeneratesAnthropicHeaders()
    {
        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.ClaudeCode,
            null,
            "claude-3-7-sonnet-20250219",
            null,
            false);

        Assert.StartsWith("claude-cli/2.1.241", headers["User-Agent"]);
        Assert.Equal("cli", headers["x-app"]);
        Assert.Equal("2023-06-01", headers["anthropic-version"]);
        Assert.True(headers.ContainsKey("anthropic-beta"));
        Assert.True(headers.ContainsKey("X-Claude-Code-Session-Id"));
        Assert.Equal("Windows", headers["X-Stainless-OS"]);
    }

    [Fact]
    public void ResolveHeaders_CodexCli_GeneratesCodexDesktopHeaders()
    {
        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.CodexCli,
            null,
            "gpt-4o",
            null,
            false);

        Assert.StartsWith("Codex Desktop/0.153.3", headers["User-Agent"]);
        Assert.Equal("Codex Desktop", headers["Originator"]);
        Assert.True(headers.ContainsKey("Session-Id"));
        Assert.True(headers.ContainsKey("Thread-Id"));
        Assert.True(headers.ContainsKey("X-Client-Request-Id"));
        Assert.True(headers.ContainsKey("X-Codex-Turn-Metadata"));
        Assert.True(headers.ContainsKey("X-Oai-Attestation"));
    }

    [Fact]
    public void ResolveHeaders_CodexVsCode_GeneratesCodexVsCodeHeaders()
    {
        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.CodexVsCode,
            null,
            "gpt-4o",
            null,
            false);

        Assert.StartsWith("codex_vscode/0.149.0", headers["User-Agent"]);
        Assert.Equal("codex_vscode", headers["Originator"]);
        Assert.True(headers.ContainsKey("Session-Id"));
        Assert.True(headers.ContainsKey("Thread-Id"));
        Assert.True(headers.ContainsKey("X-Client-Request-Id"));
        Assert.True(headers.ContainsKey("X-Codex-Turn-Metadata"));
    }

    [Fact]
    public void ResolveHeaders_ZCode_GeneratesZCodeHeaders()
    {
        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.ZCode,
            null,
            "glm-4-plus",
            null,
            false);

        Assert.StartsWith("ZCode/3.9.1", headers["User-Agent"]);
        Assert.Equal("https://zcode.z.ai", headers["http-referer"]);
        Assert.Equal("glm", headers["x-zcode-agent"]);
        Assert.Equal("3.9.1", headers["x-zcode-app-version"]);
        Assert.True(headers.ContainsKey("x-query-id"));
        Assert.True(headers.ContainsKey("x-session-id"));
    }

    [Fact]
    public void ResolveHeaders_Antigravity_GeneratesAntigravityHeaders()
    {
        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.Antigravity,
            null,
            "gemini-2.5-pro",
            "gcp-proj-1",
            true);

        Assert.StartsWith("antigravity/cli/1.1.20", headers["User-Agent"]);
        Assert.False(headers.ContainsKey("x-goog-api-client"));
        Assert.False(headers.ContainsKey("requestId"));
        Assert.False(headers.ContainsKey("requestType"));
    }

    [Fact]
    public void ResolveHeaders_WorkBuddy_GeneratesOfficialClientFingerprint()
    {
        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.WorkBuddy,
            null,
            "deepseek-v4.1",
            null,
            false);

        // 官方客户端 UA 三段式（对齐 5.9.3 / CLI 2.165.0 真实抓包）。
        Assert.Equal("WorkBuddy/5.9.3 WorkBuddy/5.9.3 CLI/2.165.0", headers["User-Agent"]);
        // 渠道校验（code 11128）关键指纹族。
        Assert.Equal("js", headers["x-stainless-lang"]);
        Assert.Equal("6.25.0", headers["x-stainless-package-version"]);
        Assert.Equal("node", headers["x-stainless-runtime"]);
        Assert.Equal("v22.21.1", headers["x-stainless-runtime-version"]);
        Assert.Equal("WorkBuddy", headers["X-IDE-Type"]);
        Assert.Equal("5.9.3", headers["X-IDE-Version"]);
        Assert.Equal("SaaS", headers["X-Product"]);
        Assert.Equal("1", headers["X-CodeBuddy-Request"]);
        Assert.Equal("craft", headers["X-Agent-Intent"]);
        // 会话类 ID 占位符已求值为 GUID，不再残留模板。
        Assert.DoesNotContain("${", headers["X-Conversation-ID"]);
        Assert.Matches("^[0-9a-f-]{36}$", headers["X-Conversation-ID"]);
        Assert.DoesNotContain("${", headers["acp-connection-id"]);
        Assert.NotEqual(headers["X-Conversation-ID"], headers["X-Conversation-Message-ID"]);
        // 账号级身份头不在静态预设中：由托管站点按账号注入或站点自定义头覆盖。
        Assert.False(headers.ContainsKey("X-User-Id"));
        Assert.False(headers.ContainsKey("X-Domain"));
        Assert.False(headers.ContainsKey("X-Machine-ID"));
        // 内部标记（不透传）：转发层据此做渠道校验 11128 的请求体内容适配。
        Assert.Equal("workbuddy", headers[ProxyForwardService.RewriteChannelHeader]);
    }

    [Fact]
    public void Normalize_AcceptsWorkBuddyAliases()
    {
        Assert.Equal(ClientEmulationConstants.WorkBuddy, ClientEmulationConstants.Normalize("WorkBuddy"));
        Assert.Equal(ClientEmulationConstants.WorkBuddy, ClientEmulationConstants.Normalize("workbuddy"));
        Assert.Equal(ClientEmulationConstants.WorkBuddy, ClientEmulationConstants.Normalize("CodeBuddy"));
        Assert.Equal(ClientEmulationConstants.WorkBuddy, ClientEmulationConstants.Normalize("codebuddy-ide"));
    }

    [Fact]
    public void ResolveHeaders_ExtraHeaders_OverridePresets()
    {
        var extraHeaders = new Dictionary<string, string>
        {
            ["User-Agent"] = "CustomOverriddenAgent/1.0",
            ["x-custom-tag"] = "${nanoid:8}"
        };

        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.OpenCode,
            extraHeaders,
            "claude-3-5-sonnet",
            null,
            false);

        Assert.Equal("CustomOverriddenAgent/1.0", headers["User-Agent"]);
        Assert.Equal("cli", headers["x-opencode-client"]);
        Assert.True(headers.ContainsKey("x-custom-tag"));
        Assert.Equal(8, headers["x-custom-tag"].Length);
    }

    [Fact]
    public void ClientEmulationConstants_Normalize_HandlesCases()
    {
        Assert.Equal(ClientEmulationConstants.OpenCode, ClientEmulationConstants.Normalize("opencode"));
        Assert.Equal(ClientEmulationConstants.ClaudeCode, ClientEmulationConstants.Normalize("Claude_Code"));
        Assert.Equal(ClientEmulationConstants.CodexCli, ClientEmulationConstants.Normalize("codex"));
        Assert.Equal(ClientEmulationConstants.CodexCli, ClientEmulationConstants.Normalize("codexdesktop"));
        Assert.Equal(ClientEmulationConstants.CodexVsCode, ClientEmulationConstants.Normalize("codex_vscode"));
        Assert.Equal(ClientEmulationConstants.ZCode, ClientEmulationConstants.Normalize("zcode"));
        Assert.Equal(ClientEmulationConstants.Antigravity, ClientEmulationConstants.Normalize("ANTIGRAVITY"));
        Assert.Equal(ClientEmulationConstants.Antigravity, ClientEmulationConstants.Normalize("gemini"));
        Assert.Equal(ClientEmulationConstants.Custom, ClientEmulationConstants.Normalize("custom"));
        Assert.Equal(ClientEmulationConstants.None, ClientEmulationConstants.Normalize(null));
        Assert.Equal(ClientEmulationConstants.None, ClientEmulationConstants.Normalize(""));
        Assert.Equal(ClientEmulationConstants.None, ClientEmulationConstants.Normalize("None"));
        Assert.Equal(ClientEmulationConstants.None, ClientEmulationConstants.Normalize("none"));
        Assert.Equal("my-custom-profile", ClientEmulationConstants.Normalize("my-custom-profile"));
    }

    [Theory]
    [InlineData("OpenCode", "ClaudeCode", "Antigravity", "OpenCode")]
    [InlineData("None", "ClaudeCode", "Antigravity", "ClaudeCode")]
    [InlineData("None", "None", "Antigravity", "Antigravity")]
    [InlineData(null, null, "Antigravity", "Antigravity")]
    [InlineData(null, null, null, "None")]
    public void ResolveClientEmulation_FollowsPrecedenceHierarchy(
        string? mappingEmulation,
        string? modelEmulation,
        string? siteEmulation,
        string expected)
    {
        // Mapping > Model > Site > None
        var normMapping = ClientEmulationConstants.Normalize(mappingEmulation);
        string result;
        if (!string.Equals(normMapping, ClientEmulationConstants.None, StringComparison.OrdinalIgnoreCase))
        {
            result = normMapping;
        }
        else
        {
            var normModel = ClientEmulationConstants.Normalize(modelEmulation);
            if (!string.Equals(normModel, ClientEmulationConstants.None, StringComparison.OrdinalIgnoreCase))
            {
                result = normModel;
            }
            else
            {
                result = ClientEmulationConstants.Normalize(siteEmulation);
            }
        }

        Assert.Equal(expected, result);
    }

    [Fact]
    public void EvaluatePlaceholders_HandlesUnknownAndUnpassedPlaceholdersWithSmartDefaults()
    {
        var complexJsonTemplate = "{\"installation_id\":\"${guid}\",\"session_id\":\"${session_id}\",\"thread_id\":\"${thread_id}\",\"time\":${timestamp_ms},\"custom\":\"${my_custom_tag}\"}";
        var result = ClientEmulationEngine.EvaluatePlaceholders(complexJsonTemplate, null, null);

        Assert.DoesNotContain("${", result);
        Assert.DoesNotContain("}", result[..^1]); // except final closing brace
        Assert.DoesNotContain("${guid}", result);
        Assert.DoesNotContain("${session_id}", result);
        Assert.DoesNotContain("${thread_id}", result);
        Assert.DoesNotContain("${timestamp_ms}", result);
        Assert.DoesNotContain("${my_custom_tag}", result);
    }
}
