using AITool.Application.Networking;
using AITool.Application.Xai;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using SqlSugar;

namespace AITool.Web.Services;

/// <summary>
/// xAI (Grok) 账号供给工厂：把 OAuth token / 导入凭证转换为「隐藏 Site + XaiAccount + 模型映射」，
/// 并支持级联删除与编辑（对齐 Kimi / Google / Codex 的隐藏 Site 复用方案）。
/// 隐藏站点指向 https://api.x.ai/v1（原生 OpenAI Chat + Responses）。
/// </summary>
public sealed class XaiAccountProvisioner
{
    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
    private readonly AccountNetworkPolicyStore? _networkPolicies;
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IXaiModelFetcher _modelFetcher;
    private readonly SiteCascadeDeleter _cascadeDeleter;
    private readonly ILogger<XaiAccountProvisioner> _logger;

    public XaiAccountProvisioner(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IXaiModelFetcher modelFetcher,
        SiteCascadeDeleter cascadeDeleter,
        ILogger<XaiAccountProvisioner> logger,
        NetworkBoundOAuthClientFactory? networkFactory = null,
        AccountNetworkPolicyStore? networkPolicies = null)
    {
        _networkFactory = networkFactory;
        _networkPolicies = networkPolicies;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _modelFetcher = modelFetcher;
        _cascadeDeleter = cascadeDeleter;
        _logger = logger;
    }

    /// <summary>
    /// 用 token 创建或更新 xAI 账号（含隐藏 Site + 模型映射）。按 ExternalUserId（JWT sub）
    /// 匹配既有账号：重复登录同一账号时更新 token 而不是另建。
    /// </summary>
    private static readonly SemaphoreSlim ProvisionGate = new(1, 1);

    public async Task<XaiAccount> ProvisionFromTokensAsync(XaiProvisionInput input, CancellationToken ct,
        NetworkOperationContext? networkOperation = null, bool updateNetworkPolicy = false)
    {
        await ProvisionGate.WaitAsync(ct);
        try { return await ProvisionCoreAsync(input, ct, networkOperation, updateNetworkPolicy); }
        finally { ProvisionGate.Release(); }
    }

    private async Task<XaiAccount> ProvisionCoreAsync(XaiProvisionInput input, CancellationToken ct,
        NetworkOperationContext? networkOperation, bool updateNetworkPolicy)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        XaiAccount account;
        Site site;
        client.Ado.BeginTran();
        try
        {
            // Lock publication before identity resolution; policy and credentials become visible together.
            await client.Ado.ExecuteCommandAsync("UPDATE XaiAccounts SET UpdatedAt=UpdatedAt WHERE 1=0;");

        var existing = networkOperation?.Account is { } known && known.ProviderKey == "xai"
            ? await client.Queryable<XaiAccount>().InSingleAsync(known.AccountId)
            : await FindExistingAsync(input.Email, input.ExternalUserId, ct);



        if (existing != null)
        {
            account = existing;
            site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId)
                ?? throw new InvalidOperationException($"Linked site {account.LinkedSiteId} not found for xAI account {account.Id}");

            account.AccessToken = input.AccessToken;
            if (!string.IsNullOrWhiteSpace(input.RefreshToken)) account.RefreshToken = input.RefreshToken;
            account.Scope = input.Scope;
            account.TokenExpiresAt = input.TokenExpiresAt;
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            if (account.RequiresReauth)
            {
                account.RequiresReauth = false;
                var settings = await client.Queryable<AITool.Domain.Operations.SystemRuntimeSettings>().InSingleAsync(1);
                if (settings is { OAuthFeaturesEnabled: false } && !account.ManuallyDisabled && !account.IsDeleted)
                    account.DisabledByFeatureToggle = true;
                account.IsEnabled = !account.ManuallyDisabled && !account.DisabledByFeatureToggle && !account.IsDeleted;
            }
            if (!string.IsNullOrEmpty(input.Email)) account.Email = input.Email;
            if (!string.IsNullOrEmpty(input.ExternalUserId)) account.ExternalUserId = input.ExternalUserId;
            if (!string.IsNullOrWhiteSpace(input.DisplayName)) account.DisplayName = input.DisplayName;
            account.UpdatedAt = DateTimeOffset.UtcNow;

            site.ApiKey = input.AccessToken;
            if (!string.IsNullOrWhiteSpace(input.DisplayName)) site.Name = input.DisplayName;
            site.IsEnabled = account.IsEnabled;

            await client.Updateable(account).UpdateColumns(a => new { a.AccessToken, a.RefreshToken, a.Scope, a.TokenExpiresAt, a.LastRefreshAt, a.RequiresReauth, a.DisabledByFeatureToggle, a.Email, a.ExternalUserId, a.DisplayName, a.IsEnabled, a.ManuallyDisabled, a.IsDeleted, a.UpdatedAt }).ExecuteCommandAsync(ct);
            await client.Updateable(site).UpdateColumns(s => new { s.ApiKey, s.Name, s.IsEnabled }).ExecuteCommandAsync(ct);
        }
        else
        {
            site = new Site
            {
                Name = string.IsNullOrWhiteSpace(input.DisplayName)
                    ? (input.Email ?? "Grok 账号")
                    : input.DisplayName,
                BaseUrl = XaiConstants.ApiBaseUrl,
                // api.x.ai/v1 已含版本段：转发直接追加 /chat/completions 或 /responses。
                EndpointPathMode = "versioned-base",
                ApiKey = input.AccessToken,
                // xAI 原生提供 OpenAI Chat 与 Responses 两个一等端点（cc-switch 预设实测），
                // 两个能力都声明，转发按请求协议直传不转换。
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                SupportsResponses = true,
                ProtocolType = "OpenAI",
                ManagedSource = XaiConstants.ManagedSource,
                IsEnabled = true,
            };
            await client.Insertable(site).ExecuteCommandAsync(ct);

            account = new XaiAccount
            {
                DisplayName = site.Name,
                Email = input.Email,
                ExternalUserId = input.ExternalUserId,
                AccessToken = input.AccessToken,
                RefreshToken = input.RefreshToken,
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

        if (updateNetworkPolicy)
        {
            if (_networkPolicies is null || networkOperation is null)
                throw new NetworkPolicyException("network_policy_unavailable", "Network policy persistence is unavailable.");
            await _networkPolicies.SetInTransactionAsync(client, new AccountIdentity("xai", account.Id),
                networkOperation.Selection, expectedProfileRevision: networkOperation.ExpectedProfileRevision,
                cancellationToken: ct);
        }
        client.Ado.CommitTran();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }

        // 模型由用户手动选择导入；供给和重新登录不请求目录或修改映射。

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        return account;
    }

    /// <summary>
    /// 启用/禁用 xAI 账号及其关联的隐藏 Site。手动禁用会记录 ManuallyDisabled，
    /// 额度巡检不会自动恢复手动禁用的账号。
    /// </summary>
    public async Task ToggleAsync(Guid accountId, bool isEnabled, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        try
        {
        await client.Ado.ExecuteCommandAsync("UPDATE XaiAccounts SET UpdatedAt=UpdatedAt WHERE 1=0;");
        var account = await client.Queryable<XaiAccount>().InSingleAsync(accountId);
        if (account == null) { await client.Ado.CommitTranAsync(); return; }

        account.ManuallyDisabled = !isEnabled;
        account.IsEnabled = isEnabled && !account.RequiresReauth && !account.DisabledByFeatureToggle && !account.IsDeleted;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await client.Updateable(account).UpdateColumns(a => new { a.IsEnabled, a.ManuallyDisabled, a.UpdatedAt }).ExecuteCommandAsync(ct);

        var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
        if (site != null)
        {
            site.IsEnabled = account.IsEnabled;
            await client.Updateable(site).UpdateColumns(s => new { s.IsEnabled }).ExecuteCommandAsync(ct);
        }

        await client.Ado.CommitTranAsync();
        }
        catch { await client.Ado.RollbackTranAsync(); throw; }
        _metadataCache.InvalidateRouteTargets();
    }

    /// <summary>
    /// 更新展示名与（可选）refresh_token。
    /// </summary>
    public async Task<XaiAccount> UpdateAsync(Guid accountId, string displayName, string? refreshToken, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        var account = await client.Queryable<XaiAccount>().InSingleAsync(accountId)
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
            await client.Updateable(account).UpdateColumns(a => new { a.DisplayName, a.RefreshToken, a.UpdatedAt }).ExecuteCommandAsync(ct);
            // Only clear a refresh-refusal marker, not a forwarding 401 that disabled the account.
            await client.Updateable<XaiAccount>()
                .SetColumns(a => new XaiAccount { RequiresReauth = false })
                .Where(a => a.Id == accountId && a.IsEnabled && a.RefreshToken == account.RefreshToken)
                .ExecuteCommandAsync(ct);
        }
        else
        {
            await client.Updateable(account).UpdateColumns(a => new { a.DisplayName, a.UpdatedAt }).ExecuteCommandAsync(ct);
        }

        _metadataCache.InvalidateRouteTargets();
        return account;
    }

    /// <summary>
    /// 删除账号（级联删除关联的隐藏 Site 及路由规则、健康监控、模型映射等）。
    /// </summary>
    public async Task DeleteAsync(Guid accountId, CancellationToken ct)
    {
        await _cascadeDeleter.ExecuteGraphMutationAsync(async () =>
        {
            var client = _dbContext.Client;
            var account = await client.Queryable<XaiAccount>().InSingleAsync(accountId);
            if (account is null) return false;
            if (_networkPolicies is not null)
            {
                await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
                await _networkPolicies.DeleteInTransactionAsync(client, new AccountIdentity("xai", account.Id), cancellationToken: ct);
            }
            if (account.LinkedSiteId != Guid.Empty)
                await _cascadeDeleter.RemoveSitesInGraphMutationAsync([account.LinkedSiteId], ct);
            await client.Deleteable<XaiAccount>().Where(a => a.Id == accountId).ExecuteCommandAsync(ct);
            return true;
        }, ct, _metadataCache);

    }

    /// <summary>
    /// 按本次上游完整模型清单同步账号映射。xAI 公开名即上游 ID，无需归一化。
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
            .Select(m => m.Slug.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingMappings = await client.Queryable<SiteModelMapping>()
            .Where(m => m.SiteId == linkedSiteId && upstreamNames.Contains(m.RemoteModelName))
            .ToListAsync(ct);
        var existingMappingDict = existingMappings.ToDictionary(m => m.RemoteModelName, m => m, StringComparer.OrdinalIgnoreCase);

        var toInsertMappings = new List<SiteModelMapping>();
        var toUpdateMappings = new List<SiteModelMapping>();
        var seenUpstreams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (slug, displayName, selected) in modelList)
        {
            if (string.IsNullOrWhiteSpace(slug)) continue;
            var upstream = slug.Trim();
            if (!seenUpstreams.Add(upstream)) continue;

            if (existingMappingDict.TryGetValue(upstream, out var mapping))
            {
                if (mapping.IsEnabled != selected)
                {
                    mapping.IsEnabled = selected;
                    toUpdateMappings.Add(mapping);
                }
            }
            else if (selected)
            {
                var item = await ResolveModelItemAsync(client, upstream, displayName, ct);
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

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
    }

    /// <summary>确保模型库中存在该 ID 的条目，返回其 Id。</summary>
    private async Task<ModelLibraryItem> ResolveModelItemAsync(
        ISqlSugarClient client,
        string modelName,
        string displayName,
        CancellationToken ct)
    {
        var existing = await client.Queryable<ModelLibraryItem>().FirstAsync(m => m.ModelName == modelName, ct);
        if (existing != null) return existing;

        var item = new ModelLibraryItem
        {
            ModelName = modelName,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? modelName : displayName,
            IsEnabled = true
        };
        await client.Insertable(item).ExecuteCommandAsync(ct);
        return item;
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
            if (!mappedRemotes.Add(slug)) continue;

            var item = await ResolveModelItemAsync(client, slug, displayName, ct);
            toInsert.Add(new SiteModelMapping
            {
                SiteId = siteId,
                ModelLibraryItemId = item.Id,
                RemoteModelName = slug,
                IsEnabled = true
            });
        }

        if (toInsert.Count > 0)
        {
            await client.Insertable(toInsert).ExecuteCommandAsync(ct);
        }
    }

    /// <summary>按身份匹配既有账号：优先 ExternalUserId（JWT sub，稳定），回退 Email。</summary>
    private async Task<XaiAccount?> FindExistingAsync(string? email, string? externalUserId, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        if (!string.IsNullOrWhiteSpace(externalUserId))
        {
            var byUserId = await client.Queryable<XaiAccount>()
                .FirstAsync(a => !a.IsDeleted && a.ExternalUserId == externalUserId, ct);
            if (byUserId != null) return byUserId;
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var byEmail = await client.Queryable<XaiAccount>()
                .FirstAsync(a => !a.IsDeleted && a.Email == email, ct);
            if (byEmail != null) return byEmail;
        }

        return null;
    }
}
