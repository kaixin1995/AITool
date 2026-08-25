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

        Assert.StartsWith("Codex Desktop/0.149.0", headers["User-Agent"]);
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

        Assert.StartsWith("antigravity/1.10.4", headers["User-Agent"]);
        Assert.Equal("gl-node/20.18.0 antigravity-cli/1.10.4", headers["x-goog-api-client"]);
        Assert.True(headers.ContainsKey("requestId"));
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
        Assert.Equal(ClientEmulationConstants.GeminiCli, ClientEmulationConstants.Normalize("gemini"));
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
    [InlineData(null, null, "GeminiCli", "GeminiCli")]
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
}
