using AITool.Application.Networking;
using System.Text.Json;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Domain.Codex;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Codex;

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

    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
    private readonly AccountNetworkPolicyStore? _networkPolicies;
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
        ILogger<CodexAccountProvisioner> logger,
        NetworkBoundOAuthClientFactory? networkFactory = null,
        AccountNetworkPolicyStore? networkPolicies = null)
    {
        _networkFactory = networkFactory;
        _networkPolicies = networkPolicies;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _modelCatalog = modelCatalog;
        _cascadeDeleter = cascadeDeleter;
        _logger = logger;
    }

    /// <summary>
    /// 用 token 创建或更新 Codex 账号（含隐藏 Site + 模型映射）。仅身份明确匹配时原位更新。
    /// <para>供给全程全局串行化：OAuth 回调双击/并发导入若并发执行 check-then-insert 会落重复账号，
    /// 两个刷新循环共用同一个会被轮换的 refresh_token 互相踩踏，最终该 workspace 凭证被上游永久作废。
    /// 供给仅由人工登录/导入触发（频率极低），全局单锁的代价可忽略，且彻底规避
    /// 按身份键加锁的键漂移（同一身份先无 id、后带 id 登录时 email:/acc: 键不互斥）。</para>
    /// </summary>
    private static readonly SemaphoreSlim _provisionGate = new(1, 1);

    public async Task<CodexAccount> ProvisionFromTokensAsync(CodexProvisionInput input, CancellationToken ct,
        NetworkOperationContext? networkOperation = null, bool updateNetworkPolicy = false)
    {
        await _provisionGate.WaitAsync(ct);
        try
        {
            return await ProvisionFromTokensCoreAsync(input, ct, networkOperation, updateNetworkPolicy);
        }
        finally
        {
            _provisionGate.Release();
        }
    }

    private async Task<CodexAccount> ProvisionFromTokensCoreAsync(CodexProvisionInput input, CancellationToken ct,
        NetworkOperationContext? networkOperation, bool updateNetworkPolicy)
    {
        // 用 CopyNew 独立连接做所有 DB 操作
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        CodexAccount account;
        Site site;
        client.Ado.BeginTran();
        try
        {
            // Lock publication before identity resolution; policy and credentials become visible together.
            await client.Ado.ExecuteCommandAsync("UPDATE CodexAccounts SET IsEnabled=IsEnabled WHERE 1=0;");

        // —— 去重：按 AccountId（兜底 Email）查现有账号 ——
        var existing = networkOperation?.Account is { } known && known.ProviderKey == "codex"
            ? await client.Queryable<CodexAccount>().InSingleAsync(known.AccountId)
            : await FindExistingAsync(client, input.AccountId, input.Email, input.IdToken, input.RefreshToken, ct);



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
            if (account.RequiresReauth)
            {
                account.RequiresReauth = false;
                var settings = await client.Queryable<AITool.Domain.Operations.SystemRuntimeSettings>().InSingleAsync(1);
                if (settings is { OAuthFeaturesEnabled: false })
                    account.DisabledByFeatureToggle = true;
                account.IsEnabled = !account.ManuallyDisabled && !account.DisabledByFeatureToggle && !account.IsQuotaCooling;
                site.IsEnabled = account.IsEnabled;
            }
            if (!string.IsNullOrEmpty(input.AccountId)) account.AccountId = input.AccountId;
            var previousEmail = account.Email;
            var previousPlanType = account.PlanType;
            if (!string.IsNullOrEmpty(input.Email)) account.Email = input.Email;
            if (!string.IsNullOrEmpty(input.PlanType)) account.PlanType = input.PlanType;

            site.ApiKey = input.AccessToken;
            if (!string.IsNullOrWhiteSpace(input.DisplayName))
            {
                // 用户自定义名：站点名与账号显示名同步更新，避免账号列表与调度/熔断面板名称脱节。
                site.Name = input.DisplayName;
                account.DisplayName = input.DisplayName;
                account.IsDisplayNameCustom = true;
            }
            else if (IsLegacyDefaultDisplayName(site.Name, previousEmail, previousPlanType))
            {
                if (account.IsDisplayNameCustom)
                {
                    _logger.LogInformation("Codex account {AccountId} 的历史自定义名保守保留；如需刷新默认名请在账号编辑中手动改名。", account.Id);
                }
                else
                {
                    // 使用旧身份识别默认名，以新邮箱和套餐生成升级后的名称。
                    var upgraded = BuildDefaultDisplayName(null, account.Email, account.PlanType);
                    if (!string.Equals(upgraded, site.Name, StringComparison.Ordinal))
                    {
                        site.Name = upgraded;
                        account.DisplayName = upgraded;
                        account.IsDisplayNameCustom = false;
                    }
                }
            }
            UpdateSiteExtraHeaders(site, account.AccountId ?? input.AccountId);

            // 列限定写回：供给锁只覆盖供给/改名/删除/模型映射，后台 token 刷新不持该锁——
            // 整行写回会把刷新刚写入的 token 回滚成锁内读取的旧快照。
            await client.Updateable(account).UpdateColumns(a => new
            {
                a.AccessToken, a.RefreshToken, a.IdToken, a.TokenExpiresAt, a.LastRefreshAt, a.RequiresReauth, a.IsEnabled, a.DisabledByFeatureToggle,
                a.AccountId, a.Email, a.PlanType, a.DisplayName, a.IsDisplayNameCustom,
            }).ExecuteCommandAsync(ct);
            await client.Updateable(site).UpdateColumns(s => new { s.ApiKey, s.Name, s.ExtraHeadersJson, s.IsEnabled }).ExecuteCommandAsync(ct);
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
                IsDisplayNameCustom = !string.IsNullOrWhiteSpace(input.DisplayName),
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

        if (updateNetworkPolicy)
        {
            if (_networkPolicies is null || networkOperation is null)
                throw new NetworkPolicyException("network_policy_unavailable", "Network policy persistence is unavailable.");
            await _networkPolicies.SetInTransactionAsync(client, new AccountIdentity("codex", account.Id),
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

        // 模型由用户手动选择导入；重新登录也不修改既有映射。

        // —— 失效缓存（一次性）——
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateCodexAccounts();

        _logger.LogInformation("Codex account {Id} provisioned (site {SiteId})", account.Id, site.Id);
        return account;
    }

    /// <summary>
    /// 级联删除 Codex 账号：删 CodexAccount + 隐藏 Site（含映射/路由/空壳入口）。
    /// 复用供给全局锁：防供给持读快照期间删除完成、为已删 Site 追加孤儿映射。
    /// </summary>
    public async Task DeprovisionAsync(Guid codexAccountId, CancellationToken ct)
    {
        await _provisionGate.WaitAsync(ct);
        try
        {
            await DeprovisionCoreAsync(codexAccountId, ct);
        }
        finally
        {
            _provisionGate.Release();
        }
    }

    private async Task DeprovisionCoreAsync(Guid codexAccountId, CancellationToken ct)
    {
        await _cascadeDeleter.ExecuteGraphMutationAsync(async db =>
        {
            var client = db.Client;
            var account = await client.Queryable<CodexAccount>().InSingleAsync(codexAccountId);
            if (account is null) throw new InvalidOperationException("Codex account not found");
            if (_networkPolicies is not null)
            {
                await client.Ado.ExecuteCommandAsync("UPDATE AccountNetworkPolicies SET Revision=Revision WHERE 1=0;");
                await _networkPolicies.DeleteInTransactionAsync(client, new AccountIdentity("codex", account.Id), cancellationToken: ct);
            }
            if (account.LinkedSiteId != Guid.Empty)
                await new SiteCascadeDeleter(db).RemoveSitesInGraphMutationAsync([account.LinkedSiteId], ct);
            await client.Deleteable<CodexAccount>().Where(a => a.Id == codexAccountId).ExecuteCommandAsync(ct);
            return true;
        }, ct, _metadataCache);
        _metadataCache.InvalidateCodexAccounts();
    }

    /// <summary>
    /// 编辑 Codex 账号（当前仅支持 DisplayName），同步隐藏 Site 名称。
    /// 复用供给全局锁：防用户改名被供给的旧实体快照整行覆盖（丢更新）。
    /// </summary>
    public async Task UpdateAsync(Guid codexAccountId, string? displayName, CancellationToken ct)
    {
        await _provisionGate.WaitAsync(ct);
        try
        {
            await UpdateCoreAsync(codexAccountId, displayName, ct);
        }
        finally
        {
            _provisionGate.Release();
        }
    }

    private async Task UpdateCoreAsync(Guid codexAccountId, string? displayName, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var account = (await client.Queryable<CodexAccount>()
            .Where(a => a.Id == codexAccountId)
            .ToListAsync(ct)).FirstOrDefault() ?? throw new InvalidOperationException($"Codex account {codexAccountId} not found");
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            account.DisplayName = displayName;
            // 用户改名即视为自定义名：此后供给的默认名升级逻辑不再触碰该名。
            account.IsDisplayNameCustom = true;
            var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.Name = displayName;
                // 列限定写回（同供给）：防把后台刷新刚写入的 token 整行回滚。
                await client.Updateable(site).UpdateColumns(s => s.Name).ExecuteCommandAsync(ct);
            }
            await client.Updateable(account).UpdateColumns(a => new { a.DisplayName, a.IsDisplayNameCustom }).ExecuteCommandAsync(ct);
        }

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateCodexAccounts();
    }

    /// <summary>
    /// 为指定隐藏 Site 追加模型映射（动态拉取结果复用）。已存在的 RemoteModelName 跳过。
    /// 复用供给全局锁：与供给的同 Site 写入互斥。
    /// </summary>
    public async Task UpsertRemoteModelsAsync(Guid linkedSiteId, IEnumerable<(string Slug, string DisplayName)> models, CancellationToken ct)
    {
        await _provisionGate.WaitAsync(ct);
        try
        {
            await UpsertModelMappingsCoreAsync(linkedSiteId, models, ct);
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateModelMetadata();
            _metadataCache.InvalidateCodexAccounts();
        }
        finally
        {
            _provisionGate.Release();
        }
    }

    /// <summary>
    /// 仅对源账号应用选择；同套餐的其他账号权限无法推断，跳过并报告风险。
    /// </summary>
    public async Task<List<CodexBatchApplyResult>> BatchUpsertRemoteModelsAsync(
        Guid sourceAccountId,
        IReadOnlyList<(string RemoteModelName, string DisplayName)> models,
        CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var allAccounts = await client.Queryable<CodexAccount>().ToListAsync(ct);
        var source = allAccounts.FirstOrDefault(a => a.Id == sourceAccountId)
            ?? throw new InvalidOperationException($"Codex account {sourceAccountId} not found");

        // 保留原有分组以逐项报告跳过的账号；套餐不能证明模型权限一致。
        var groupMembers = allAccounts
            .Where(a => string.Equals(a.PlanType, source.PlanType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var results = new List<CodexBatchApplyResult>(groupMembers.Count);
        foreach (var member in groupMembers)
        {
            if (member.Id != source.Id)
            {
                results.Add(new CodexBatchApplyResult(member.Id, member.DisplayName, false, "无法验证该账号的模型权限，已跳过；请逐账号拉取并同步模型", null));
                continue;
            }
            try
            {
                var site = await client.Queryable<Site>().InSingleAsync(member.LinkedSiteId);
                if (site == null)
                {
                    results.Add(new CodexBatchApplyResult(member.Id, member.DisplayName, false, "关联站点缺失", null));
                    continue;
                }

                await UpsertRemoteModelsAsync(member.LinkedSiteId, models, ct);
                results.Add(new CodexBatchApplyResult(member.Id, member.DisplayName, true, null, models.Count));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(new CodexBatchApplyResult(member.Id, member.DisplayName, false, ex.Message, null));
            }
        }

        return results;
    }

    // —— 私有 ——

    /// <summary>
    /// 去重匹配（对齐 CLIProxyAPI 的凭证注册键 hash8(account_id)+email）：身份键是
    /// (chatgpt_account_id, email) 复合——chatgpt_account_id 标识 workspace 而非用户，
    /// 同一邮箱可属于多个 workspace（各自 account_id 不同），同一 team workspace 的多个成员
    /// （不同邮箱）又共享同一个 account_id。任一维度单键折叠都会互相覆盖：
    /// 按邮箱折叠吞掉多 workspace，按 account_id 折叠吞掉同 workspace 的其它成员。
    /// <para>旧行缺少 workspace ID 时，只能凭相同的非空刷新凭证唯一确认归属；
    /// 无法确认时保留旧行并提示人工处理，避免覆盖其他 workspace 或产生重复刷新循环。</para>
    /// </summary>
    private static async Task<CodexAccount?> FindExistingAsync(SqlSugar.ISqlSugarClient client, string? accountId, string? email, string? idToken, string? refreshToken, CancellationToken ct)
    {
        var acc = NormalizeKey(accountId);
        var mail = NormalizeEmail(email);
        // Email 列在 SQLite 中按 BINARY 比较；内存归一才能匹配保留原始大小写的存量行。
        var rows = await client.Queryable<CodexAccount>().ToListAsync(ct);

        if (acc != null)
        {
            // 同一 workspace 内仍须按成员身份区分凭证。
            // 无邮箱时改用 id_token 中的用户 ID；无法唯一确定身份时不合并。
            var sameAccount = rows.Where(a => NormalizeKey(a.AccountId) == acc).ToList();
            if (mail == null)
            {
                // workspace ID 不能标识成员；旧凭证也必须有相同的稳定用户 ID 才能安全更新。
                var userId = NormalizeKey(CodexJwtParser.Parse(idToken)?.UserId);
                if (userId == null) return null;
                var matches = sameAccount
                    .Where(a => NormalizeKey(CodexJwtParser.Parse(a.IdToken)?.UserId) == userId)
                    .ToList();
                return matches.Count == 1 ? matches[0] : null;
            }

            var exact = sameAccount.OrderBy(a => a.CreatedAt).FirstOrDefault(a => NormalizeEmail(a.Email) == mail);
            if (exact != null) return exact;

            if (mail != null)
            {
                var legacy = rows.Where(a => NormalizeKey(a.AccountId) == null && NormalizeEmail(a.Email) == mail).ToList();
                if (legacy.Count > 0)
                {
                    // 相同刷新凭证可证明是同一授权；邮箱或用户 ID 不能证明 workspace。
                    var matches = legacy.Where(a => !string.IsNullOrWhiteSpace(refreshToken)
                        && string.Equals(a.RefreshToken, refreshToken, StringComparison.Ordinal)).ToList();
                    if (matches.Count == 1) return matches[0];
                    throw new CodexWorkspaceRequiredException(true);
                }
            }
            return null;
        }
        if (mail != null)
        {
            // 已存在带 workspace ID 的账号时，无法确定无 ID 凭证属于哪个 workspace；
            // 即使还有旧的无 ID 行也不能更新，否则可能让同一凭证进入多个刷新循环。
            var sameEmail = rows.Where(a => NormalizeEmail(a.Email) == mail).ToList();
            if (sameEmail.Any(a => NormalizeKey(a.AccountId) != null))
                throw new CodexWorkspaceRequiredException();
            return sameEmail.FirstOrDefault(a => NormalizeKey(a.AccountId) == null);
        }
        return null;
    }

    /// <summary>身份键归一：空白视为无值，其余去首尾空白（对齐 CPA 文件名的 TrimSpace）。</summary>
    private static string? NormalizeKey(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>邮箱身份键归一：空白视为无值；去首尾空白并整串按不变文化小写。
    /// 域名大小写不敏感（RFC 5321 §2.3.5）；本地部分技术上有大小写语义，但主流 IdP 下发的
    /// email claim 一律按大小写不敏感标识处理，且 GoogleAccountProvisioner.FindExistingAsync
    /// 已按 OrdinalIgnoreCase 判重。若区分本地部分，同一凭证大小写变化重登即被判为新身份、
    /// 另建一行并进入第二个刷新循环。仅用于比较，落库保留 IdP 原始大小写（展示/对齐 CPA 凭证名）。</summary>
    private static string? NormalizeEmail(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

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

    /// <summary>
    /// 仅匹配历史默认名白名单：旧邮箱或兜底名，可带与旧套餐一致的后缀。
    /// 任意其他后缀均保留；即使命中白名单，自定义标记仍可阻止重命名。
    /// </summary>
    private static bool IsLegacyDefaultDisplayName(string siteName, string? email, string? planType)
    {
        if (string.IsNullOrWhiteSpace(siteName)) return false;
        if (string.Equals(siteName, BuildDefaultDisplayName(null, email, planType), StringComparison.OrdinalIgnoreCase)
            || string.Equals(siteName, BuildDefaultDisplayName(null, email, null), StringComparison.OrdinalIgnoreCase)
            || string.Equals(siteName, "Codex 账号", StringComparison.OrdinalIgnoreCase)
            || string.Equals(siteName, "OAuth 账号", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(planType)) return false;
        var plan = planType.Trim();
        return string.Equals(siteName, $"Codex 账号 ({plan})", StringComparison.OrdinalIgnoreCase)
            || string.Equals(siteName, $"OAuth 账号 ({plan})", StringComparison.OrdinalIgnoreCase);
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

/// <summary>同邮箱已有 workspace 时，缺少 workspace ID 的凭证不能安全地供给。</summary>
public sealed class CodexWorkspaceRequiredException(bool ambiguousLegacy = false) : InvalidOperationException(
    ambiguousLegacy
        ? "同邮箱存在无法确认 workspace 归属的旧 Codex 账号，已保留旧记录并停止供给；请人工核实并处理旧账号后重试。"
        : "该邮箱已有 Codex workspace，凭证缺少 workspace ID；请使用包含 workspace ID 的凭证重新登录或导入。")
{
}

/// <summary>批量应用模型选择的逐账号结果（同套餐组）。</summary>
public sealed record CodexBatchApplyResult(
    Guid AccountId,
    string DisplayName,
    bool Ok,
    string? Error,
    int? ImportedCount);
