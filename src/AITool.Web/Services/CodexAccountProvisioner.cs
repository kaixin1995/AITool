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

    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ICodexModelCatalog _modelCatalog;
    private readonly SiteCascadeDeleter _cascadeDeleter;
    private readonly ILogger<CodexAccountProvisioner> _logger;

    /// <summary>按身份键串行化供给（check-then-insert 原子性），键量级=账号数，常驻无清理可接受。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _provisionGates = new();

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
    /// <para>按身份键（accountId，兜底 email）全程串行化：OAuth 回调双击/并发导入若并发执行
    /// check-then-insert 会落两条同身份账号，两个刷新循环共用同一个会被轮换的 refresh_token
    /// 互相踩踏，最终该 workspace 凭证被上游永久作废。</para>
    /// </summary>
    public async Task<CodexAccount> ProvisionFromTokensAsync(CodexProvisionInput input, CancellationToken ct)
    {
        var identity = input.AccountId is { Length: > 0 } ? "acc:" + input.AccountId
            : input.Email is { Length: > 0 } ? "email:" + input.Email
            : "anon:" + Guid.NewGuid(); // 无身份无邮箱的并发供给无法互相同步（极罕见），退化为无锁
        var gate = _provisionGates.GetOrAdd(identity, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await ProvisionFromTokensCoreAsync(input, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CodexAccount> ProvisionFromTokensCoreAsync(CodexProvisionInput input, CancellationToken ct)
    {
        // 用 CopyNew 独立连接做所有 DB 操作
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        // —— 去重：按 AccountId（兜底 Email）查现有账号 ——
        var existing = await FindExistingAsync(input.AccountId, input.Email, ct);

        CodexAccount account;
        Site site;

        if (existing != null)
        {
            // 更新 token
            account = existing;
            site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId)
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

            await client.Updateable(account).ExecuteCommandAsync(ct);
            await client.Updateable(site).ExecuteCommandAsync(ct);
        }
        else
        {
            // 新建隐藏 Site + CodexAccount
            site = new Site
            {
                // 同一邮箱可属于多个 workspace（plus + team），默认名带 plan 后缀区分（对齐 CPA 凭证文件名含 plan）。
                Name = BuildDefaultDisplayName(input.DisplayName, input.Email, input.PlanType),
                BaseUrl = CodexBaseUrl,
                EndpointPathMode = "versioned-base",
                ApiKey = input.AccessToken,
                SupportsOpenAi = false,
                SupportsAnthropic = false,
                // Codex 上游使用 Responses 原生接口，显式声明以避免依赖旧配置兼容分支。
                SupportsResponses = true,
                ManagedSource = CodexManagedSource,
                IsEnabled = true,
            };
            UpdateSiteExtraHeaders(site, input.AccountId);
            await client.Insertable(site).ExecuteCommandAsync(ct);

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
            await client.Insertable(account).ExecuteCommandAsync(ct);
        }

        // —— 模型映射（按 plan 分层）——
        await UpsertModelMappingsAsync(site.Id, account.PlanType, ct);

        // —— 失效缓存（一次性）——
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateCodexAccounts();

        _logger.LogInformation("Codex account {Id} provisioned (site {SiteId})", account.Id, site.Id);
        return account;
    }

    /// <summary>
    /// 级联删除 Codex 账号：删 CodexAccount + 隐藏 Site（含映射/路由/空壳入口）。
    /// </summary>
    public async Task DeprovisionAsync(Guid codexAccountId, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var account = (await client.Queryable<CodexAccount>()
            .Where(a => a.Id == codexAccountId)
            .ToListAsync(ct)).FirstOrDefault();
        if (account == null)
        {
            throw new InvalidOperationException($"Codex account {codexAccountId} not found");
        }

        await _cascadeDeleter.RemoveSitesAsync([account.LinkedSiteId], ct);
        await client.Deleteable<CodexAccount>().Where(a => a.Id == codexAccountId).ExecuteCommandAsync(ct);

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateCodexAccounts();

        _logger.LogInformation("Codex account {Id} deprovisioned", codexAccountId);
    }

    /// <summary>
    /// 编辑 Codex 账号（当前仅支持 DisplayName），同步隐藏 Site 名称。
    /// </summary>
    public async Task UpdateAsync(Guid codexAccountId, string? displayName, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var account = (await client.Queryable<CodexAccount>()
            .Where(a => a.Id == codexAccountId)
            .ToListAsync(ct)).FirstOrDefault() ?? throw new InvalidOperationException($"Codex account {codexAccountId} not found");
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
        _metadataCache.InvalidateCodexAccounts();
    }

    /// <summary>
    /// 为指定隐藏 Site 追加模型映射（动态拉取结果复用）。已存在的 RemoteModelName 跳过。
    /// </summary>
    public async Task UpsertRemoteModelsAsync(Guid linkedSiteId, IEnumerable<(string Slug, string DisplayName)> models, CancellationToken ct)
    {
        await UpsertModelMappingsCoreAsync(linkedSiteId, models, ct);
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateCodexAccounts();
    }

    // —— 私有 ——

    /// <summary>
    /// 去重匹配（对齐 CLIProxyAPI 的身份键方案）：chatgpt_account_id 是账号身份——
    /// 同一邮箱可属于多个 workspace（如个人 plus + 组织 team），各自持有不同 account_id，
    /// 必须作为两条独立账号共存，绝不能按邮箱折叠覆盖。
    /// <para>认领规则：accountId 未命中但同邮箱恰好存在一条「无 accountId 的存量行」（老版本登录落库）时
    /// 认领该行原位更新——否则同一 workspace 重登会另建新行，存量行变成刷新必败的孤儿死凭证参与路由；
    /// 存量不唯一时不认领（避免随机合并），仍走新建。</para>
    /// </summary>
    private async Task<CodexAccount?> FindExistingAsync(string? accountId, string? email, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(accountId))
        {
            var byAccount = (await _dbContext.CodexAccounts
                .Where(a => a.AccountId == accountId)
                .ToListAsync(ct)).FirstOrDefault();
            if (byAccount != null) return byAccount;

            if (!string.IsNullOrEmpty(email))
            {
                // 拉回内存筛无身份行（账号量级小）；恰好一条才认领。
                var claimable = (await _dbContext.CodexAccounts
                    .Where(a => a.Email == email)
                    .ToListAsync(ct))
                    .Where(a => string.IsNullOrEmpty(a.AccountId))
                    .ToList();
                if (claimable.Count == 1) return claimable[0];
            }
            return null;
        }
        if (!string.IsNullOrEmpty(email))
        {
            // 拉回内存再筛（账号量级小）：只允许命中同样没有 AccountId 的存量账号。
            return (await _dbContext.CodexAccounts
                .Where(a => a.Email == email)
                .ToListAsync(ct))
                .FirstOrDefault(a => string.IsNullOrEmpty(a.AccountId));
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
                ClientEmulation = ClientEmulationConstants.CodexCli,
            });
        }
    }

    /// <summary>
    /// 默认显示名：优先自定义名；无自定义名时取邮箱（无邮箱则"Codex 账号"），
    /// 且 plan 非空时追加后缀——同邮箱多 workspace 的两条账号在列表里一眼可分。
    /// </summary>
    private static string BuildDefaultDisplayName(string? displayName, string? email, string? planType)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) return displayName;
        var baseName = string.IsNullOrWhiteSpace(email) ? "Codex 账号" : email!;
        return string.IsNullOrWhiteSpace(planType) ? baseName : $"{baseName} ({planType.Trim()})";
    }

    private static void UpdateSiteExtraHeaders(Site site, string? accountId)
    {
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            headers["Chatgpt-Account-Id"] = accountId.Trim();
        }
        site.ExtraHeadersJson = headers.Count > 0
            ? JsonSerializer.Serialize(headers, JsonSerializerPresets.Compact)
            : null;
    }
}
