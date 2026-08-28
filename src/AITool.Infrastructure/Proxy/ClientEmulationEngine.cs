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
                // OpenCode 真实官方客户端特征 (Node 24 环境与动态 Session Affinity)
                headers["User-Agent"] = "opencode/1.18.18 ai-sdk/provider-utils/4.0.23 runtime/node.js/24";
                headers["x-session-affinity"] = "ses_${nanoid:20}";
                headers["x-session-id"] = "ses_${nanoid:20}";
                headers["x-opencode-client"] = "cli";
                headers["x-opencode-project"] = "global";
                headers["x-opencode-request"] = "msg_${nanoid:12}";
                break;

            case ClientEmulationConstants.ClaudeCode:
                // Anthropic Claude Code 官方终端工具特征 (全套 Stainless 与官方 Beta Header)
                headers["User-Agent"] = "claude-cli/2.1.241 (external, claude-vscode, agent-sdk/0.3.241)";
                headers["X-Claude-Code-Session-Id"] = "${guid}";
                headers["X-Stainless-Arch"] = "x64";
                headers["X-Stainless-Lang"] = "js";
                headers["X-Stainless-OS"] = "Windows";
                headers["X-Stainless-Package-Version"] = "0.112.1";
                headers["X-Stainless-Retry-Count"] = "0";
                headers["X-Stainless-Runtime"] = "node";
                headers["X-Stainless-Runtime-Version"] = "v26.3.0";
                headers["X-Stainless-Timeout"] = "600";
                headers["anthropic-beta"] = "claude-code-20250219,context-1m-2025-08-07,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,advanced-tool-use-2025-11-20,effort-2025-11-24";
                headers["anthropic-dangerous-direct-browser-access"] = "true";
                headers["anthropic-version"] = "2023-06-01";
                headers["x-app"] = "cli";
                break;

            case ClientEmulationConstants.CodexCli:
                // OpenAI Codex Desktop 官方客户端特征 (默认 Codex 方案)
                headers["User-Agent"] = "Codex Desktop/0.149.0-alpha.4.3 (Windows 10.0.19045; x86_64) unknown (Codex Desktop; 26.818.61809)";
                headers["Originator"] = "Codex Desktop";
                headers["Session-Id"] = "${guid}";
                headers["Thread-Id"] = "${guid}";
                headers["X-Client-Request-Id"] = "${guid}";
                headers["X-Codex-Beta-Features"] = "remote_compaction_v2";
                headers["X-Codex-Turn-Metadata"] = "{\"installation_id\":\"${guid}\",\"session_id\":\"${guid}\",\"thread_id\":\"${guid}\",\"agent_name\":\"/root\",\"turn_id\":\"${guid}\",\"window_id\":\"${guid}:0\",\"request_kind\":\"turn\",\"root_turn_id\":\"${guid}\",\"thread_source\":\"user\",\"sandbox\":\"none\",\"sandbox_mode\":\"danger-full-access\",\"auto_review_enabled\":false,\"node_repl_auto_review_required\":false,\"node_repl_disabled\":false,\"turn_started_at_unix_ms\":${timestamp_ms},\"workspace_kind\":\"project\"}";
                headers["X-Codex-Window-Id"] = "${guid}:0";
                headers["X-Oai-Attestation"] = "{\"v\":1,\"s\":0,\"t\":\"v1.o2plcnJvcl9jb2RlAWlidW5kbGVfaWRwY29tLm9wZW5haS5jb2RleGFmWE6nAAEBgWV6aC1DTgJlemgtQ04DbUFzaWEvU2hhbmdoYWkEGQu4BQEGeCRmYTYxN2M0Yi0wMzhiLTQwYmMtOTk0OC0xNTE5ZWY2ODQ0NmE\"}";
                break;

            case ClientEmulationConstants.CodexVsCode:
                // VS Code Codex 插件官方特征
                headers["User-Agent"] = "codex_vscode/0.149.0-alpha.4.1 (Windows 10.0.19045; x86_64) unknown (VS Code; 26.818.41705)";
                headers["Originator"] = "codex_vscode";
                headers["Session-Id"] = "${guid}";
                headers["Thread-Id"] = "${guid}";
                headers["X-Client-Request-Id"] = "${guid}";
                headers["X-Codex-Beta-Features"] = "remote_compaction_v2";
                headers["X-Codex-Turn-Metadata"] = "{\"installation_id\":\"${guid}\",\"session_id\":\"${guid}\",\"thread_id\":\"${guid}\",\"agent_name\":\"/root\",\"turn_id\":\"${guid}\",\"window_id\":\"${guid}:0\",\"request_kind\":\"turn\",\"thread_source\":\"system\",\"sandbox\":\"windows_elevated\",\"sandbox_mode\":\"read-only\",\"auto_review_enabled\":false,\"node_repl_auto_review_required\":false,\"node_repl_disabled\":false,\"turn_started_at_unix_ms\":${timestamp_ms}}";
                headers["X-Codex-Window-Id"] = "${guid}:0";
                break;

            case ClientEmulationConstants.ZCode:
                // ZCode / GLM 客户端官方特征
                headers["User-Agent"] = "ZCode/3.9.1 ai-sdk/provider-utils/4.0.27 runtime/node.js/24";
                headers["http-referer"] = "https://zcode.z.ai";
                headers["x-client-language"] = "zh-CN";
                headers["x-client-timezone"] = "Asia/Shanghai";
                headers["x-os-category"] = "windows";
                headers["x-os-version"] = "10.0.17763";
                headers["x-platform"] = "win32-x64";
                headers["x-query-id"] = "${guid}";
                headers["x-release-channel"] = "production";
                headers["x-request-id"] = "${guid}";
                headers["x-session-id"] = "${guid}";
                headers["x-title"] = "Z Code@electron";
                headers["x-zcode-agent"] = "glm";
                headers["x-zcode-app-version"] = "3.9.1";
                headers["x-zcode-session-type"] = "main";
                headers["x-zcode-trace-id"] = "${guid}";
                break;

            case ClientEmulationConstants.Antigravity:
                // Google Antigravity 官方 CLI 真实特征（对齐 agy 1.1.20 抓包，HTTP 请求头仅设置官方 User-Agent）
                headers["User-Agent"] = GoogleAccountKinds.AntigravityUserAgent;
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
                return !string.IsNullOrWhiteSpace(modelName) ? modelName : $"model-{GenerateNanoId(8)}";
            }

            if (string.Equals(key, "project_id", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "projectId", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(projectId) ? projectId : $"proj-{GenerateNanoId(10)}";
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

            if (key.StartsWith("random_hex", StringComparison.OrdinalIgnoreCase) || key.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
            {
                var length = 16;
                var parts = key.Split(':');
                if (parts.Length > 1 && int.TryParse(parts[1], out var parsedLen) && parsedLen > 0 && parsedLen <= 128)
                {
                    length = parsedLen;
                }
                return GenerateRandomHex(length);
            }

            // 智能随机兜底：未预置的自定义占位符（如 ${session_id}, ${account_id}, ${user_id} 等）每次自动生成全新随机值，杜绝固定字符串特征
            var lowerKey = key.ToLowerInvariant();
            if (lowerKey.Contains("guid:n") || lowerKey.Contains("uuid:n"))
            {
                return Guid.NewGuid().ToString("N");
            }
            if (lowerKey.Contains("guid") || lowerKey.Contains("uuid") || lowerKey.EndsWith("id"))
            {
                return Guid.NewGuid().ToString("D");
            }
            if (lowerKey.Contains("time") || lowerKey.Contains("date"))
            {
                return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            }
            if (lowerKey.Contains("hex"))
            {
                return GenerateRandomHex(16);
            }

            return GenerateNanoId(16);
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
