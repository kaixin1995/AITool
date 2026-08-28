using AITool.Infrastructure.Proxy;
using System.Runtime.InteropServices;
using System.Text.Json;
using AITool.Application.Common;
using SqlSugar;
using AITool.Application.Kimi;
using AITool.Domain.Kimi;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;

namespace AITool.Admin.Services;

/// <summary>
/// Kimi 账号供给工厂：把 OAuth token / 导入凭证转换为「隐藏 Site + KimiAccount + 模型映射」，
/// 并支持级联删除与编辑（对齐 Google / Codex 的隐藏 Site 复用方案）。
/// </summary>
public sealed class KimiAccountProvisioner
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>split 双宿主：变更推送 Core（惰性解析，避免 配额服务→失效服务→设置服务→配额服务 的 DI 环）。</summary>
    private readonly IServiceScopeFactory _corePushScopeFactory;
    private readonly IKimiModelFetcher _modelFetcher;
    private readonly KimiCredentialRefreshService _credentialRefreshService;
    private readonly SiteCascadeDeleter _cascadeDeleter;
    private readonly ILogger<KimiAccountProvisioner> _logger;

    public KimiAccountProvisioner(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IServiceScopeFactory corePushScopeFactory,
        IKimiModelFetcher modelFetcher,
        KimiCredentialRefreshService credentialRefreshService,
        SiteCascadeDeleter cascadeDeleter,
        ILogger<KimiAccountProvisioner> logger)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _corePushScopeFactory = corePushScopeFactory;
        _modelFetcher = modelFetcher;
        _credentialRefreshService = credentialRefreshService;
        _cascadeDeleter = cascadeDeleter;
        _logger = logger;
    }

    /// <summary>
    /// 用 token 创建或更新 Kimi 账号（含隐藏 Site + 模型映射）。
    /// </summary>
    public async Task<KimiAccount> ProvisionFromTokensAsync(KimiProvisionInput input, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        var existing = await FindExistingAsync(input.Email, input.UserId, input.DeviceId, ct);

        KimiAccount account;
        Site site;

        var deviceId = NormalizeDeviceId(input.DeviceId) ?? Guid.NewGuid().ToString("N");
        // 静态指纹（KimiCLI UA + x-msh-platform/version + x-stainless-*）由内置客户端仿真预设 Kimi 提供，
        // 这里只写每账号稳定的设备标识与部署机信息（自定义头优先级高于预设），格式对齐官方 CLI 真实抓包。
        var extraHeaders = new Dictionary<string, string>
        {
            ["X-Msh-Device-Id"] = deviceId,
            ["X-Msh-Device-Name"] = Environment.MachineName,
            ["X-Msh-Device-Model"] = GetDeviceModel(),
            ["X-Msh-Os-Version"] = Environment.OSVersion.Version.ToString()
        };
        var extraHeadersJson = JsonSerializer.Serialize(extraHeaders, JsonSerializerPresets.Compact);

        if (existing != null)
        {
            account = existing;
            site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId)
                ?? throw new InvalidOperationException($"Linked site {account.LinkedSiteId} not found for Kimi account {account.Id}");

            account.AccessToken = input.AccessToken;
            if (!string.IsNullOrWhiteSpace(input.RefreshToken)) account.RefreshToken = input.RefreshToken;
            account.TokenType = input.TokenType ?? "bearer";
            account.Scope = input.Scope;
            account.TokenExpiresAt = input.TokenExpiresAt;
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(input.Email)) account.Email = input.Email;
            if (!string.IsNullOrEmpty(input.UserId)) account.UserId = input.UserId;
            if (!string.IsNullOrEmpty(input.DeviceId)) account.DeviceId = input.DeviceId;
            if (!string.IsNullOrWhiteSpace(input.DisplayName)) account.DisplayName = input.DisplayName;
            account.IsEnabled = true;
            account.ManuallyDisabled = false;
            account.IsDeleted = false;
            account.UpdatedAt = DateTimeOffset.UtcNow;

            site.ApiKey = input.AccessToken;
            if (!string.IsNullOrWhiteSpace(input.DisplayName)) site.Name = input.DisplayName;
            site.ExtraHeadersJson = extraHeadersJson;
            site.ClientEmulation = ClientEmulationConstants.Kimi;
            site.IsEnabled = true;

            await client.Updateable(account).ExecuteCommandAsync(ct);
            await client.Updateable(site).ExecuteCommandAsync(ct);
        }
        else
        {
            site = new Site
            {
                Name = string.IsNullOrWhiteSpace(input.DisplayName)
                    ? (input.Email ?? "Kimi 账号")
                    : input.DisplayName,
                BaseUrl = KimiConstants.ApiBaseUrl,
                EndpointPathMode = "standard-root",
                ApiKey = input.AccessToken,
                // Kimi 上游仅提供 OpenAI 兼容的 /v1/chat/completions（见 CLIProxyAPI KimiExecutor）。
                // 必须声明 SupportsOpenAi=true：若三个 Supports* 均为 false，ProxyProtocolResolver 会把站点
                // 推导为 Responses 协议并请求上游不存在的 /v1/responses，导致转发必然失败并被熔断。
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                SupportsResponses = false,
                ProtocolType = "OpenAI",
                ManagedSource = KimiConstants.ManagedSource,
                ClientEmulation = ClientEmulationConstants.Kimi,
                ExtraHeadersJson = extraHeadersJson,
                IsEnabled = true,
            };
            await client.Insertable(site).ExecuteCommandAsync(ct);

            account = new KimiAccount
            {
                DisplayName = site.Name,
                Email = input.Email,
                UserId = input.UserId,
                DeviceId = deviceId,
                AccessToken = input.AccessToken,
                RefreshToken = input.RefreshToken,
                TokenType = input.TokenType ?? "bearer",
                Scope = input.Scope,
                TokenExpiresAt = input.TokenExpiresAt,
                LastRefreshAt = DateTimeOffset.UtcNow,
                LinkedSiteId = site.Id,
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await client.Insertable(account).ExecuteCommandAsync(ct);
        }

        // —— 模型映射 ——
        try
        {
            var models = await _modelFetcher.FetchAsync(input.AccessToken, account.DeviceId, ct);
            if (models.Count > 0)
            {
                await UpsertModelMappingsCoreAsync(site.Id, models, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kimi model fetching failed during provision, falling back to default models");
            await UpsertModelMappingsCoreAsync(site.Id, KimiConstants.DefaultModels, ct);
        }

        _metadataCache.InvalidateRouteTargets();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        _metadataCache.InvalidateModelMetadata();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        _metadataCache.InvalidateKimiAccounts();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        return account;
    }

    /// <summary>
    /// 启用/禁用 Kimi 账号及其关联的隐藏 Site。手动禁用会记录 ManuallyDisabled，
    /// 额度巡检不会自动恢复手动禁用的账号。
    /// </summary>
    public async Task ToggleAsync(Guid accountId, bool isEnabled, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        var account = await client.Queryable<KimiAccount>().InSingleAsync(accountId);
        if (account == null) return;

        account.IsEnabled = isEnabled;
        account.ManuallyDisabled = !isEnabled;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await client.Updateable(account).UpdateColumns(a => new { a.IsEnabled, a.ManuallyDisabled, a.UpdatedAt }).ExecuteCommandAsync(ct);

        var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
        if (site != null)
        {
            site.IsEnabled = isEnabled;
            await client.Updateable(site).UpdateColumns(s => new { s.IsEnabled }).ExecuteCommandAsync(ct);
        }

        _metadataCache.InvalidateRouteTargets();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        _metadataCache.InvalidateKimiAccounts();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
    }

    /// <summary>
    /// 更新展示名与（可选）refresh_token。
    /// </summary>
    public async Task<KimiAccount> UpdateAsync(Guid accountId, string displayName, string? refreshToken, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        var account = await client.Queryable<KimiAccount>().InSingleAsync(accountId)
            ?? throw new KeyNotFoundException("账号不存在");

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("账号展示名不能为空", nameof(displayName));
        }

        account.DisplayName = displayName.Trim();
        account.UpdatedAt = DateTimeOffset.UtcNow;

        var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
        if (site != null)
        {
            site.Name = account.DisplayName;
            await client.Updateable(site).UpdateColumns(s => new { s.Name }).ExecuteCommandAsync(ct);
        }

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            account.RefreshToken = refreshToken.Trim();
            await client.Updateable(account).ExecuteCommandAsync(ct);

            // 立即用新凭证刷新一次 token
            await _credentialRefreshService.RefreshKimiCredentialAsync(account.Id, ct);
            account = await client.Queryable<KimiAccount>().InSingleAsync(accountId) ?? account;
        }
        else
        {
            await client.Updateable(account).ExecuteCommandAsync(ct);
        }

        _metadataCache.InvalidateRouteTargets();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        _metadataCache.InvalidateKimiAccounts();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        return account;
    }

    /// <summary>
    /// 删除账号（级联删除关联的隐藏 Site 及路由规则、健康监控、模型映射等）。
    /// </summary>
    public async Task DeleteAsync(Guid accountId, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        var account = await client.Queryable<KimiAccount>().InSingleAsync(accountId);
        if (account == null) return;

        await _cascadeDeleter.RemoveSitesAsync([account.LinkedSiteId], ct);
        await client.Deleteable<KimiAccount>().Where(a => a.Id == accountId).ExecuteCommandAsync(ct);

        _metadataCache.InvalidateRouteTargets();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        _metadataCache.InvalidateModelMetadata();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        _metadataCache.InvalidateKimiAccounts();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
    }

    /// <summary>
    /// 按本次上游完整模型清单同步账号映射。Slug 为对外公开名（与 CLIProxyAPI 注册表一致），
    /// 映射的 RemoteModelName 存上游规范 ID（如 kimi-k2.5→k2.5），模型库条目用公开名展示。
    /// 未选中的既有映射会禁用，未选中的新模型不会创建映射。
    /// </summary>
    public async Task SyncRemoteModelsAsync(
        Guid linkedSiteId,
        IEnumerable<(string Slug, string DisplayName, bool Selected)> models,
        CancellationToken ct)
    {
        var modelList = models.ToList();
        if (modelList.Count == 0) return;

        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        var upstreamNames = modelList
            .Select(m => KimiModelNormalizer.NormalizeUpstreamModel(m.Slug))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingMappings = await client.Queryable<SiteModelMapping>()
            .Where(m => m.SiteId == linkedSiteId && upstreamNames.Contains(m.RemoteModelName))
            .ToListAsync(ct);
        var existingMappingDict = existingMappings.ToDictionary(m => m.RemoteModelName, m => m, StringComparer.OrdinalIgnoreCase);

        var toInsertMappings = new List<SiteModelMapping>();
        var toUpdateMappings = new List<SiteModelMapping>();
        var updatedMappingIds = new HashSet<Guid>();
        var orphanItemIds = new List<Guid>();
        var seenUpstreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (slug, displayName, selected) in modelList)
        {
            if (string.IsNullOrWhiteSpace(slug)) continue;
            var upstream = KimiModelNormalizer.NormalizeUpstreamModel(slug);
            if (string.IsNullOrWhiteSpace(upstream) || !seenUpstreams.Add(upstream)) continue;

            if (existingMappingDict.TryGetValue(upstream, out var mapping))
            {
                var targetItem = await ResolvePublicModelItemAsync(client, slug, upstream, displayName, ct);
                if (mapping.ModelLibraryItemId != targetItem.Id)
                {
                    // 旧版本曾把上游规范 ID 直接当模型库名，这里把映射改挂到公开名条目上。
                    orphanItemIds.Add(mapping.ModelLibraryItemId);
                    mapping.ModelLibraryItemId = targetItem.Id;
                    if (updatedMappingIds.Add(mapping.Id)) toUpdateMappings.Add(mapping);
                }

                if (mapping.IsEnabled != selected)
                {
                    mapping.IsEnabled = selected;
                    if (updatedMappingIds.Add(mapping.Id)) toUpdateMappings.Add(mapping);
                }
            }
            else if (selected)
            {
                var item = await ResolvePublicModelItemAsync(client, slug, upstream, displayName, ct);
                toInsertMappings.Add(new SiteModelMapping
                {
                    SiteId = linkedSiteId,
                    ModelLibraryItemId = item.Id,
                    RemoteModelName = upstream,
                    IsEnabled = true
                });
            }
        }

        if (toInsertMappings.Count > 0) await client.Insertable(toInsertMappings).ExecuteCommandAsync(ct);
        if (toUpdateMappings.Count > 0) await client.Updateable(toUpdateMappings).ExecuteCommandAsync(ct);
        await CleanupOrphanModelItemsAsync(client, orphanItemIds, ct);

        _metadataCache.InvalidateRouteTargets();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        _metadataCache.InvalidateModelMetadata();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
        _metadataCache.InvalidateKimiAccounts();
        await PushToCoreAsyncAccountCredentials(CancellationToken.None);
    }

    /// <summary>
    /// 生成 X-Msh-Device-Model 请求头（对齐官方 Kimi CLI 抓包格式，如 "Windows 10 AMD64"）。
    /// </summary>
    private static string GetDeviceModel()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var version = Environment.OSVersion.Version;
            var name = version.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} {GetArchLabel()}";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return $"macOS {GetArchLabel()}";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return $"Linux {GetArchLabel()}";
        }
        return $"{RuntimeInformation.OSDescription} {GetArchLabel()}";
    }

    private static string GetArchLabel() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "AMD64",
        Architecture.Arm64 => "ARM64",
        Architecture.X86 => "x86",
        _ => RuntimeInformation.ProcessArchitecture.ToString()
    };

    /// <summary>
    /// 归一化设备 ID 为官方 CLI 格式（32 位无横线小写 hex，来源 ~/.kimi/device_id）。
    /// </summary>
    private static string? NormalizeDeviceId(string? deviceId)
    {
        var normalized = deviceId?.Trim().Replace("-", string.Empty).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private async Task<KimiAccount?> FindExistingAsync(string? email, string? userId, string? deviceId, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        if (!string.IsNullOrWhiteSpace(email))
        {
            var byEmail = await client.Queryable<KimiAccount>()
                .FirstAsync(a => !a.IsDeleted && a.Email == email, ct);
            if (byEmail != null) return byEmail;
        }

        if (!string.IsNullOrWhiteSpace(userId))
        {
            var byUserId = await client.Queryable<KimiAccount>()
                .FirstAsync(a => !a.IsDeleted && a.UserId == userId, ct);
            if (byUserId != null) return byUserId;
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            var byDeviceId = await client.Queryable<KimiAccount>()
                .FirstAsync(a => !a.IsDeleted && a.DeviceId == deviceId, ct);
            if (byDeviceId != null) return byDeviceId;
        }

        return null;
    }

    private async Task UpsertModelMappingsCoreAsync(Guid siteId, IReadOnlyList<(string Slug, string DisplayName)> models, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        var existingMappings = await client.Queryable<SiteModelMapping>()
            .Where(m => m.SiteId == siteId)
            .ToListAsync(ct);

        var mappedRemotes = new HashSet<string>(existingMappings.Select(m => m.RemoteModelName), StringComparer.OrdinalIgnoreCase);
        var toInsert = new List<SiteModelMapping>();

        foreach (var (slug, displayName) in models)
        {
            if (string.IsNullOrWhiteSpace(slug)) continue;
            // Slug 为对外公开名，映射存上游规范 ID；已映射（按上游名）的模型跳过。
            var upstream = KimiModelNormalizer.NormalizeUpstreamModel(slug);
            if (string.IsNullOrWhiteSpace(upstream) || mappedRemotes.Contains(upstream)) continue;

            var item = await ResolvePublicModelItemAsync(client, slug, upstream, displayName, ct);
            toInsert.Add(new SiteModelMapping
            {
                SiteId = siteId,
                ModelLibraryItemId = item.Id,
                RemoteModelName = upstream,
                IsEnabled = true
            });
            mappedRemotes.Add(upstream);
        }

        if (toInsert.Count > 0)
        {
            await client.Insertable(toInsert).ExecuteCommandAsync(ct);
        }
    }

    /// <summary>
    /// 确保模型库中存在公开名为 publicName 的条目，返回其 Id。
    /// 历史兼容：早期版本曾把上游规范 ID 直接当模型库名（如 k3、kimi-for-coding），
    /// 若发现这类旧条目则原地更名为公开名，避免重复建项。
    /// </summary>
    private async Task<ModelLibraryItem> ResolvePublicModelItemAsync(
        ISqlSugarClient client,
        string publicName,
        string upstreamName,
        string displayName,
        CancellationToken ct)
    {
        var byPublic = await client.Queryable<ModelLibraryItem>().FirstAsync(m => m.ModelName == publicName, ct);
        if (byPublic != null) return byPublic;

        if (!string.Equals(upstreamName, publicName, StringComparison.OrdinalIgnoreCase))
        {
            var legacy = await client.Queryable<ModelLibraryItem>().FirstAsync(m => m.ModelName == upstreamName, ct);
            if (legacy != null)
            {
                legacy.ModelName = publicName;
                if (string.IsNullOrWhiteSpace(legacy.DisplayName)
                    || string.Equals(legacy.DisplayName, upstreamName, StringComparison.OrdinalIgnoreCase))
                {
                    legacy.DisplayName = string.IsNullOrWhiteSpace(displayName) ? publicName : displayName;
                }
                await client.Updateable(legacy).ExecuteCommandAsync(ct);
                return legacy;
            }
        }

        var item = new ModelLibraryItem
        {
            ModelName = publicName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? publicName : displayName,
            IsEnabled = true
        };
        await client.Insertable(item).ExecuteCommandAsync(ct);
        return item;
    }

    /// <summary>清理不再被任何映射引用的孤儿模型库条目。</summary>
    private async Task CleanupOrphanModelItemsAsync(ISqlSugarClient client, IReadOnlyList<Guid> itemIds, CancellationToken ct)
    {
        foreach (var itemId in itemIds.Distinct())
        {
            var referenced = await client.Queryable<SiteModelMapping>().AnyAsync(m => m.ModelLibraryItemId == itemId, ct);
            if (!referenced)
            {
                await client.Deleteable<ModelLibraryItem>().Where(m => m.Id == itemId).ExecuteCommandAsync(ct);
            }
        }
    }

    /// <summary>惰性解析 AdminCacheInvalidationService 推送变更到 Core（scoped，调用点建作用域）。</summary>
    private async Task PushToCoreAsyncAccountCredentials(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _corePushScopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AdminCacheInvalidationService>()
                .InvalidateAccountCredentialsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 推送失败不影响主流程：下次写操作或启动推送会重试。
        }
    }
}
