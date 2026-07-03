using System.Text.Json;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Domain.Codex;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// Codex 账号供给工厂：把 OAuth token / 导入凭证转换为「隐藏 Site + CodexAccount + 模型映射」，
/// 并支持级联删除与编辑。这是后端核心枢纽——所有写操作经它执行。
/// <para>
/// 采用「隐藏 Site 复用」方案：每个 Codex 账号自动创建一个 Responses 协议的隐藏 Site，
/// Models / Routes / Chat 经 SiteId 自动联动，无需改动这三处业务代码。
/// </para>
/// </summary>
public sealed class CodexAccountProvisioner
{
    private const string CodexManagedSource = "Codex";
    private const string CodexBaseUrl = "https://chatgpt.com/backend-api/codex";
    private const string CodexUserAgent = "codex_cli_rs/0.133.0 (Mac OS 26.3.1; arm64) iTerm.app/3.6.9";

    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ICodexModelCatalog _modelCatalog;
    private readonly SiteCascadeDeleter _cascadeDeleter;
    private readonly ILogger<CodexAccountProvisioner> _logger;

    public CodexAccountProvisioner(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        ICodexModelCatalog modelCatalog,
        SiteCascadeDeleter cascadeDeleter,
        ILogger<CodexAccountProvisioner> logger)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _modelCatalog = modelCatalog;
        _cascadeDeleter = cascadeDeleter;
        _logger = logger;
    }

    /// <summary>
    /// 用 token 创建或更新 Codex 账号（含隐藏 Site + 模型映射）。同 AccountId 二次供给视为更新。
    /// </summary>
    public async Task<CodexAccount> ProvisionFromTokensAsync(CodexProvisionInput input, CancellationToken ct)
    {
        // —— 去重：按 AccountId（兜底 Email）查现有账号 ——
        var existing = await FindExistingAsync(input.AccountId, input.Email, ct);

        CodexAccount account;
        Site site;

        if (existing != null)
        {
            // 更新 token
            account = existing;
            site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId)
                ?? throw new InvalidOperationException($"Linked site {account.LinkedSiteId} not found for Codex account {account.Id}");

            account.AccessToken = input.AccessToken;
            account.RefreshToken = input.RefreshToken;
            account.IdToken = input.IdToken;
            account.TokenExpiresAt = input.TokenExpiresAt;
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            if (!string.IsNullOrEmpty(input.AccountId)) account.AccountId = input.AccountId;
            if (!string.IsNullOrEmpty(input.Email)) account.Email = input.Email;
            if (!string.IsNullOrEmpty(input.PlanType)) account.PlanType = input.PlanType;

            site.ApiKey = input.AccessToken;
            if (!string.IsNullOrWhiteSpace(input.DisplayName)) site.Name = input.DisplayName;
            UpdateSiteExtraHeaders(site, account.AccountId ?? input.AccountId);

            await _dbContext.UpdateAsync(account, ct);
            await _dbContext.UpdateAsync(site, ct);
        }
        else
        {
            // 新建隐藏 Site + CodexAccount
            site = new Site
            {
                Name = string.IsNullOrWhiteSpace(input.DisplayName) ? (input.Email ?? "Codex 账号") : input.DisplayName,
                BaseUrl = CodexBaseUrl,
                EndpointPathMode = "versioned-base",
                ApiKey = input.AccessToken,
                SupportsOpenAi = false,
                SupportsAnthropic = false, // → ResolveSiteProtocolType 返回 "Responses"
                ManagedSource = CodexManagedSource,
                IsEnabled = true,
            };
            UpdateSiteExtraHeaders(site, input.AccountId);
            await _dbContext.InsertAsync(site, ct);

            account = new CodexAccount
            {
                DisplayName = site.Name,
                Email = input.Email,
                AccountId = input.AccountId,
                PlanType = input.PlanType,
                AccessToken = input.AccessToken,
                RefreshToken = input.RefreshToken,
                IdToken = input.IdToken,
                TokenExpiresAt = input.TokenExpiresAt,
                LastRefreshAt = DateTimeOffset.UtcNow,
                LinkedSiteId = site.Id,
                IsEnabled = true,
            };
            await _dbContext.InsertAsync(account, ct);
        }

        // —— 模型映射（按 plan 分层）——
        await UpsertModelMappingsAsync(site.Id, account.PlanType, ct);

        // —— 失效缓存（一次性）——
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();

        _logger.LogInformation("Codex account {Id} provisioned (site {SiteId})", account.Id, site.Id);
        return account;
    }

    /// <summary>
    /// 级联删除 Codex 账号：删 CodexAccount + 隐藏 Site（含映射/路由/空壳入口）。
    /// </summary>
    public async Task DeprovisionAsync(Guid codexAccountId, CancellationToken ct)
    {
        var account = (await _dbContext.CodexAccounts
            .Where(a => a.Id == codexAccountId)
            .ToListAsync(ct)).FirstOrDefault();
        if (account == null)
        {
            throw new InvalidOperationException($"Codex account {codexAccountId} not found");
        }

        await _cascadeDeleter.RemoveSitesAsync([account.LinkedSiteId], ct);
        await _dbContext.DeleteAsync<CodexAccount>(a => a.Id == codexAccountId, ct);

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();

        _logger.LogInformation("Codex account {Id} deprovisioned", codexAccountId);
    }

    /// <summary>
    /// 编辑 Codex 账号（DisplayName / AutoDisableThreshold），同步隐藏 Site 名称。
    /// </summary>
    public async Task UpdateAsync(Guid codexAccountId, string? displayName, decimal? autoDisableThreshold, CancellationToken ct)
    {
        var account = (await _dbContext.CodexAccounts
            .Where(a => a.Id == codexAccountId)
            .ToListAsync(ct)).FirstOrDefault() ?? throw new InvalidOperationException($"Codex account {codexAccountId} not found");
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            account.DisplayName = displayName;
            var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.Name = displayName;
                await _dbContext.UpdateAsync(site, ct);
            }
        }
        account.AutoDisableThreshold = autoDisableThreshold;
        await _dbContext.UpdateAsync(account, ct);

        _metadataCache.InvalidateRouteTargets();
    }

    /// <summary>
    /// 为指定隐藏 Site 追加模型映射（动态拉取结果复用）。已存在的 RemoteModelName 跳过。
    /// </summary>
    public async Task UpsertRemoteModelsAsync(Guid linkedSiteId, IEnumerable<(string Slug, string DisplayName)> models, CancellationToken ct)
    {
        await UpsertModelMappingsCoreAsync(linkedSiteId, models, ct);
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
    }

    // —— 私有 ——

    private async Task<CodexAccount?> FindExistingAsync(string? accountId, string? email, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(accountId))
        {
            var byAccount = (await _dbContext.CodexAccounts
                .Where(a => a.AccountId == accountId)
                .ToListAsync(ct)).FirstOrDefault();
            if (byAccount != null) return byAccount;
        }
        if (!string.IsNullOrEmpty(email))
        {
            return (await _dbContext.CodexAccounts
                .Where(a => a.Email == email)
                .ToListAsync(ct)).FirstOrDefault();
        }
        return null;
    }

    private async Task UpsertModelMappingsAsync(Guid siteId, string? planType, CancellationToken ct)
    {
        var modelNames = _modelCatalog.GetModelsForPlan(planType);
        var models = modelNames.Select(n => (Slug: n, DisplayName: n));
        await UpsertModelMappingsCoreAsync(siteId, models, ct);
    }

    /// <summary>
    /// 批量 upsert 模型库 + 映射（P6：先 upsert ModelLibraryItem，再建指向它的 SiteModelMapping）。
    /// </summary>
    private async Task UpsertModelMappingsCoreAsync(Guid siteId, IEnumerable<(string Slug, string DisplayName)> models, CancellationToken ct)
    {
        var modelList = models.ToList();
        if (modelList.Count == 0) return;

        var remoteNames = modelList.Select(m => m.Slug).Distinct().ToList();

        // 载入内存求差集（P2/P6）
        var existingModelItems = await _dbContext.ModelLibraryItems
            .Where(m => remoteNames.Contains(m.ModelName))
            .ToListAsync(ct);
        var existingModelDict = existingModelItems.ToDictionary(m => m.ModelName, m => m);

        var existingMappings = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == siteId && remoteNames.Contains(m.RemoteModelName))
            .ToListAsync(ct);
        var existingMappingNames = existingMappings.Select(m => m.RemoteModelName).ToHashSet();

        // 1) 先建缺失的 ModelLibraryItem（拿到稳定 Id）
        var newModelItems = new List<ModelLibraryItem>();
        foreach (var (slug, displayName) in modelList)
        {
            if (existingModelDict.ContainsKey(slug)) continue;
            // 跨多个新模型去重
            if (newModelItems.Any(x => x.ModelName == slug)) continue;
            var item = new ModelLibraryItem
            {
                ModelName = slug,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? slug : displayName,
            };
            _dbContext.ModelLibraryItems.Add(item);
            newModelItems.Add(item);
            existingModelDict[slug] = item; // 供后续 mapping 引用
        }

        // 2) 再建缺失的 SiteModelMapping
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

    private static void UpdateSiteExtraHeaders(Site site, string? accountId)
    {
        var headers = new Dictionary<string, string>
        {
            ["Originator"] = "codex_cli_rs",
            ["Chatgpt-Account-Id"] = accountId ?? string.Empty,
            ["User-Agent"] = CodexUserAgent,
        };
        site.ExtraHeadersJson = JsonSerializer.Serialize(headers, JsonSerializerPresets.Compact);
    }
}
