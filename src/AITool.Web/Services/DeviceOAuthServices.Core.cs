using System.Collections.Concurrent;
using AITool.Application.OAuth;
using AITool.Application.Networking;
using AITool.Domain.Operations;
using AITool.Domain.OAuth;
using AITool.Domain.Sites;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.Xai;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 通用轮询式 OAuth 厂商注册表：按 VendorKey 解析厂商定义（DI 注册顺序即优先级）。
/// </summary>
public sealed class DeviceOAuthVendorRegistry
{
    private readonly ConcurrentDictionary<string, IDeviceOAuthVendor> _vendors = new(StringComparer.OrdinalIgnoreCase);

    public DeviceOAuthVendorRegistry(IEnumerable<IDeviceOAuthVendor> vendors)
    {
        foreach (var vendor in vendors)
        {
            _vendors[vendor.VendorKey] = vendor;
        }
    }

    public IDeviceOAuthVendor? Find(string? vendorKey)
        => string.IsNullOrWhiteSpace(vendorKey) ? null : _vendors.GetValueOrDefault(vendorKey);

    public IReadOnlyList<IDeviceOAuthVendor> All => [.. _vendors.Values];
}

/// <summary>
/// 通用 OAuth 账号供给工厂：把登录完成的 token 转换为「隐藏 Site + DeviceOAuthAccount + 模型映射」。
/// 站点协议/请求头来自厂商定义的 ChatSiteConfig；不支持聊天转发的厂商（ChatSiteConfig=null）
/// 只建账号不建站点（额度托管可用）。
/// </summary>
public sealed class DeviceOAuthAccountProvisioner
{
    public const string ManagedSource = "device_oauth";

    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly SiteCascadeDeleter _cascadeDeleter;
    private readonly ILogger<DeviceOAuthAccountProvisioner> _logger;
    private readonly IAccountNetworkPolicyStore? _networkPolicies;
    private static readonly KeyedAsyncLock ProvisionLocks = new();

    public DeviceOAuthAccountProvisioner(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        SiteCascadeDeleter cascadeDeleter,
        ILogger<DeviceOAuthAccountProvisioner> logger,
        IAccountNetworkPolicyStore? networkPolicies = null)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _cascadeDeleter = cascadeDeleter;
        _logger = logger;
        _networkPolicies = networkPolicies;
    }

    public async Task<DeviceOAuthAccount> ProvisionAsync(
        IDeviceOAuthVendor vendor,
        string? displayName,
        string? email,
        string? externalUserId,
        DeviceOAuthTokenSet tokens,
        string? extraJson,
        CancellationToken ct,
        bool isUserSuppliedName = false,
        AccountNetworkSelection? networkSelection = null,
        long? expectedProfileRevision = null)
    {
        if (networkSelection is not null && _networkPolicies is not AccountNetworkPolicyStore)
            throw new NetworkPolicyException("network_policy_unavailable", "Transactional account network policy storage is unavailable.");
        // Serialize deduplication through publication, including concurrent sessions for one vendor.
        using var provisionLock = await ProvisionLocks.WaitAsync(vendor.VendorKey.ToLowerInvariant(), ct);
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        // Never wait for the credential gate while holding the SQLite writer. The vendor
        // provision lease keeps the dedup probe stable against other provisioning calls;
        // deletion can still win, so the authoritative lookup is repeated in the transaction.
        var probe = await FindExistingAsync(client, vendor.VendorKey, email, externalUserId, ct);
        using var credentialLease = probe is null ? null : await DeviceOAuthRefreshGate.AcquireAsync(probe.Id, ct);
        client.Ado.BeginTran();
        try
        {
        // Take the SQLite write lock before reading dedup/policy state (no read-to-write upgrade).
        await client.Ado.ExecuteCommandAsync("UPDATE DeviceOAuthAccounts SET UpdatedAt=UpdatedAt WHERE 1=0;");
        var existing = await FindExistingAsync(client, vendor.VendorKey, email, externalUserId, ct);
        if (existing is not null && existing.Id != probe?.Id)
            throw new InvalidOperationException("Device OAuth account changed during provisioning; retry login.");
        DeviceOAuthAccount account;
        Site? site = null;

        var label = string.IsNullOrWhiteSpace(displayName)
            ? email ?? $"{vendor.Label} 账号"
            : displayName!.Trim();

        // 名称策略（对齐 CodexAccountProvisioner 的 IsDisplayNameCustom 语义）：
        // 用户自定义名（登录时命名/事后改名）绝不因重登被上游昵称/邮箱冲掉；
        // 非自定义默认名跟随上游刷新（上游改名要能在面板看到）。
        bool renameOnProvision;
        if (existing is null)
        {
            renameOnProvision = true;
        }
        else if (isUserSuppliedName)
        {
            renameOnProvision = !string.Equals(existing.DisplayName, label, StringComparison.Ordinal)
                || !existing.IsDisplayNameCustom;
        }
        else
        {
            renameOnProvision = !existing.IsDisplayNameCustom
                && !string.IsNullOrWhiteSpace(displayName)
                && !string.Equals(existing.DisplayName, label, StringComparison.Ordinal);
        }

        if (existing is not null)
        {
            account = existing;
            site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            account.AccessToken = tokens.AccessToken;
            if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) account.RefreshToken = tokens.RefreshToken;
            account.TokenExpiresAt = tokens.ExpiresAt;
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            if (account.RequiresReauth)
            {
                account.RequiresReauth = false;
                var settings = await client.Queryable<SystemRuntimeSettings>().InSingleAsync(1);
                if (settings is { OAuthFeaturesEnabled: false })
                    account.DisabledByFeatureToggle = true;
                account.IsEnabled = !account.ManuallyDisabled && !account.DisabledByFeatureToggle && !account.IsDeleted;
            }
            if (!string.IsNullOrEmpty(email)) account.Email = email;
            if (!string.IsNullOrEmpty(externalUserId)) account.ExternalUserId = externalUserId;
            if (!string.IsNullOrWhiteSpace(extraJson)) account.ExtraJson = extraJson;
            if (renameOnProvision)
            {
                account.DisplayName = label;
                account.IsDisplayNameCustom = isUserSuppliedName;
            }
            account.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            account = new DeviceOAuthAccount
            {
                VendorKey = vendor.VendorKey,
                DisplayName = label,
                IsDisplayNameCustom = isUserSuppliedName,
                Email = email,
                ExternalUserId = externalUserId,
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                TokenExpiresAt = tokens.ExpiresAt,
                LastRefreshAt = DateTimeOffset.UtcNow,
                ExtraJson = extraJson,
                LinkedSiteId = Guid.Empty, // 下方创建站点后回填
                IsEnabled = true,
            };
        }

        // 聊天站点：协议与请求头按厂商定义；无 ChatSiteConfig 的厂商跳过（纯额度托管）。
        if (vendor.ChatSiteConfig is { } chat)
        {

            // 厂商在 ExtraJson 里给 chatBaseUrl 时覆盖默认基地址（如 Copilot 个人版端点）。
            var chatBaseUrl = TryGetExtraString(extraJson, "chatBaseUrl");
            var mergedHeadersJson = ComputeChatHeadersJson(chat, extraJson);
            if (site is null)
            {
                site = new Site
                {
                    Name = label,
                    BaseUrl = chatBaseUrl ?? chat.BaseUrl,
                    // 基地址已含版本段（如 …/v1 或 …/api/anthropic），路径解析用 versioned-base。
                    EndpointPathMode = "versioned-base",
                    ApiKey = tokens.AccessToken,
                    SupportsOpenAi = chat.SupportsOpenAi,
                    SupportsAnthropic = chat.SupportsAnthropic,
                    SupportsResponses = chat.SupportsResponses,
                    ProtocolType = chat.ProtocolType,
                    ManagedSource = ManagedSource,
                    ExtraHeadersJson = mergedHeadersJson,
                    IsEnabled = true,
                };
                await client.Insertable(site).ExecuteCommandAsync(ct);
                account.LinkedSiteId = site.Id;
            }
            else
            {
                site.ApiKey = tokens.AccessToken;
                // 站点名与账号名同步：仅在账号名本轮变更时跟随（自定义名保持时站点名不动）。
                if (renameOnProvision) site.Name = label;
                site.BaseUrl = chatBaseUrl ?? chat.BaseUrl;
                site.ExtraHeadersJson = mergedHeadersJson;
                site.IsEnabled = account.IsEnabled;
                await client.Updateable(site).ExecuteCommandAsync(ct);
            }
        }

        if (existing is null)
        {
            await client.Insertable(account).ExecuteCommandAsync(ct);
        }
        else
        {
            await client.Updateable(account).ExecuteCommandAsync(ct);
        }

        // 模型由用户手动选择导入；供给和重新登录不修改既有映射。

        // Missing selection on relogin preserves existing policy; an explicit Legacy selection
        // is still an update. New unspecified accounts inherit Legacy without a policy row.
        if (networkSelection is not null)
            await ((AccountNetworkPolicyStore)_networkPolicies!).SetInTransactionAsync(client,
                NetworkBoundDeviceOAuthVendorFactory.Identity(account.Id), networkSelection,
                expectedProfileRevision: expectedProfileRevision, cancellationToken: ct);
        ct.ThrowIfCancellationRequested();
        client.Ado.CommitTran();
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        return account;
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
    }

    public async Task ToggleAsync(Guid accountId, bool isEnabled, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        try
        {
        await client.Ado.ExecuteCommandAsync("UPDATE DeviceOAuthAccounts SET UpdatedAt=UpdatedAt WHERE 1=0;");
        var account = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(accountId);
        if (account == null) { await client.Ado.CommitTranAsync(); return; }

        account.ManuallyDisabled = !isEnabled;
        account.IsEnabled = isEnabled && !account.RequiresReauth && !account.DisabledByFeatureToggle && !account.IsDeleted;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await client.Updateable(account).UpdateColumns(a => new { a.IsEnabled, a.ManuallyDisabled, a.UpdatedAt }).ExecuteCommandAsync(ct);

        if (account.LinkedSiteId != Guid.Empty)
        {
            var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.IsEnabled = account.IsEnabled;
                await client.Updateable(site).UpdateColumns(s => new { s.IsEnabled }).ExecuteCommandAsync(ct);
            }
        }

        await client.Ado.CommitTranAsync();
        }
        catch { await client.Ado.RollbackTranAsync(); throw; }
        _metadataCache.InvalidateRouteTargets();
    }

    public async Task UpdateAsync(Guid accountId, string? displayName, string? refreshToken, CancellationToken ct)
    {
        using var credentialLease = await DeviceOAuthRefreshGate.AcquireAsync(accountId, ct);
        using var client = _dbContext.Client.CopyNew();
        var account = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(accountId)
            ?? throw new KeyNotFoundException("账号不存在");
        var requestedName = displayName?.Trim();
        var isRename = !string.IsNullOrWhiteSpace(requestedName) && !string.Equals(requestedName, account.DisplayName, StringComparison.Ordinal);
        var hasRefreshToken = !string.IsNullOrWhiteSpace(refreshToken);
        if (!isRename && !hasRefreshToken) return;

        if (isRename)
        {
            account.DisplayName = requestedName!;
            // 用户改名即视为自定义名：此后供给的上游昵称刷新不再触碰该名。
            account.IsDisplayNameCustom = true;
        }
        account.UpdatedAt = DateTimeOffset.UtcNow;
        if (hasRefreshToken)
        {
            account.RefreshToken = refreshToken!.Trim();
        }

        if (isRename && hasRefreshToken)
            await client.Updateable(account)
                .UpdateColumns(a => new { a.DisplayName, a.IsDisplayNameCustom, a.RefreshToken, a.UpdatedAt })
                .ExecuteCommandAsync(ct);
        else if (isRename)
            await client.Updateable(account)
                .UpdateColumns(a => new { a.DisplayName, a.IsDisplayNameCustom, a.UpdatedAt })
                .ExecuteCommandAsync(ct);
        else
            await client.Updateable(account)
                .UpdateColumns(a => new { a.RefreshToken, a.UpdatedAt })
                .ExecuteCommandAsync(ct);
        // A refresh-token edit cannot clear a forwarding 401, which disabled the account.
        if (hasRefreshToken)
            await client.Updateable<DeviceOAuthAccount>()
                .SetColumns(a => new DeviceOAuthAccount { RequiresReauth = false })
                .Where(a => a.Id == accountId && a.IsEnabled && a.RefreshToken == account.RefreshToken)
                .ExecuteCommandAsync(ct);
        if (isRename && account.LinkedSiteId != Guid.Empty)
        {
            var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.Name = account.DisplayName;
                await client.Updateable(site).UpdateColumns(s => new { s.Name }).ExecuteCommandAsync(ct);
            }
        }

        _metadataCache.InvalidateRouteTargets();
    }

    public async Task DeleteAsync(Guid accountId, CancellationToken ct)
    {
        using var credentialLease = await DeviceOAuthRefreshGate.AcquireAsync(accountId, ct);
        await _cascadeDeleter.ExecuteGraphMutationAsync(async db =>
        {
            // Cascade and policy/account removal share the graph owner's client and transaction.
            var client = db.Client;
            var account = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(accountId);
            if (account is null) return false;
            if (account.LinkedSiteId != Guid.Empty)
                await new SiteCascadeDeleter(db).RemoveSitesInGraphMutationAsync([account.LinkedSiteId], ct);
            await client.Deleteable<AccountNetworkPolicy>()
                .Where(p => p.ProviderKey == DeviceOAuthQuotaService.ProviderKey && p.AccountId == accountId)
                .ExecuteCommandAsync(ct);
            await client.Deleteable<DeviceOAuthAccount>().Where(a => a.Id == accountId).ExecuteCommandAsync(ct);
            return true;
        }, ct, _metadataCache);
    }

    /// <summary>
    /// 站点自定义头 = 厂商 ChatSiteConfig.ExtraHeaders + ExtraJson.chatHeaders（每账号稳定头，后者优先）。
    /// 供给与启动时的托管站点头同步共用，保证两处产出一致。
    /// </summary>
    internal static string ComputeChatHeadersJson(DeviceOAuthChatSiteConfig chat, string? extraJson)
    {
        var effectiveHeaders = new Dictionary<string, string>(chat.ExtraHeaders, StringComparer.OrdinalIgnoreCase);
        if (TryGetExtraObject(extraJson, "chatHeaders") is { } perAccount)
        {
            foreach (var property in perAccount.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(property.Name) && property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    effectiveHeaders[property.Name] = property.Value.GetString()!;
                }
            }
        }

        // 仅当厂商声明了域身份头映射（如 WorkBuddy 的 X-Domain）、目标头未显式配置且 extra.domain 为非空字符串时兜底回填。
        if (!string.IsNullOrWhiteSpace(chat.DomainIdentityHeaderName)
            && !effectiveHeaders.ContainsKey(chat.DomainIdentityHeaderName)
            && TryGetExtraString(extraJson, "domain") is { } domain && !string.IsNullOrWhiteSpace(domain))
        {
            effectiveHeaders[chat.DomainIdentityHeaderName] = domain;
        }

        return System.Text.Json.JsonSerializer.Serialize(
            effectiveHeaders, AITool.Application.Common.JsonSerializerPresets.Compact);
    }

    /// <summary>读取厂商 ExtraJson 的对象字段（如 chatHeaders）。</summary>
    private static System.Text.Json.JsonElement? TryGetExtraObject(string? extraJson, string name)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try
        {
            var root = System.Text.Json.JsonDocument.Parse(extraJson).RootElement;
            return root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Object
                ? value.Clone()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? TryGetExtraString(string? extraJson, string name)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try
        {
            var root = System.Text.Json.JsonDocument.Parse(extraJson).RootElement;
            return root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 按用户选择同步关联站点的模型映射：选中的建立/启用，未选中的既有映射禁用。
    /// Slug 为上游模型 ID；无关联站点（纯额度托管）时抛业务异常。
    /// </summary>
    public async Task<int> SyncSelectedModelsAsync(
        Guid accountId,
        IReadOnlyList<(string Slug, string DisplayName, bool Selected)> models,
        CancellationToken ct)
    {
        // 一个上游模型只处理一次，重复项以最后一次选择为准；空清单不能隐式改变映射。
        var selections = models
            .Where(m => !string.IsNullOrWhiteSpace(m.Slug))
            .Select(m => (Slug: m.Slug.Trim(),
                PublicName: string.IsNullOrWhiteSpace(m.DisplayName) ? m.Slug.Trim() : m.DisplayName.Trim(),
                m.Selected))
            .GroupBy(m => m.Slug, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var last = g.Last();
                return (Slug: g.First().Slug.Trim(), last.PublicName, last.Selected);
            })
            .ToList();
        if (selections.Count == 0) throw new ArgumentException("未收到模型清单", nameof(models));

        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        client.Ado.BeginTran();
        var changed = 0;
        try
        {
            var account = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(accountId);
            if (account is null || account.IsDeleted) throw new KeyNotFoundException("账号不存在");
            if (account.LinkedSiteId == Guid.Empty
                || await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId) is null)
            {
                throw new InvalidOperationException("该账号无关联聊天站点，模型目录仅供查看");
            }

            var existingMappings = await client.Queryable<Domain.SiteCatalog.SiteModelMapping>()
                .Where(m => m.SiteId == account.LinkedSiteId)
                .ToListAsync(ct);
            var existingByName = existingMappings.ToLookup(m => m.RemoteModelName, StringComparer.OrdinalIgnoreCase);
            var publicNames = selections.Where(m => m.Selected).Select(m => m.PublicName).Distinct().ToList();
            var items = publicNames.Count == 0
                ? new List<Domain.Models.ModelLibraryItem>()
                : await client.Queryable<Domain.Models.ModelLibraryItem>()
                    .Where(m => publicNames.Contains(m.ModelName)).ToListAsync(ct);
            var itemsByName = items.ToDictionary(m => m.ModelName, StringComparer.Ordinal);
            var newItems = new List<Domain.Models.ModelLibraryItem>();
            var itemsToEnable = new List<Domain.Models.ModelLibraryItem>();
            var itemsToEnableIds = new HashSet<Guid>();
            var newMappings = new List<Domain.SiteCatalog.SiteModelMapping>();
            var updates = new List<Domain.SiteCatalog.SiteModelMapping>();

            foreach (var (slug, publicName, selected) in selections)
            {
                var mappings = existingByName[slug].ToList();
                Domain.Models.ModelLibraryItem? item = null;
                if (selected && !itemsByName.TryGetValue(publicName, out item))
                {
                    // 对外模型名是路由标识 ModelName，不是共享模型库项的展示文本。
                    item = new Domain.Models.ModelLibraryItem { ModelName = publicName, DisplayName = publicName };
                    itemsByName.Add(publicName, item);
                    newItems.Add(item);
                }
                else if (selected && item is { IsEnabled: false })
                {
                    item.IsEnabled = true;
                    if (itemsToEnableIds.Add(item.Id)) itemsToEnable.Add(item);
                }

                if (selected && mappings.Count == 0)
                {
                    newMappings.Add(new Domain.SiteCatalog.SiteModelMapping
                    {
                        SiteId = account.LinkedSiteId,
                        ModelLibraryItemId = item!.Id,
                        RemoteModelName = slug,
                    });
                }
                foreach (var mapping in mappings)
                {
                    if (mapping.IsEnabled == selected && (!selected || mapping.ModelLibraryItemId == item!.Id)) continue;
                    mapping.IsEnabled = selected;
                    if (selected) mapping.ModelLibraryItemId = item!.Id;
                    updates.Add(mapping);
                }
            }

            if (newItems.Count > 0) await client.Insertable(newItems).ExecuteCommandAsync(ct);
            if (itemsToEnable.Count > 0)
            {
                await client.Updateable(itemsToEnable).UpdateColumns(item => new { item.IsEnabled }).ExecuteCommandAsync(ct);
            }
            if (newMappings.Count > 0) await client.Insertable(newMappings).ExecuteCommandAsync(ct);
            if (updates.Count > 0)
            {
                await client.Updateable(updates).UpdateColumns(m => new { m.IsEnabled, m.ModelLibraryItemId }).ExecuteCommandAsync(ct);
            }
            changed = newMappings.Count + updates.Count + itemsToEnable.Count;
            ct.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }

        if (changed > 0)
        {
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateModelMetadata();
        }
        return changed;
    }
    private static async Task<DeviceOAuthAccount?> FindExistingAsync(
        SqlSugar.ISqlSugarClient client, string vendorKey, string? email, string? externalUserId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(externalUserId))
        {
            var byUser = await client.Queryable<DeviceOAuthAccount>()
                .FirstAsync(a => !a.IsDeleted && a.VendorKey == vendorKey && a.ExternalUserId == externalUserId, ct);
            if (byUser != null) return byUser;
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var byEmail = await client.Queryable<DeviceOAuthAccount>()
                .FirstAsync(a => !a.IsDeleted && a.VendorKey == vendorKey && a.Email == email, ct);
            if (byEmail != null) return byEmail;
        }

        return null;
    }

    private async Task UpsertDefaultModelMappingsAsync(
        SqlSugar.ISqlSugarClient client, Guid siteId, IDeviceOAuthVendor vendor, CancellationToken ct)
    {
        var existing = await client.Queryable<Domain.SiteCatalog.SiteModelMapping>()
            .Where(m => m.SiteId == siteId)
            .ToListAsync(ct);
        var mapped = new HashSet<string>(existing.Select(m => m.RemoteModelName), StringComparer.OrdinalIgnoreCase);
        var toInsert = new List<Domain.SiteCatalog.SiteModelMapping>();

        foreach (var (slug, display) in vendor.DefaultModels)
        {
            if (string.IsNullOrWhiteSpace(slug) || !mapped.Add(slug)) continue;
            var item = await client.Queryable<Domain.Models.ModelLibraryItem>().FirstAsync(m => m.ModelName == slug, ct);
            if (item is null)
            {
                item = new Domain.Models.ModelLibraryItem
                {
                    ModelName = slug,
                    DisplayName = string.IsNullOrWhiteSpace(display) ? slug : display,
                    IsEnabled = true,
                };
                await client.Insertable(item).ExecuteCommandAsync(ct);
            }

            toInsert.Add(new Domain.SiteCatalog.SiteModelMapping
            {
                SiteId = siteId,
                ModelLibraryItemId = item.Id,
                RemoteModelName = slug,
                IsEnabled = true,
            });
        }

        if (toInsert.Count > 0)
        {
            await client.Insertable(toInsert).ExecuteCommandAsync(ct);
        }
    }
}

/// <summary>
/// 通用 OAuth token 后台刷新服务（泛型基座，四14）：周期扫描临期账号，按厂商定义刷新，
/// 写回账号与隐藏 Site 的 ApiKey。上游明确拒绝（invalid_grant）标记 RequiresReauth 并退避。
/// token 不可取消段原则与 CodexCredentialRefreshService 一致：刷新发出后即使扫描轮被取消也完成写回。
/// </summary>
public sealed class DeviceOAuthTokenRefreshService : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<DeviceOAuthTokenRefreshService> _logger;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _retryAt = [];

    public DeviceOAuthTokenRefreshService(IServiceProvider services, ILogger<DeviceOAuthTokenRefreshService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动时把厂商 ChatSiteConfig 当前定义的请求头同步到既有托管站点：
        // 升级引入的新头（如 WorkBuddy 强制流式标记）无需重新登录即可生效。
        try
        {
            await SyncManagedSiteHeadersAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 关停中退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device OAuth managed site header sync error");
        }

        try
        {
            await NormalizeLegacyDefaultNamesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 关停中退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device OAuth legacy default name normalization error");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDueAccountsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device OAuth token refresh loop error");
            }

            try
            {
                await Task.Delay(ScanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 将厂商 ChatSiteConfig 当前定义和账号级 chatHeaders 合并到站点，保留其他已有头。
    /// 头变更只来自代码升级（进程内配置静态，启动执行一次即可）；只回写有差异的站点，
    /// 非托管（ManagedSource 不符）或用户自建站点不触碰。
    /// </summary>
    internal async Task SyncManagedSiteHeadersAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DeviceOAuthVendorRegistry>();
        var metadataCache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        var accounts = await db.DeviceOAuthAccounts
            .Where(a => !a.IsDeleted && a.LinkedSiteId != Guid.Empty)
            .ToListAsync(ct);
        if (accounts.Count == 0) return;

        var changed = false;
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            if (registry.Find(account.VendorKey)?.ChatSiteConfig is not { } chat) continue;

            try
            {
                var expected = DeviceOAuthAccountProvisioner.ComputeChatHeadersJson(chat, account.ExtraJson);
                var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
                if (site is null || site.ManagedSource != DeviceOAuthAccountProvisioner.ManagedSource)
                    continue;

                // HTTP header names are case-insensitive. Keep every unrelated existing value,
                // but let the current vendor/account values win for managed names.
                var headers = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(site.ExtraHeadersJson))
                {
                    using var existing = System.Text.Json.JsonDocument.Parse(site.ExtraHeadersJson);
                    foreach (var property in existing.RootElement.EnumerateObject())
                        headers.Add(property.Name, property.Value.Clone());
                }

                var headersChanged = false;
                using var managed = System.Text.Json.JsonDocument.Parse(expected);
                foreach (var property in managed.RootElement.EnumerateObject())
                {
                    if (!headers.TryGetValue(property.Name, out var current)
                        || current.ValueKind != System.Text.Json.JsonValueKind.String
                        || current.GetString() != property.Value.GetString())
                    {
                        headers[property.Name] = property.Value.Clone();
                        headersChanged = true;
                    }
                }
                if (!headersChanged) continue;

                site.ExtraHeadersJson = System.Text.Json.JsonSerializer.Serialize(
                    headers, AITool.Application.Common.JsonSerializerPresets.Compact);
                await db.Client.Updateable(site).UpdateColumns(s => new { s.ExtraHeadersJson }).ExecuteCommandAsync(ct);
                changed = true;
                _logger.LogInformation(
                    "Device OAuth managed site {SiteId} headers synced for {Vendor} account {Id}",
                    site.Id, account.VendorKey, account.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Device OAuth managed site header sync failed for account {Id}", account.Id);
            }
        }

        if (changed)
        {
            // 路由目标缓存携带站点自定义头，变更后必须失效重建。
            metadataCache.InvalidateRouteTargets();
        }
    }

    /// <summary>
    /// 厂商 Label 精简历史沿革（默认名 "{Label} 账号" 按登录时的 Label 落库）：
    /// 启动时把精确匹配的旧默认名规整为新名；用户自定义名不含其中、绝不触碰。
    /// </summary>
    private static readonly (string VendorKey, string LegacyLabel, string CurrentLabel)[] LegacyLabelRenames =
    [
        ("workbuddy-cn", "WorkBuddy（腾讯 CodeBuddy）", "WorkBuddy"),
    ];

    /// <summary>把旧 Label 时代落库的默认账号名/站点名精确替换为新默认名（启动执行一次）。</summary>
    internal async Task NormalizeLegacyDefaultNamesAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var metadataCache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        var accounts = await db.DeviceOAuthAccounts
            .Where(a => !a.IsDeleted)
            .ToListAsync(ct);
        if (accounts.Count == 0) return;

        var changed = false;
        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (vendorKey, legacy, current) in LegacyLabelRenames)
            {
                if (!string.Equals(account.VendorKey, vendorKey, StringComparison.OrdinalIgnoreCase)) continue;

                var legacyDefault = $"{legacy} 账号";
                var currentDefault = $"{current} 账号";
                try
                {
                    var renamed = false;
                    // 读取快照后名称可能已被用户并发修改；条件更新保证只改仍为旧默认名的行。
                    // IsDisplayNameCustom 用快照值比较，仅用于发现并发改名标记变化：
                    // 旧库升级回填 DEFAULT 1 的历史账号仍满足 true==true，不能被误跳过。
                    var wasCustom = account.IsDisplayNameCustom;
                    var accountRenamed = account.DisplayName == legacyDefault
                        && await db.Client.Updateable<DeviceOAuthAccount>()
                            .SetColumns(a => new DeviceOAuthAccount { DisplayName = currentDefault })
                            .Where(a => a.Id == account.Id
                                        && !a.IsDeleted
                                        && a.DisplayName == legacyDefault
                                        && a.IsDisplayNameCustom == wasCustom)
                            .ExecuteCommandAsync(ct) > 0;
                    if (accountRenamed)
                    {
                        renamed = true;

                        changed = true;
                    }

                    if (account.LinkedSiteId != Guid.Empty)
                    {
                        var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
                        if (site is not null && site.ManagedSource == DeviceOAuthAccountProvisioner.ManagedSource && site.Name == legacyDefault)
                        {
                            // 站点改名或取消托管后 WHERE 不再匹配，跳过而不是覆盖用户值。
                            var siteRenamed = await db.Client.Updateable<Site>()
                                .SetColumns(s => new Site { Name = currentDefault })
                                .Where(s => s.Id == site.Id
                                            && s.Name == legacyDefault
                                            && s.ManagedSource == DeviceOAuthAccountProvisioner.ManagedSource)
                                .ExecuteCommandAsync(ct) > 0;
                            if (siteRenamed)
                            {
                                renamed = true;

                                changed = true;
                            }
                        }
                    }

                    if (renamed)
                    {
                        _logger.LogInformation("Device OAuth account {Id} legacy default name normalized for {Vendor}", account.Id, account.VendorKey);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Device OAuth legacy name normalization failed for account {Id}", account.Id);
                }
            }
        }

        if (changed)
        {
            // 路由目标缓存携带站点展示名，改名后失效重建。
            metadataCache.InvalidateRouteTargets();
        }
    }

    internal async Task RefreshDueAccountsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DeviceOAuthVendorRegistry>();
        var metadataCache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var networkVendors = scope.ServiceProvider.GetService<NetworkBoundDeviceOAuthVendorFactory>();

        var now = DateTimeOffset.UtcNow;
        var due = await db.DeviceOAuthAccounts
            .Where(a => !a.IsDeleted && a.IsEnabled && !a.RequiresReauth
                        && a.RefreshToken != null && a.RefreshToken != "")
            .ToListAsync(ct);
        var dueSiteIds = due.Select(a => a.LinkedSiteId).Where(id => id != Guid.Empty).Distinct().ToList();
        var dueSites = dueSiteIds.Count == 0 ? new List<Site>()
            : await db.Sites.Where(s => dueSiteIds.Contains(s.Id)).ToListAsync(ct);
        var siteKeys = dueSites.ToDictionary(s => s.Id, s => s.ApiKey);

        foreach (var candidate in due)
        {
            if (ct.IsCancellationRequested) break;
            var mayNeedRepair = candidate.LinkedSiteId != Guid.Empty
                && !string.IsNullOrWhiteSpace(candidate.AccessToken)
                && siteKeys.TryGetValue(candidate.LinkedSiteId, out var siteKey)
                && !string.Equals(siteKey, candidate.AccessToken, StringComparison.Ordinal);
            var candidateVendor = registry.Find(candidate.VendorKey);
            if (!mayNeedRepair && (candidateVendor is null || candidate.TokenExpiresAt is not { } candidateExpiry
                || candidateExpiry - now > candidateVendor.RefreshLead
                || (candidate.LastRefreshAt is { } candidateLast && now - candidateLast < MinRefreshInterval)))
                continue;

            using (await DeviceOAuthRefreshGate.AcquireAsync(candidate.Id, ct))
            {
            // The scan is only a candidate list. A 401/manual refresh may have rotated it while
            // this worker waited, so never send its captured refresh token or write its entity.
            var account = await db.DeviceOAuthAccounts.InSingleAsync(candidate.Id);
            if (account is null || account.IsDeleted || !account.IsEnabled || account.RequiresReauth
                || string.IsNullOrWhiteSpace(account.RefreshToken)) continue;
            // Repair previously committed rotations before checking due/backoff or stale scan.
            try
            {
                await DeviceOAuthSiteCredentialSync.ReconcileAsync(db, metadataCache, account.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Device OAuth site credential repair failed for {Id}", account.Id);
                continue;
            }
            if (!string.Equals(account.AccessToken, candidate.AccessToken, StringComparison.Ordinal)
                || !string.Equals(account.RefreshToken, candidate.RefreshToken, StringComparison.Ordinal)) continue;
            var vendor = registry.Find(account.VendorKey);
            if (vendor is null)
            {
                _logger.LogWarning("No vendor registered for DeviceOAuthAccount {Id} vendor {Vendor}", account.Id, account.VendorKey);
                continue;
            }

            // 无过期时间的 token（如 ZCode 铸出的长效 API key）无需周期刷新；
            // 有过期时间的按厂商级提前量（短寿 token 厂商覆写更小值）。
            if (account.TokenExpiresAt is not { } expiresAt) continue;
            if (expiresAt - now > vendor.RefreshLead) continue;
            if (_retryAt.TryGetValue(account.Id, out var retryAt) && now < retryAt) continue;
            if (account.LastRefreshAt is { } last && now - last < MinRefreshInterval) continue;

            try
            {
                using var boundVendor = networkVendors?.ForAccount(account, "device-oauth-background-refresh");
                var remoteVendor = boundVendor?.Vendor ?? vendor;
                // 刷新→写回为不可取消段（stoppingToken 不进 vendor/DB 调用），防止半途丢轮换。
                var tokens = await remoteVendor.RefreshTokenAsync(
                    new DeviceOAuthTokenSet
                    {
                        AccessToken = account.AccessToken ?? string.Empty,
                        RefreshToken = account.RefreshToken,
                        ExpiresAt = account.TokenExpiresAt,
                    },
                    account.ExtraJson,
                    CancellationToken.None);
                account.AccessToken = tokens.AccessToken;
                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) account.RefreshToken = tokens.RefreshToken;
                account.TokenExpiresAt = tokens.ExpiresAt;
                account.LastRefreshAt = DateTimeOffset.UtcNow;
                account.RequiresReauth = false;
                account.UpdatedAt = DateTimeOffset.UtcNow;
                await DeviceOAuthSiteCredentialSync.PersistRotationAsync(db, metadataCache, account);
                await DeviceOAuthSiteCredentialSync.ReconcileAsync(db, metadataCache, account.Id, CancellationToken.None);
                _retryAt.TryRemove(account.Id, out _);
                _logger.LogInformation("Device OAuth account {Vendor}/{Id} token refreshed", account.VendorKey, account.Id);
            }
            catch (DeviceOAuthRefreshRefusedException ex)
            {
                account.RequiresReauth = true;
                account.UpdatedAt = DateTimeOffset.UtcNow;
                await db.Client.Updateable(account).UpdateColumns(a => new { a.RequiresReauth, a.UpdatedAt }).ExecuteCommandAsync(CancellationToken.None);
                _retryAt[account.Id] = DateTimeOffset.UtcNow.Add(RefreshFailureBackoff);
                _logger.LogWarning("Device OAuth account {Vendor}/{Id} requires re-auth: {Message}", account.VendorKey, account.Id, ex.Message);
            }
            catch (Exception ex)
            {
                _retryAt[account.Id] = DateTimeOffset.UtcNow.Add(RefreshFailureBackoff);
                _logger.LogWarning(ex, "Device OAuth token refresh failed for {Vendor}/{Id}", account.VendorKey, account.Id);
            }

            await Task.Delay(500, ct);
            }
        }
    }
}

/// <summary>Persist the rotated token even when publishing its site key fails.</summary>
internal static class DeviceOAuthSiteCredentialSync
{
    internal static async Task PersistRotationAsync(AppDbContext db, ProxyRequestMetadataCache cache, DeviceOAuthAccount account)
    {
        try
        {
            // This commit is independent of site publication: a rejected Site update must never
            // roll back the rotated refresh token, which the provider may already have consumed.
            var count = await db.Client.Updateable(account).UpdateColumns(a => new
            {
                a.AccessToken, a.RefreshToken, a.TokenExpiresAt, a.LastRefreshAt, a.UpdatedAt
            }).Where(a => a.Id == account.Id && !a.IsDeleted).ExecuteCommandAsync(CancellationToken.None);
            if (count != 1) throw new InvalidOperationException("Device OAuth account no longer exists.");
        }
        finally { cache.InvalidateRouteTargets(); }
    }

    internal static async Task ReconcileAsync(AppDbContext db, ProxyRequestMetadataCache cache, Guid accountId, CancellationToken ct)
    {
        using var client = db.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        client.Ado.BeginTran();
        var changed = false;
        try
        {
            // Acquire the SQLite writer before reading ownership; a concurrent relink, deletion,
            // or provisioning transaction cannot invalidate the binding before this update commits.
            await client.Ado.ExecuteCommandAsync("UPDATE DeviceOAuthAccounts SET UpdatedAt=UpdatedAt WHERE 1=0;");
            var account = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(accountId);
            if (account is null || account.IsDeleted) throw new InvalidOperationException("Device OAuth account no longer exists.");
            if (account.LinkedSiteId != Guid.Empty)
            {
                if (string.IsNullOrWhiteSpace(account.AccessToken))
                    throw new InvalidOperationException("Device OAuth account has no usable access token.");
                var siteId = account.LinkedSiteId;
                // Keep live-owner semantics identical to AccountNetworkPolicyStore. Query only
                // identities on this same writer transaction, never a separately cached snapshot.
                var owners = await client.Queryable<DeviceOAuthAccount>()
                    .Where(a => a.LinkedSiteId == siteId && !a.IsDeleted).Select(a => a.Id).ToListAsync(ct);
                var otherOwner = await client.Queryable<CodexAccount>().AnyAsync(a => a.LinkedSiteId == siteId, ct)
                    || await client.Queryable<GoogleAccount>().AnyAsync(a => a.LinkedSiteId == siteId, ct)
                    || await client.Queryable<KimiAccount>().AnyAsync(a => a.LinkedSiteId == siteId && !a.IsDeleted, ct)
                    || await client.Queryable<XaiAccount>().AnyAsync(a => a.LinkedSiteId == siteId && !a.IsDeleted, ct);
                var site = await client.Queryable<Site>().InSingleAsync(siteId);
                if (owners.Count != 1 || owners[0] != accountId || otherOwner || site is null
                    || site.ManagedSource != DeviceOAuthAccountProvisioner.ManagedSource)
                    throw new InvalidOperationException("Device OAuth site ownership changed.");
                if (!string.Equals(site.ApiKey, account.AccessToken, StringComparison.Ordinal))
                {
                    site.ApiKey = account.AccessToken ?? string.Empty;
                    await client.Updateable(site).UpdateColumns(s => new { s.ApiKey }).ExecuteCommandAsync(ct);
                    changed = true;
                }
            }
            ct.ThrowIfCancellationRequested();
            client.Ado.CommitTran();
        }
        catch
        {
            client.Ado.RollbackTran();
            throw;
        }
        finally { if (changed) cache.InvalidateRouteTargets(); }
    }
}

/// <summary>One canonical lease for every device OAuth credential refresh entry point.</summary>
public static class DeviceOAuthRefreshGate
{
    private static readonly KeyedAsyncLock Locks = new();
    internal static event Action<Guid>? Queued;

    public static Task<IDisposable> AcquireAsync(Guid accountId, CancellationToken ct)
    {
        var wait = Locks.WaitAsync($"device-oauth:{accountId:D}", ct);
        if (!wait.IsCompleted) Queued?.Invoke(accountId);
        return wait;
    }
}

/// <summary>
/// 代理 401 / 额度手动刷新按本地账号 ID single-flight，与后台刷新共用同一把锁。
/// </summary>
public sealed class DeviceOAuthCredentialRefreshService
{
    private static readonly TimeSpan RefreshSafetyTimeout = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _dbContext;
    private readonly DeviceOAuthVendorRegistry _registry;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<DeviceOAuthCredentialRefreshService> _logger;
    private readonly NetworkBoundDeviceOAuthVendorFactory? _networkVendors;

    public DeviceOAuthCredentialRefreshService(
        AppDbContext dbContext,
        DeviceOAuthVendorRegistry registry,
        ProxyRequestMetadataCache metadataCache,
        ILogger<DeviceOAuthCredentialRefreshService> logger,
        NetworkBoundDeviceOAuthVendorFactory? networkVendors = null)
    {
        _dbContext = dbContext;
        _registry = registry;
        _metadataCache = metadataCache;
        _logger = logger;
        _networkVendors = networkVendors;
    }

    public async Task<string?> RefreshAsync(Guid linkedSiteId, string staleAccessToken, CancellationToken cancellationToken)
    {
        // 无站点账号（LinkedSiteId=Empty，如 Qoder/Trae/Kiro）：按站点键查账号会命中任意同态账号，
        // 绝不能拿别的账号 token 用——直接返回 null（额度查询按 CredentialInvalid 报错即可）。
        if (linkedSiteId == Guid.Empty)
        {
            return null;
        }

        var owners = await _dbContext.DeviceOAuthAccounts
            .Where(a => a.LinkedSiteId == linkedSiteId && !a.IsDeleted).ToListAsync(cancellationToken);
        if (owners.Count != 1) return null;
        return await RefreshCoreAsync(owners[0].Id, linkedSiteId, staleAccessToken, cancellationToken, false);
    }

    /// <summary>Quota refresh is keyed by local account identity, including accounts without a site.</summary>
    public Task<string?> RefreshForAccountAsync(Guid accountId, string staleAccessToken, CancellationToken cancellationToken)
        => RefreshCoreAsync(accountId, null, staleAccessToken, cancellationToken, true);

    /// <summary>Returns current credentials under the same gate as proxy and background rotations.</summary>
    public async Task<DeviceOAuthTokenSet> EnsureFreshAsync(Guid accountId, bool forceRefresh, CancellationToken ct)
    {
        var initial = await _dbContext.DeviceOAuthAccounts.InSingleAsync(accountId);
        if (initial is null || initial.IsDeleted) throw new KeyNotFoundException("OAuth account not found.");
        var access = await RefreshCoreAsync(accountId, null, initial.AccessToken ?? string.Empty, ct, true,
            onlyIfDue: !forceRefresh, requireRefreshToken: true, initialRefreshToken: initial.RefreshToken,
            initialRefreshAt: initial.LastRefreshAt, initialSiteId: initial.LinkedSiteId);
        if (string.IsNullOrWhiteSpace(access)) throw new InvalidOperationException("OAuth credential refresh did not produce an access token.");
        var current = await _dbContext.DeviceOAuthAccounts.InSingleAsync(accountId);
        if (current is null || current.IsDeleted) throw new KeyNotFoundException("OAuth account not found.");
        if (current.LinkedSiteId != initial.LinkedSiteId || !string.Equals(current.AccessToken, access, StringComparison.Ordinal))
            throw new InvalidOperationException("OAuth account credential changed while being read.");
        return new DeviceOAuthTokenSet { AccessToken = current.AccessToken ?? string.Empty, RefreshToken = current.RefreshToken, ExpiresAt = current.TokenExpiresAt };
    }

    private async Task<string?> RefreshCoreAsync(Guid accountId, Guid? expectedSiteId, string staleAccessToken, CancellationToken cancellationToken, bool reportFailure, bool onlyIfDue = false, bool requireRefreshToken = false, string? initialRefreshToken = null, DateTimeOffset? initialRefreshAt = null, Guid? initialSiteId = null)
    {
        using (await DeviceOAuthRefreshGate.AcquireAsync(accountId, cancellationToken))
        {
            var account = await _dbContext.DeviceOAuthAccounts.InSingleAsync(accountId);
            if (account is null || account.IsDeleted) return null;
            if (initialSiteId is { } originalSiteId && account.LinkedSiteId != originalSiteId)
                throw new InvalidOperationException("OAuth account site changed while refreshing.");
            if (expectedSiteId is { } siteId && (account.LinkedSiteId != siteId
                || (await _dbContext.DeviceOAuthAccounts.Where(a => a.LinkedSiteId == siteId && !a.IsDeleted)
                    .ToListAsync(cancellationToken)).Count != 1)) return null;
            if (onlyIfDue)
            {
                if (account.TokenExpiresAt is null || account.TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return account.AccessToken;
            }
            else if (requireRefreshToken && (!string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal)
                || !string.Equals(account.RefreshToken, initialRefreshToken, StringComparison.Ordinal)
                || account.LastRefreshAt != initialRefreshAt)) return account.AccessToken;
            try
            {
                await DeviceOAuthSiteCredentialSync.ReconcileAsync(_dbContext, _metadataCache, accountId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Device OAuth site credential repair failed for {AccountId}", accountId);
                if (reportFailure) throw;
                return null;
            }
            if (!onlyIfDue && !string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal)) return account.AccessToken;
            if (string.IsNullOrWhiteSpace(account.RefreshToken))
            {
                if (requireRefreshToken) throw new DeviceOAuthRefreshRefusedException("OAuth reauthorization required.");
                return null;
            }

            var vendor = _registry.Find(account.VendorKey);
            if (vendor is null) return null;

            // 不可取消关键段：刷新发出后必须完成写回（对齐 CodexCredentialRefreshService）。
            using var coreCts = new CancellationTokenSource(RefreshSafetyTimeout);
            try
            {
                using var boundVendor = _networkVendors?.ForAccount(account, "device-oauth-credential-refresh");
                var remoteVendor = boundVendor?.Vendor ?? vendor;
                var tokens = await remoteVendor.RefreshTokenAsync(
                    new DeviceOAuthTokenSet
                    {
                        AccessToken = account.AccessToken ?? string.Empty,
                        RefreshToken = account.RefreshToken,
                        ExpiresAt = account.TokenExpiresAt,
                    },
                    account.ExtraJson,
                    coreCts.Token);
                if (string.IsNullOrWhiteSpace(tokens.AccessToken))
                    throw new InvalidOperationException("OAuth refresh returned an empty access token.");
                account.AccessToken = tokens.AccessToken;
                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) account.RefreshToken = tokens.RefreshToken;
                account.TokenExpiresAt = tokens.ExpiresAt;
                account.LastRefreshAt = DateTimeOffset.UtcNow;
                account.RequiresReauth = false;
                account.UpdatedAt = DateTimeOffset.UtcNow;
                await DeviceOAuthSiteCredentialSync.PersistRotationAsync(_dbContext, _metadataCache, account);
                await DeviceOAuthSiteCredentialSync.ReconcileAsync(_dbContext, _metadataCache, account.Id, CancellationToken.None);
                return tokens.AccessToken;
            }
            catch (DeviceOAuthRefreshRefusedException)
            {
                // Quota invalidation uses a credential-version guard; do not write a stale snapshot here.
                if (reportFailure) throw;
                account.RequiresReauth = true;
                await _dbContext.Client.Updateable(account).UpdateColumns(a => new { a.RequiresReauth }).ExecuteCommandAsync(CancellationToken.None);
                return null;
            }
            catch (NetworkPolicyException)
            {
                // A revoked network is not an authentication failure and must not be hidden.
                throw;
            }
            catch (Exception)
            {
                // Vendor errors can contain response bodies; never log credentials or tokens.
                _logger.LogWarning("Device OAuth credential refresh failed for account {AccountId}", accountId);
                if (reportFailure) throw;
                return null;
            }
        }
    }
}
