using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AITool.Application.Google;
using AITool.Domain.Sites;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 客户端特征模拟与动态 Header 模板引擎。
/// 负责为发往上游的请求注入官方客户端特征头，并对 ${guid}, ${nanoid}, ${timestamp}, ${model} 等占位符动态求值。
/// </summary>
public static partial class ClientEmulationEngine
{
    private static readonly char[] NanoIdAlphabet = "_-0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".ToCharArray();

    /// <summary>
    /// 匹配动态占位符变量，例如 ${guid}, ${nanoid:12}, ${timestamp}, ${random_hex:16}, ${model}
    /// </summary>
    [GeneratedRegex(@"\$\{([a-zA-Z0-9_\-:]+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    /// <summary>
    /// 解析并合并生成最终发往上游的请求头字典。
    /// 优先级：用户自定义 ExtraHeaders > 预设预定头。所有 Header 最终经动态变量求值。
    /// </summary>
    public static Dictionary<string, string> ResolveHeaders(
        string? emulationPreset,
        IReadOnlyDictionary<string, string>? extraHeaders,
        string? modelName = null,
        string? projectId = null,
        bool isAntigravity = false)
    {
        var normalizedPreset = ClientEmulationConstants.Normalize(emulationPreset);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. 载入预设客户端的基础特征头
        ApplyPresetHeaders(result, normalizedPreset, modelName, projectId, isAntigravity);

        // 2. 覆盖/叠加用户自定义的 ExtraHeaders
        if (extraHeaders != null && extraHeaders.Count > 0)
        {
            foreach (var (k, v) in extraHeaders)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    result[k] = v ?? string.Empty;
                }
            }
        }

        // 3. 对所有请求头的值执行动态占位符求值
        var evaluated = new Dictionary<string, string>(result.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in result)
        {
            evaluated[k] = EvaluatePlaceholders(v, modelName, projectId);
        }

        return evaluated;
    }

    /// <summary>
    /// 注入预设客户端特征头模板。
    /// </summary>
    private static void ApplyPresetHeaders(
        Dictionary<string, string> headers,
        string preset,
        string? modelName,
        string? projectId,
        bool isAntigravity)
    {
        switch (preset)
        {
            case ClientEmulationConstants.OpenCode:
                // OpenCode CLI 官方特征 (支持免密访问 OpenCode Zen 免费模型)
                headers["User-Agent"] = "opencode/1.15.0 ai-sdk/provider-utils/4.0.23 runtime/bun/1.3.13";
                headers["x-opencode-client"] = "cli";
                headers["x-opencode-project"] = "global";
                headers["x-opencode-request"] = "msg_${nanoid:12}";
                headers["x-opencode-session"] = "ses_${nanoid:12}";
                break;

            case ClientEmulationConstants.ClaudeCode:
                // Anthropic Claude Code 官方终端工具特征 (防封与环境仿真)
                headers["User-Agent"] = "claude-code/0.2.29 (external; x86_64-pc-windows-msvc)";
                headers["anthropic-client-name"] = "claude-code";
                headers["anthropic-client-version"] = "0.2.29";
                headers["anthropic-beta"] = "prompt-caching-2024-07-31,computer-use-2024-10-22";
                break;

            case ClientEmulationConstants.CodexCli:
                // VS Code GitHub Copilot & Codex 客户端特征
                headers["User-Agent"] = "GitHubCopilotChat/0.24.1 VSCode/1.96.2";
                headers["Editor-Version"] = "vscode/1.96.2";
                headers["Editor-Plugin-Version"] = "copilot-chat/0.24.1";
                headers["Openai-Organization"] = "github-copilot";
                headers["X-Request-Id"] = "${guid}";
                headers["Session-Id"] = "${guid}";
                break;

            case ClientEmulationConstants.Antigravity:
                // Google Antigravity 官方 CLI 特征（UA 与 GoogleAccountKinds 同源，保证与额度/模型拉取一致）
                headers["User-Agent"] = GoogleAccountKinds.AntigravityUserAgent;
                headers["requestId"] = "req-${guid:N}";
                headers["requestType"] = modelName is not null && modelName.Contains("image", StringComparison.OrdinalIgnoreCase)
                    ? "image_gen"
                    : "agent";
                headers["x-goog-api-client"] = "gl-node/20.18.0 antigravity-cli/1.10.4";
                break;

            case ClientEmulationConstants.GeminiCli:
                // Google Cloud Code / Gemini CLI 特征
                if (isAntigravity)
                {
                    headers["User-Agent"] = GoogleAccountKinds.AntigravityUserAgent;
                    headers["requestId"] = "req-${guid:N}";
                    headers["requestType"] = modelName is not null && modelName.Contains("image", StringComparison.OrdinalIgnoreCase)
                        ? "image_gen"
                        : "agent";
                }
                else
                {
                    headers["User-Agent"] = $"GeminiCLI/0.35.2/{modelName ?? string.Empty} (win32; x64; cloud-shell)";
                    if (!string.IsNullOrWhiteSpace(projectId))
                    {
                        headers["x-goog-user-project"] = projectId;
                    }
                }
                break;

            case ClientEmulationConstants.None:
            case ClientEmulationConstants.Custom:
            default:
                break;
        }
    }

    /// <summary>
    /// 对字符串中的占位符进行动态求值替换。
    /// </summary>
    public static string EvaluatePlaceholders(string? template, string? modelName = null, string? projectId = null)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("${", StringComparison.Ordinal))
        {
            return template ?? string.Empty;
        }

        return PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();

            if (string.Equals(key, "guid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "uuid", StringComparison.OrdinalIgnoreCase))
            {
                return Guid.NewGuid().ToString("D");
            }

            if (string.Equals(key, "guid:N", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "uuid:N", StringComparison.OrdinalIgnoreCase))
            {
                return Guid.NewGuid().ToString("N");
            }

            if (string.Equals(key, "timestamp", StringComparison.OrdinalIgnoreCase))
            {
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            }

            if (string.Equals(key, "timestamp_ms", StringComparison.OrdinalIgnoreCase))
            {
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            }

            if (string.Equals(key, "model", StringComparison.OrdinalIgnoreCase))
            {
                return modelName ?? string.Empty;
            }

            if (string.Equals(key, "project_id", StringComparison.OrdinalIgnoreCase))
            {
                return projectId ?? string.Empty;
            }

            if (key.StartsWith("nanoid", StringComparison.OrdinalIgnoreCase))
            {
                var length = 12;
                var parts = key.Split(':');
                if (parts.Length > 1 && int.TryParse(parts[1], out var parsedLen) && parsedLen > 0 && parsedLen <= 64)
                {
                    length = parsedLen;
                }
                return GenerateNanoId(length);
            }

            if (key.StartsWith("random_hex", StringComparison.OrdinalIgnoreCase))
            {
                var length = 16;
                var parts = key.Split(':');
                if (parts.Length > 1 && int.TryParse(parts[1], out var parsedLen) && parsedLen > 0 && parsedLen <= 128)
                {
                    length = parsedLen;
                }
                return GenerateRandomHex(length);
            }

            // 未知占位符保留原样
            return match.Value;
        });
    }

    /// <summary>
    /// 生成加密安全的 NanoID 随机字符串。
    /// </summary>
    public static string GenerateNanoId(int length = 12)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);

        var result = new char[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = NanoIdAlphabet[bytes[i] % NanoIdAlphabet.Length];
        }

        return new string(result);
    }

    /// <summary>
    /// 生成加密安全的随机十六进制字符串。
    /// </summary>
    public static string GenerateRandomHex(int length = 16)
    {
        var byteCount = (length + 1) / 2;
        var bytes = new byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return hex.Length > length ? hex[..length] : hex;
    }
}
