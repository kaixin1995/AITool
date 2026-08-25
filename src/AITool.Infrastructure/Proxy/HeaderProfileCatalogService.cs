using System.Collections.Concurrent;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Sites;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 请求头模板方案本地文件目录服务（直接读写 client-header-profiles.json，脱离数据库存储）。
/// </summary>
public sealed class HeaderProfileCatalogService : IHeaderProfileCatalogService
{
    private const string CatalogFileName = "client-header-profiles.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _catalogPath;
    private readonly string _templateCatalogPath;
    private readonly ILogger<HeaderProfileCatalogService>? _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private List<HeaderProfile>? _memoryCache;

    public HeaderProfileCatalogService(IHostEnvironment environment, ILogger<HeaderProfileCatalogService>? logger = null)
    {
        _catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CatalogFileName);
        _templateCatalogPath = Path.Combine(environment.ContentRootPath, CatalogFileName);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HeaderProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await LoadOrInitializeAsync(cancellationToken);
        return profiles.OrderByDescending(x => x.IsBuiltIn).ThenBy(x => x.SortOrder).ThenBy(x => x.Key).ToList();
    }

    /// <inheritdoc />
    public async Task<HeaderProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profiles = await LoadOrInitializeAsync(cancellationToken);
        return profiles.FirstOrDefault(x => x.Id == id);
    }

    /// <inheritdoc />
    public async Task<HeaderProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var profiles = await LoadOrInitializeAsync(cancellationToken);
        return profiles.FirstOrDefault(x => string.Equals(x.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetActiveProfilesDictionaryAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await LoadOrInitializeAsync(cancellationToken);
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles.Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.HeadersJson)))
        {
            dict[p.Key] = p.HeadersJson!;
        }
        return dict;
    }

    /// <inheritdoc />
    public async Task<HeaderProfile> CreateAsync(HeaderProfile profile, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadInternalAsync(cancellationToken);
            if (profiles.Any(x => string.Equals(x.Key, profile.Key.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"方案标识 Key '{profile.Key}' 已存在");
            }

            profile.Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id;
            profile.CreatedAt = DateTimeOffset.UtcNow;
            profile.UpdatedAt = null;
            profiles.Add(profile);

            await SaveInternalAsync(profiles, cancellationToken);
            _memoryCache = profiles;
            return profile;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<HeaderProfile?> UpdateAsync(Guid id, Action<HeaderProfile> updateAction, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadInternalAsync(cancellationToken);
            var target = profiles.FirstOrDefault(x => x.Id == id);
            if (target == null)
            {
                return null;
            }

            updateAction(target);
            target.UpdatedAt = DateTimeOffset.UtcNow;

            await SaveInternalAsync(profiles, cancellationToken);
            _memoryCache = profiles;
            return target;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadInternalAsync(cancellationToken);
            var target = profiles.FirstOrDefault(x => x.Id == id);
            if (target == null)
            {
                return false;
            }

            if (target.IsBuiltIn)
            {
                throw new InvalidOperationException("系统内置预设方案不支持删除，如不需要可将其停用");
            }

            profiles.Remove(target);
            await SaveInternalAsync(profiles, cancellationToken);
            _memoryCache = profiles;
            return true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HeaderProfile>> ResetBuiltInsAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            var profiles = await LoadInternalAsync(cancellationToken);
            var defaults = GetDefaultBuiltInProfiles();

            // 保留所有用户自定义的方案
            var customs = profiles.Where(x => !x.IsBuiltIn).ToList();

            // 用默认的内置方案替换
            var merged = defaults.Concat(customs).ToList();

            await SaveInternalAsync(merged, cancellationToken);
            _memoryCache = merged;
            return merged.OrderByDescending(x => x.IsBuiltIn).ThenBy(x => x.SortOrder).ThenBy(x => x.Key).ToList();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<List<HeaderProfile>> LoadOrInitializeAsync(CancellationToken cancellationToken)
    {
        if (_memoryCache != null)
        {
            return _memoryCache;
        }

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (_memoryCache != null)
            {
                return _memoryCache;
            }

            var profiles = await LoadInternalAsync(cancellationToken);
            _memoryCache = profiles;
            return profiles;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<List<HeaderProfile>> LoadInternalAsync(CancellationToken cancellationToken)
    {
        var targetFile = File.Exists(_catalogPath) ? _catalogPath : (File.Exists(_templateCatalogPath) ? _templateCatalogPath : null);

        if (targetFile != null)
        {
            try
            {
                var json = await File.ReadAllTextAsync(targetFile, cancellationToken);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var list = JsonSerializer.Deserialize<List<HeaderProfile>>(json, JsonOptions);
                    if (list != null && list.Count > 0)
                    {
                        // 确保 7 种内置预设均存在（若新版本新增了预设如 CodexVsCode/ZCode，自动补齐）
                        EnsureBuiltInDefaults(list);
                        return list;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "读取请求头配置文件 {Path} 异常，使用默认内置预设", targetFile);
            }
        }

        // 初始化默认配置
        var defaults = GetDefaultBuiltInProfiles();
        await SaveInternalAsync(defaults, cancellationToken);
        return defaults;
    }

    private async Task SaveInternalAsync(List<HeaderProfile> profiles, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(_catalogPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(profiles, JsonOptions);
            await File.WriteAllTextAsync(_catalogPath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入请求头配置文件 {Path} 失败", _catalogPath);
            throw;
        }
    }

    private static void EnsureBuiltInDefaults(List<HeaderProfile> list)
    {
        var defaults = GetDefaultBuiltInProfiles();
        foreach (var def in defaults)
        {
            var existing = list.FirstOrDefault(x => string.Equals(x.Key, def.Key, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                list.Add(def);
            }
            else if (existing.IsBuiltIn)
            {
                // 同步内置预设的名称、排序与描述
                existing.Name = def.Name;
                existing.Description = def.Description;
                existing.SortOrder = def.SortOrder;
                // 若内置请求头发生升级且用户未修改过，同步最新请求头
                if (string.IsNullOrWhiteSpace(existing.HeadersJson) || existing.HeadersJson.Contains("codex_cli_rs") || existing.HeadersJson.Contains("opencode/1.15.0"))
                {
                    existing.HeadersJson = def.HeadersJson;
                }
            }
        }
    }

    public static List<HeaderProfile> GetDefaultBuiltInProfiles()
    {
        return
        [
            new HeaderProfile
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                Key = ClientEmulationConstants.OpenCode,
                Name = "OpenCode CLI 终端",
                Description = "OpenCode CLI 终端工具官方特征指纹",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "opencode/1.18.18 ai-sdk/provider-utils/4.0.23 runtime/node.js/24",
                    ["x-session-affinity"] = "ses_${nanoid:20}",
                    ["x-session-id"] = "ses_${nanoid:20}",
                    ["x-opencode-client"] = "cli",
                    ["x-opencode-project"] = "global",
                    ["x-opencode-request"] = "msg_${nanoid:12}",
                }, JsonOptions),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 1,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new HeaderProfile
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                Key = ClientEmulationConstants.ClaudeCode,
                Name = "Claude Code 官方命令行",
                Description = "Anthropic Claude Code CLI 官方特征指纹",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "claude-cli/2.1.241 (external, claude-vscode, agent-sdk/0.3.241)",
                    ["X-Claude-Code-Session-Id"] = "${guid}",
                    ["X-Stainless-Arch"] = "x64",
                    ["X-Stainless-Lang"] = "js",
                    ["X-Stainless-OS"] = "Windows",
                    ["X-Stainless-Package-Version"] = "0.112.1",
                    ["X-Stainless-Retry-Count"] = "0",
                    ["X-Stainless-Runtime"] = "node",
                    ["X-Stainless-Runtime-Version"] = "v26.3.0",
                    ["X-Stainless-Timeout"] = "600",
                    ["anthropic-beta"] = "claude-code-20250219,context-1m-2025-08-07,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13,context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,advanced-tool-use-2025-11-20,effort-2025-11-24",
                    ["anthropic-dangerous-direct-browser-access"] = "true",
                    ["anthropic-version"] = "2023-06-01",
                    ["x-app"] = "cli",
                }, JsonOptions),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 2,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new HeaderProfile
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000003"),
                Key = ClientEmulationConstants.CodexCli,
                Name = "Codex Desktop 官方客户端 (默认)",
                Description = "OpenAI Codex Desktop 客户端真实特征指纹",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "Codex Desktop/0.149.0-alpha.4.3 (Windows 10.0.19045; x86_64) unknown (Codex Desktop; 26.818.61809)",
                    ["Originator"] = "Codex Desktop",
                    ["Session-Id"] = "${guid}",
                    ["Thread-Id"] = "${guid}",
                    ["X-Client-Request-Id"] = "${guid}",
                    ["X-Codex-Beta-Features"] = "remote_compaction_v2",
                    ["X-Codex-Turn-Metadata"] = "{\"installation_id\":\"${guid}\",\"session_id\":\"${guid}\",\"thread_id\":\"${guid}\",\"agent_name\":\"/root\",\"turn_id\":\"${guid}\",\"window_id\":\"${guid}:0\",\"request_kind\":\"turn\",\"root_turn_id\":\"${guid}\",\"thread_source\":\"user\",\"sandbox\":\"none\",\"sandbox_mode\":\"danger-full-access\",\"auto_review_enabled\":false,\"node_repl_auto_review_required\":false,\"node_repl_disabled\":false,\"turn_started_at_unix_ms\":${timestamp_ms},\"workspace_kind\":\"project\"}",
                    ["X-Codex-Window-Id"] = "${guid}:0",
                    ["X-Oai-Attestation"] = "{\"v\":1,\"s\":0,\"t\":\"v1.o2plcnJvcl9jb2RlAWlidW5kbGVfaWRwY29tLm9wZW5haS5jb2RleGFmWE6nAAEBgWV6aC1DTgJlemgtQ04DbUFzaWEvU2hhbmdoYWkEGQu4BQEGeCRmYTYxN2M0Yi0wMzhiLTQwYmMtOTk0OC0xNTE5ZWY2ODQ0NmE\"}"
                }, JsonOptions),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 3,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new HeaderProfile
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000004"),
                Key = ClientEmulationConstants.CodexVsCode,
                Name = "Codex VS Code 插件",
                Description = "OpenAI Codex in VS Code 插件真实特征指纹",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "codex_vscode/0.149.0-alpha.4.1 (Windows 10.0.19045; x86_64) unknown (VS Code; 26.818.41705)",
                    ["Originator"] = "codex_vscode",
                    ["Session-Id"] = "${guid}",
                    ["Thread-Id"] = "${guid}",
                    ["X-Client-Request-Id"] = "${guid}",
                    ["X-Codex-Beta-Features"] = "remote_compaction_v2",
                    ["X-Codex-Turn-Metadata"] = "{\"installation_id\":\"${guid}\",\"session_id\":\"${guid}\",\"thread_id\":\"${guid}\",\"agent_name\":\"/root\",\"turn_id\":\"${guid}\",\"window_id\":\"${guid}:0\",\"request_kind\":\"turn\",\"thread_source\":\"system\",\"sandbox\":\"windows_elevated\",\"sandbox_mode\":\"read-only\",\"auto_review_enabled\":false,\"node_repl_auto_review_required\":false,\"node_repl_disabled\":false,\"turn_started_at_unix_ms\":${timestamp_ms}}",
                    ["X-Codex-Window-Id"] = "${guid}:0"
                }, JsonOptions),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 4,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new HeaderProfile
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000005"),
                Key = ClientEmulationConstants.ZCode,
                Name = "ZCode / GLM 客户端",
                Description = "智谱 GLM / ZCode Electron 客户端真实特征指纹",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "ZCode/3.9.1 ai-sdk/provider-utils/4.0.27 runtime/node.js/24",
                    ["http-referer"] = "https://zcode.z.ai",
                    ["x-client-language"] = "zh-CN",
                    ["x-client-timezone"] = "Asia/Shanghai",
                    ["x-os-category"] = "windows",
                    ["x-os-version"] = "10.0.17763",
                    ["x-platform"] = "win32-x64",
                    ["x-query-id"] = "${guid}",
                    ["x-release-channel"] = "production",
                    ["x-request-id"] = "${guid}",
                    ["x-session-id"] = "${guid}",
                    ["x-title"] = "Z Code@electron",
                    ["x-zcode-agent"] = "glm",
                    ["x-zcode-app-version"] = "3.9.1",
                    ["x-zcode-session-type"] = "main",
                    ["x-zcode-trace-id"] = "${guid}"
                }, JsonOptions),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 5,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new HeaderProfile
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000006"),
                Key = ClientEmulationConstants.Antigravity,
                Name = "Google Antigravity CLI",
                Description = "Google Cloud Antigravity CLI 官方特征指纹",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "antigravity/1.10.4 linux/x86_64",
                    ["x-goog-api-client"] = "gl-node/20.18.0 antigravity-cli/1.10.4",
                    ["requestId"] = "req-${guid:N}",
                    ["requestType"] = "agent"
                }, JsonOptions),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 6,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new HeaderProfile
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000007"),
                Key = ClientEmulationConstants.GeminiCli,
                Name = "Google Gemini CLI",
                Description = "Google Gemini CLI 官方特征指纹",
                HeadersJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["User-Agent"] = "GeminiCLI/0.35.2/${model} (win32; x64; cloud-shell)"
                }, JsonOptions),
                IsBuiltIn = true,
                IsEnabled = true,
                SortOrder = 7,
                CreatedAt = DateTimeOffset.UtcNow
            }
        ];
    }
}
