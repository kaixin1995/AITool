using AITool.Application.Common;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// Google 账号供给输入：OAuth 交换或凭证导入得到的一次账号快照。
/// </summary>
public sealed record GoogleProvisionInput
{
    /// <summary>接入方式（GeminiCli / Antigravity）。</summary>
    public required string AccountKind { get; init; }

    /// <summary>展示名（缺省用邮箱）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>账号邮箱。</summary>
    public string? Email { get; init; }

    /// <summary>项目 ID。</summary>
    public string? ProjectId { get; init; }

    /// <summary>订阅等级（Antigravity：free/pro/ultra）。</summary>
    public string? SubscriptionTier { get; init; }

    /// <summary>剩余积分。</summary>
    public int? CreditAmount { get; init; }

    /// <summary>访问令牌。</summary>
    public required string AccessToken { get; init; }

    /// <summary>刷新令牌。</summary>
    public string? RefreshToken { get; init; }

    /// <summary>令牌过期时刻。</summary>
    public required DateTimeOffset TokenExpiresAt { get; init; }
}

/// <summary>
/// Google 账号供给工厂：把 OAuth token / 导入凭证转换为「隐藏 Site + GoogleAccount + 模型映射」，
/// 并支持级联删除与编辑（对齐 CodexAccountProvisioner 的隐藏 Site 复用方案）。
/// <para>
/// 隐藏 Site 以 ProtocolType=Gemini 接入转发链路（Supports* 全 false），Models / Routes / Chat
/// 经 SiteId 自动联动；模型目录：GeminiCli 用静态清单（对齐 gcli2api BASE_MODELS），
/// Antigravity 走 fetchAvailableModels 动态拉取（失败时跳过，可稍后手动拉取）。
/// </para>
/// </summary>
public sealed class GoogleAccountProvisioner
{
    /// <summary>GeminiCLI 静态模型清单（与模型拉取器共用，定义在 GoogleAccountKinds.GeminiCliModels）。</summary>
    public static readonly string[] GeminiCliModels = GoogleAccountKinds.GeminiCliModels;

    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IGoogleModelFetcher _modelFetcher;
    private readonly SiteCascadeDeleter _cascadeDeleter;
    private readonly ILogger<GoogleAccountProvisioner> _logger;

    public GoogleAccountProvisioner(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IGoogleModelFetcher modelFetcher,
        SiteCascadeDeleter cascadeDeleter,
        ILogger<GoogleAccountProvisioner> logger)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _modelFetcher = modelFetcher;
        _cascadeDeleter = cascadeDeleter;
        _logger = logger;
    }

    /// <summary>
    /// 用 token 创建或更新 Google 账号（含隐藏 Site + 模型映射）。同 (AccountKind, Email) 二次供给视为更新。
    /// </summary>
    public async Task<GoogleAccount> ProvisionFromTokensAsync(GoogleProvisionInput input, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        var existing = await FindExistingAsync(input.AccountKind, input.Email, ct);

        GoogleAccount account;
        Site site;

        if (existing != null)
        {
            account = existing;
            site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId)
                ?? throw new InvalidOperationException($"Linked site {account.LinkedSiteId} not found for Google account {account.Id}");

            account.AccessToken = input.AccessToken;
            if (!string.IsNullOrWhiteSpace(input.RefreshToken)) account.RefreshToken = input.RefreshToken;
            account.TokenExpiresAt = input.TokenExpiresAt;
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(input.Email)) account.Email = input.Email;
            if (!string.IsNullOrEmpty(input.ProjectId)) account.ProjectId = input.ProjectId;
            if (!string.IsNullOrEmpty(input.SubscriptionTier)) account.SubscriptionTier = input.SubscriptionTier;
            if (input.CreditAmount.HasValue) account.CreditAmount = input.CreditAmount;

            site.ApiKey = input.AccessToken;
            if (!string.IsNullOrWhiteSpace(input.DisplayName)) site.Name = input.DisplayName;

            await client.Updateable(account).ExecuteCommandAsync(ct);
            await client.Updateable(site).ExecuteCommandAsync(ct);
        }
        else
        {
            site = new Site
            {
                Name = string.IsNullOrWhiteSpace(input.DisplayName)
                    ? (input.Email ?? $"{GoogleAccountKinds.Normalize(input.AccountKind)} 账号")
                    : input.DisplayName,
                BaseUrl = GoogleAccountKinds.GetBaseUrl(input.AccountKind),
                EndpointPathMode = "standard-root",
                ApiKey = input.AccessToken,
                SupportsOpenAi = false,
                SupportsAnthropic = false,
                SupportsResponses = false,
                // Gemini 原生上游：协议由 ProtocolType=Gemini 标识（三个 Supports* 全 false），
                // 客户端三种协议经 ProxyProtocolResolver 统一桥接到 Gemini。
                ProtocolType = "Gemini",
                ManagedSource = GoogleAccountKinds.ManagedSource,
                IsEnabled = true,
            };
            await client.Insertable(site).ExecuteCommandAsync(ct);

            account = new GoogleAccount
            {
                DisplayName = site.Name,
                Email = input.Email,
                AccountKind = GoogleAccountKinds.Normalize(input.AccountKind),
                ProjectId = input.ProjectId,
                SubscriptionTier = input.SubscriptionTier,
                CreditAmount = input.CreditAmount,
                AccessToken = input.AccessToken,
                RefreshToken = input.RefreshToken,
                TokenExpiresAt = input.TokenExpiresAt,
                LastRefreshAt = DateTimeOffset.UtcNow,
                LinkedSiteId = site.Id,
                IsEnabled = true,
            };
            await client.Insertable(account).ExecuteCommandAsync(ct);
        }

        // —— 模型映射 ——
        if (string.Equals(account.AccountKind, GoogleAccountKinds.Antigravity, StringComparison.OrdinalIgnoreCase))
        {
            // Antigravity：动态拉取模型清单（失败不阻断供给，可稍后手动拉取）。
            try
            {
                var models = await _modelFetcher.FetchAsync(account.AccountKind, input.AccessToken, ct);
                if (models.Count > 0)
                {
                    await UpsertModelMappingsCoreAsync(site.Id, models, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Antigravity 模型拉取失败（账号供给继续）: {AccountId}", account.Id);
            }
        }
        else
        {
            var staticModels = GeminiCliModels.Select(n => (Slug: n, DisplayName: n));
            await UpsertModelMappingsCoreAsync(site.Id, staticModels, ct);
        }

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateGoogleAccounts();

        _logger.LogInformation("Google account {Id} provisioned (kind {Kind}, site {SiteId})", account.Id, account.AccountKind, site.Id);
        return account;
    }

    /// <summary>
    /// 级联删除 Google 账号：删 GoogleAccount + 隐藏 Site（含映射/路由/空壳入口）。
    /// </summary>
    public async Task DeprovisionAsync(Guid accountId, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var account = (await client.Queryable<GoogleAccount>()
            .Where(a => a.Id == accountId)
            .ToListAsync(ct)).FirstOrDefault();
        if (account == null)
        {
            throw new InvalidOperationException($"Google account {accountId} not found");
        }

        await _cascadeDeleter.RemoveSitesAsync([account.LinkedSiteId], ct);
        await client.Deleteable<GoogleAccount>().Where(a => a.Id == accountId).ExecuteCommandAsync(ct);

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateGoogleAccounts();

        _logger.LogInformation("Google account {Id} deprovisioned", account.Id);
    }

    /// <summary>
    /// 编辑 Google 账号（当前仅支持 DisplayName），同步隐藏 Site 名称。
    /// </summary>
    public async Task UpdateAsync(Guid accountId, string? displayName, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var account = (await client.Queryable<GoogleAccount>()
            .Where(a => a.Id == accountId)
            .ToListAsync(ct)).FirstOrDefault() ?? throw new InvalidOperationException($"Google account {accountId} not found");
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            account.DisplayName = displayName;
            var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.Name = displayName;
                await client.Updateable(site).ExecuteCommandAsync(ct);
            }
        }
        await client.Updateable(account).ExecuteCommandAsync(ct);

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateGoogleAccounts();
    }

    /// <summary>
    /// 为指定隐藏 Site 追加模型映射（动态拉取结果复用）。已存在的 RemoteModelName 跳过。
    /// </summary>
    public async Task UpsertRemoteModelsAsync(Guid linkedSiteId, IEnumerable<(string Slug, string DisplayName)> models, CancellationToken ct)
    {
        await UpsertModelMappingsCoreAsync(linkedSiteId, models, ct);
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateGoogleAccounts();
    }

    /// <summary>
    /// 按本次上游完整模型清单同步账号映射。未选中的既有映射会禁用，
    /// 未选中的新模型不会创建映射；这使模型选择能够真正反映到路由和聊天页。
    /// </summary>
    public async Task SyncRemoteModelsAsync(
        Guid linkedSiteId,
        IEnumerable<(string Slug, string DisplayName, bool Selected)> models,
        CancellationToken ct)
    {
        await SyncModelMappingsCoreAsync(linkedSiteId, models, ct);
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateGoogleAccounts();
    }

    // —— 私有 ——

    private async Task<GoogleAccount?> FindExistingAsync(string accountKind, string? email, CancellationToken ct)
    {
        var normalizedKind = GoogleAccountKinds.Normalize(accountKind);
        if (!string.IsNullOrEmpty(email))
        {
            var byEmailAndKind = (await _dbContext.GoogleAccounts
                .Where(a => a.Email == email && a.AccountKind == normalizedKind)
                .ToListAsync(ct)).FirstOrDefault();
            if (byEmailAndKind != null) return byEmailAndKind;
        }

        return null;
    }

    /// <summary>
    /// 批量 upsert 模型库 + 映射（与 CodexAccountProvisioner 相同的两步策略）。
    /// </summary>
    private async Task UpsertModelMappingsCoreAsync(Guid siteId, IEnumerable<(string Slug, string DisplayName)> models, CancellationToken ct)
    {
        var modelList = models.ToList();
        if (modelList.Count == 0) return;

        var remoteNames = modelList.Select(m => m.Slug).Distinct().ToList();

        var existingModelItems = await _dbContext.ModelLibraryItems
            .Where(m => remoteNames.Contains(m.ModelName))
            .ToListAsync(ct);
        var existingModelDict = existingModelItems.ToDictionary(m => m.ModelName, m => m);

        var existingMappings = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == siteId && remoteNames.Contains(m.RemoteModelName))
            .ToListAsync(ct);
        var existingMappingNames = existingMappings.Select(m => m.RemoteModelName).ToHashSet();

        var newModelItems = new List<ModelLibraryItem>();
        foreach (var (slug, displayName) in modelList)
        {
            if (existingModelDict.TryGetValue(slug, out var existingModel)
                && !string.IsNullOrWhiteSpace(displayName)
                && (string.IsNullOrWhiteSpace(existingModel.DisplayName)
                    || string.Equals(existingModel.DisplayName, slug, StringComparison.OrdinalIgnoreCase))
                && !string.Equals(existingModel.DisplayName, displayName, StringComparison.Ordinal))
            {
                existingModel.DisplayName = displayName;
                await _dbContext.UpdateAsync(existingModel, ct);
            }

            if (existingModelDict.ContainsKey(slug)) continue;
            if (newModelItems.Any(x => x.ModelName == slug)) continue;
            var item = new ModelLibraryItem
            {
                ModelName = slug,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? slug : displayName,
            };
            _dbContext.ModelLibraryItems.Add(item);
            newModelItems.Add(item);
            existingModelDict[slug] = item;
        }

        foreach (var (slug, _) in modelList)
        {
            if (existingMappingNames.Contains(slug)) continue;
            if (!existingModelDict.TryGetValue(slug, out var libItem)) continue;
            _dbContext.SiteModelMappings.Add(new SiteModelMapping
            {
                SiteId = siteId,
                ModelLibraryItemId = libItem.Id,
                RemoteModelName = slug,
                LastStatus = "imported",
                IsEnabled = true,
            });
        }
    }

    private async Task SyncModelMappingsCoreAsync(
        Guid siteId,
        IEnumerable<(string Slug, string DisplayName, bool Selected)> models,
        CancellationToken ct)
    {
        var modelList = models
            .Where(model => !string.IsNullOrWhiteSpace(model.Slug))
            .GroupBy(model => model.Slug.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var last = group.Last();
                return (Slug: group.Key, DisplayName: last.DisplayName, Selected: last.Selected);
            })
            .ToList();
        if (modelList.Count == 0) return;

        var remoteNames = modelList.Select(model => model.Slug).ToList();
        var selectionByRemote = modelList.ToDictionary(model => model.Slug, StringComparer.OrdinalIgnoreCase);

        var existingModelItems = await _dbContext.ModelLibraryItems
            .Where(model => remoteNames.Contains(model.ModelName))
            .ToListAsync(ct);
        var existingModelDict = existingModelItems.ToDictionary(model => model.ModelName, StringComparer.OrdinalIgnoreCase);

        var existingMappings = await _dbContext.SiteModelMappings
            .Where(mapping => mapping.SiteId == siteId)
            .ToListAsync(ct);
        var existingMappingDict = existingMappings.ToDictionary(mapping => mapping.RemoteModelName, StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in existingMappings)
        {
            if (selectionByRemote.TryGetValue(mapping.RemoteModelName, out var selection))
            {
                mapping.IsEnabled = selection.Selected;
                mapping.LastStatus = selection.Selected ? "imported" : "disabled";
            }
            else
            {
                mapping.IsEnabled = false;
                mapping.LastStatus = "disabled";
            }

            await _dbContext.UpdateAsync(mapping, ct);
        }

        foreach (var (slug, displayName, selected) in modelList)
        {
            if (selected
                && existingModelDict.TryGetValue(slug, out var existingModel)
                && !string.IsNullOrWhiteSpace(displayName)
                && (string.IsNullOrWhiteSpace(existingModel.DisplayName)
                    || string.Equals(existingModel.DisplayName, slug, StringComparison.OrdinalIgnoreCase))
                && !string.Equals(existingModel.DisplayName, displayName, StringComparison.Ordinal))
            {
                existingModel.DisplayName = displayName;
                await _dbContext.UpdateAsync(existingModel, ct);
            }

            if (!selected || existingMappingDict.ContainsKey(slug)) continue;

            if (!existingModelDict.TryGetValue(slug, out var modelItem))
            {
                modelItem = new ModelLibraryItem
                {
                    ModelName = slug,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? slug : displayName,
                };
                _dbContext.ModelLibraryItems.Add(modelItem);
                existingModelDict[slug] = modelItem;
            }

            _dbContext.SiteModelMappings.Add(new SiteModelMapping
            {
                SiteId = siteId,
                ModelLibraryItemId = modelItem.Id,
                RemoteModelName = slug,
                LastStatus = "imported",
                IsEnabled = true,
            });
        }
    }
}
