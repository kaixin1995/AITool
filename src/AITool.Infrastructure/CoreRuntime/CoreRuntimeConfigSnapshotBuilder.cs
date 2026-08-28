using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Application.Kimi;
using AITool.Domain.Codex;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// 从当前数据库中的核心主数据构建 Core 运行时配置快照。
/// 当前阶段先由 Admin 侧复用，后续可作为对外同步模型构造器。
/// </summary>
public static class CoreRuntimeConfigSnapshotBuilder
{
    private static readonly JsonSerializerOptions HashSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// 从当前主数据构建完整配置快照。
    /// </summary>
    /// <param name="compatibilityProfiles">兼容规则集（可选）。提供后会预解析规则并烤进对应 RouteRule/Model，供 Core 直接透传。</param>
    public static CoreRuntimeConfigSnapshot Build(
        IEnumerable<Site> sites,
        IEnumerable<ModelLibraryItem> models,
        IEnumerable<SiteModelMapping> siteModelMappings,
        IEnumerable<ProxyRouteEntry> routeEntries,
        IEnumerable<ProxyRouteRule> routeRules,
        IEnumerable<ProxyAccessKey> accessKeys,
        SystemRuntimeSettings runtimeSettings,
        long configVersion,
        DateTimeOffset generatedAt,
        IEnumerable<CompatibilityProfile>? compatibilityProfiles = null,
        IEnumerable<SiteKey>? siteKeys = null,
        IEnumerable<CodexAccount>? codexAccounts = null,
        IEnumerable<Domain.Google.GoogleAccount>? googleAccounts = null,
        IEnumerable<Domain.Kimi.KimiAccount>? kimiAccounts = null,
        IEnumerable<Domain.Sites.ProxyProfile>? proxyProfiles = null,
        IReadOnlyDictionary<string, string>? activeHeaderProfiles = null)
    {
        // 预解析兼容规则集：构建 Id→规则列表字典（仅启用的），供路由规则投影时查（避免 N+1）。
        var profileRules = compatibilityProfiles is null
            ? new Dictionary<Guid, IReadOnlyList<CompatibilityRule>>()
            : CompatibilityRuleParser.BuildProfileRuleMap(compatibilityProfiles);
        // 预建 model 查找字典：按 UpstreamModelName 关联（route.UpstreamModelName → model），左外联。
        var modelList = models as IList<ModelLibraryItem> ?? models.ToList();
        var modelByName = modelList
            .GroupBy(m => m.ModelName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = configVersion,
            GeneratedAt = generatedAt,
            Sites = sites
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeSite
                {
                    Id = x.Id,
                    Name = x.Name,
                    BaseUrl = x.BaseUrl,
                    EndpointPathMode = x.EndpointPathMode,
                    ApiKey = x.ApiKey,
                    ProtocolType = x.ProtocolType,
                    SupportsOpenAi = x.SupportsOpenAi,
                    SupportsAnthropic = x.SupportsAnthropic,
                    SupportsResponses = x.SupportsResponses,
                    IsEnabled = x.IsEnabled,
                    ManagedSource = x.ManagedSource,
                    ClientEmulation = x.ClientEmulation,
                    EgressProxyUrl = x.EgressProxyUrl,
                    ExtraHeadersJson = x.ExtraHeadersJson
                })
                .ToList(),
            // 站点密钥（多 Key）：仅下发启用的密钥，Core 据此把路由按 Key 展开为多条候选。
            SiteKeys = (siteKeys ?? [])
                .Where(x => x.IsEnabled)
                .OrderBy(x => x.SiteId)
                .ThenBy(x => x.Priority)
                .ThenBy(x => x.CreatedAt)
                .ThenBy(x => x.Id)
                .Select(x => new CoreRuntimeSiteKey
                {
                    Id = x.Id,
                    SiteId = x.SiteId,
                    KeyValue = x.KeyValue,
                    Remark = x.Remark,
                    Priority = x.Priority,
                    IsEnabled = x.IsEnabled,
                    CreatedAt = x.CreatedAt
                })
                .ToList(),
            // 托管 OAuth 账号凭证（Codex/Google/Kimi）：Core 401 即刷的 refresh token 与 Google 项目标识来源。
            AccountCredentials = BuildAccountCredentials(codexAccounts, googleAccounts, kimiAccounts),
            // 客户端特征模拟档案：请求头模板（JSON 文件存储）与出口代理池（表存储）。
            HeaderProfiles = (activeHeaderProfiles ?? new Dictionary<string, string>(StringComparer.Ordinal))
                .Select(kv => new CoreRuntimeHeaderProfile
                {
                    Key = kv.Key,
                    HeadersJson = kv.Value,
                    IsEnabled = true
                })
                .ToList(),
            ProxyProfiles = (proxyProfiles ?? [])
                .Where(x => x.IsEnabled)
                .Select(x => new CoreRuntimeProxyProfile
                {
                    Key = x.Key,
                    ProxyUrl = x.ProxyUrl,
                    IsEnabled = x.IsEnabled
                })
                .ToList(),
            Models = modelList
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeModel
                {
                    Id = x.Id,
                    ModelName = x.ModelName,
                    DisplayName = x.DisplayName,
                    IsEnabled = x.IsEnabled,
                    OverrideReasoningEffort = x.OverrideReasoningEffort ?? string.Empty,
                    CompatibilityProfileId = x.CompatibilityProfileId,
                    ClientEmulation = x.ClientEmulation,
                    ExtraHeadersJson = x.ExtraHeadersJson
                })
                .ToList(),
            SiteModelMappings = siteModelMappings
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeSiteModelMapping
                {
                    Id = x.Id,
                    SiteId = x.SiteId,
                    ModelLibraryItemId = x.ModelLibraryItemId,
                    RemoteModelName = x.RemoteModelName,
                    LastStatus = x.LastStatus,
                    IsEnabled = x.IsEnabled,
                    MaxConcurrency = x.MaxConcurrency,
                    ClientEmulation = x.ClientEmulation,
                    ExtraHeadersJson = x.ExtraHeadersJson,
                    EgressProxyUrl = x.EgressProxyUrl
                })
                .ToList(),
            RouteEntries = routeEntries
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeRouteEntry
                {
                    Id = x.Id,
                    EntryName = x.EntryName
                })
                .ToList(),
            RouteRules = routeRules
                .OrderBy(x => x.ExternalModelName, StringComparer.Ordinal)
                .ThenBy(x => x.ModelPriority)
                .ThenBy(x => x.InstancePriority)
                .ThenBy(x => x.Priority)
                .ThenBy(x => x.Id)
                .Select(x =>
                {
                    // 按上游模型名关联 model，左外联：model 不存在（如规则指向已删除模型）时按空规则处理。
                    modelByName.TryGetValue(x.UpstreamModelName ?? string.Empty, out var model);
                    return new CoreRuntimeRouteRule
                    {
                        Id = x.Id,
                        ExternalModelName = x.ExternalModelName,
                        UpstreamModelName = x.UpstreamModelName,
                        SiteId = x.SiteId,
                        SiteModelName = x.SiteModelName,
                        Priority = x.Priority,
                        ModelPriority = x.ModelPriority,
                        InstancePriority = x.InstancePriority,
                        IsEnabled = x.IsEnabled,
                        AvailabilityMode = x.AvailabilityMode,
                        TimeRangesJson = x.TimeRangesJson,
                        // 派生字段：由 Admin 端预解析，Core 直接透传，避免 Core 重复实现解析+关联逻辑。
                        OverrideReasoningEffort = model?.OverrideReasoningEffort ?? string.Empty,
                        CompatibilityRules = CompatibilityRuleParser.GetRulesForModel(model?.CompatibilityProfileId, profileRules)
                    };
                })
                .ToList(),
            AccessKeys = accessKeys
                .OrderBy(x => x.Id)
                .Select(x => new CoreRuntimeAccessKey
                {
                    Id = x.Id,
                    KeyName = x.KeyName,
                    PlainKey = x.PlainKey,
                    AccessKeyHash = x.AccessKeyHash,
                    MaskedValue = x.MaskedValue,
                    IsEnabled = x.IsEnabled,
                    AllowedRouteNames = x.AllowedRouteNames
                })
                .ToList(),
            RuntimeSettings = new CoreRuntimeSettings
            {
                ProxyRequestTimeoutSeconds = runtimeSettings.ProxyRequestTimeoutSeconds,
                ProxyRetryCount = runtimeSettings.ProxyRetryCount,
                CircuitBreakerFailureThreshold = runtimeSettings.CircuitBreakerFailureThreshold,
                CircuitBreakerRecoveryMinutes = runtimeSettings.CircuitBreakerRecoveryMinutes,
                ConcurrencyMode = runtimeSettings.ConcurrencyMode,
                ConcurrencyQueueTimeoutSeconds = runtimeSettings.ConcurrencyQueueTimeoutSeconds,
                ConversationLogEnabled = runtimeSettings.ConversationLogEnabled,
                DeveloperFeaturesEnabled = runtimeSettings.DeveloperFeaturesEnabled
            }
        };

        snapshot.ConfigHash = ComputeHash(snapshot);
        return snapshot;
    }

    /// <summary>
    /// 计算配置快照哈希，供后续判断是否有真实变化。
    /// </summary>
    public static string ComputeHash(CoreRuntimeConfigSnapshot snapshot)
    {
        var payload = new
        {
            snapshot.Sites,
            snapshot.SiteKeys,
            snapshot.Models,
            snapshot.SiteModelMappings,
            snapshot.RouteEntries,
            snapshot.RouteRules,
            snapshot.AccessKeys,
            snapshot.RuntimeSettings
        };
        var json = JsonSerializer.Serialize(payload, HashSerializerOptions);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return "sha256:" + Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// 投影托管 OAuth 账号凭证（Codex/Google/Kimi）到 Core 快照。
    /// 仅包含启用的账号；禁用账号的站点已同步禁用，Core 无需其凭证。
    /// </summary>
    private static List<CoreRuntimeAccountCredential> BuildAccountCredentials(
        IEnumerable<CodexAccount>? codexAccounts,
        IEnumerable<Domain.Google.GoogleAccount>? googleAccounts,
        IEnumerable<Domain.Kimi.KimiAccount>? kimiAccounts)
    {
        var credentials = new List<CoreRuntimeAccountCredential>();
        foreach (var account in codexAccounts ?? [])
        {
            if (!account.IsEnabled) continue;
            credentials.Add(new CoreRuntimeAccountCredential
            {
                Provider = "Codex",
                AccountId = account.Id,
                LinkedSiteId = account.LinkedSiteId,
                RefreshToken = account.RefreshToken ?? string.Empty,
                IsEnabled = true
            });
        }
        foreach (var account in googleAccounts ?? [])
        {
            if (!account.IsEnabled) continue;
            credentials.Add(new CoreRuntimeAccountCredential
            {
                Provider = "Google",
                AccountId = account.Id,
                LinkedSiteId = account.LinkedSiteId,
                RefreshToken = account.RefreshToken ?? string.Empty,
                ProjectId = account.ProjectId,
                AccountKind = account.AccountKind,
                IsEnabled = true
            });
        }
        foreach (var account in kimiAccounts ?? [])
        {
            if (!account.IsEnabled) continue;
            credentials.Add(new CoreRuntimeAccountCredential
            {
                Provider = KimiConstants.ManagedSource,
                AccountId = account.Id,
                LinkedSiteId = account.LinkedSiteId,
                RefreshToken = account.RefreshToken ?? string.Empty,
                DeviceId = account.DeviceId,
                IsEnabled = true
            });
        }
        return credentials;
    }
}
