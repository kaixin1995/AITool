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

        Assert.StartsWith("opencode/1.15.0", headers["User-Agent"]);
        Assert.Equal("cli", headers["x-opencode-client"]);
        Assert.StartsWith("msg_", headers["x-opencode-request"]);
        Assert.StartsWith("ses_", headers["x-opencode-session"]);
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

        Assert.Equal("claude-code", headers["anthropic-client-name"]);
        Assert.Equal("0.2.29", headers["anthropic-client-version"]);
        Assert.StartsWith("claude-code/0.2.29", headers["User-Agent"]);
    }

    [Fact]
    public void ResolveHeaders_CodexCli_GeneratesCopilotHeaders()
    {
        var headers = ClientEmulationEngine.ResolveHeaders(
            ClientEmulationConstants.CodexCli,
            null,
            "gpt-4o",
            null,
            false);

        Assert.StartsWith("GitHubCopilotChat/0.24.1", headers["User-Agent"]);
        Assert.Equal("vscode/1.96.2", headers["Editor-Version"]);
        Assert.Equal("github-copilot", headers["Openai-Organization"]);
        Assert.True(headers.ContainsKey("Session-Id"));
        Assert.True(headers.ContainsKey("X-Request-Id"));
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
        Assert.Equal(ClientEmulationConstants.Antigravity, ClientEmulationConstants.Normalize("ANTIGRAVITY"));
        Assert.Equal(ClientEmulationConstants.GeminiCli, ClientEmulationConstants.Normalize("gemini"));
        Assert.Equal(ClientEmulationConstants.Custom, ClientEmulationConstants.Normalize("custom"));
        Assert.Equal(ClientEmulationConstants.None, ClientEmulationConstants.Normalize(null));
        Assert.Equal(ClientEmulationConstants.None, ClientEmulationConstants.Normalize(""));
        Assert.Equal(ClientEmulationConstants.None, ClientEmulationConstants.Normalize("unknown-value"));
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
