using System.Collections.Concurrent;
using AITool.Application.OAuth;
using AITool.Domain.OAuth;
using AITool.Domain.Sites;
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

    public DeviceOAuthAccountProvisioner(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        SiteCascadeDeleter cascadeDeleter,
        ILogger<DeviceOAuthAccountProvisioner> logger)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _cascadeDeleter = cascadeDeleter;
        _logger = logger;
    }

    public async Task<DeviceOAuthAccount> ProvisionAsync(
        IDeviceOAuthVendor vendor,
        string? displayName,
        string? email,
        string? externalUserId,
        DeviceOAuthTokenSet tokens,
        string? extraJson,
        CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        var existing = await FindExistingAsync(client, vendor.VendorKey, email, externalUserId, ct);
        DeviceOAuthAccount account;
        Site? site = null;

        var label = string.IsNullOrWhiteSpace(displayName)
            ? email ?? $"{vendor.Label} 账号"
            : displayName!.Trim();

        if (existing is not null)
        {
            account = existing;
            site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            account.AccessToken = tokens.AccessToken;
            if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) account.RefreshToken = tokens.RefreshToken;
            account.TokenExpiresAt = tokens.ExpiresAt;
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            account.RequiresReauth = false;
            if (!string.IsNullOrEmpty(email)) account.Email = email;
            if (!string.IsNullOrEmpty(externalUserId)) account.ExternalUserId = externalUserId;
            if (!string.IsNullOrWhiteSpace(extraJson)) account.ExtraJson = extraJson;
            if (!string.IsNullOrWhiteSpace(displayName)) account.DisplayName = label;
            account.IsEnabled = true;
            account.ManuallyDisabled = false;
            account.DisabledByFeatureToggle = false;
            account.IsDeleted = false;
            account.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            account = new DeviceOAuthAccount
            {
                VendorKey = vendor.VendorKey,
                DisplayName = label,
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
            var extraHeadersJson = System.Text.Json.JsonSerializer.Serialize(
                chat.ExtraHeaders, AITool.Application.Common.JsonSerializerPresets.Compact);
            // 厂商在 ExtraJson 里给 chatBaseUrl 时覆盖默认基地址（如 Copilot 个人版端点）。
            var chatBaseUrl = TryGetExtraString(extraJson, "chatBaseUrl");
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
                    ExtraHeadersJson = extraHeadersJson,
                    IsEnabled = true,
                };
                await client.Insertable(site).ExecuteCommandAsync(ct);
                account.LinkedSiteId = site.Id;
            }
            else
            {
                site.ApiKey = tokens.AccessToken;
                site.Name = label;
                site.BaseUrl = chatBaseUrl ?? chat.BaseUrl;
                site.ExtraHeadersJson = extraHeadersJson;
                site.IsEnabled = true;
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

        // 模型映射：默认清单（厂商静态注册表）；映射的上游名与公开名一致。
        if (site is not null)
        {
            await UpsertDefaultModelMappingsAsync(client, site.Id, vendor, ct);
        }

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
        return account;
    }

    public async Task ToggleAsync(Guid accountId, bool isEnabled, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        var account = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(accountId);
        if (account == null) return;

        account.IsEnabled = isEnabled;
        account.ManuallyDisabled = !isEnabled;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await client.Updateable(account).UpdateColumns(a => new { a.IsEnabled, a.ManuallyDisabled, a.UpdatedAt }).ExecuteCommandAsync(ct);

        if (account.LinkedSiteId != Guid.Empty)
        {
            var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.IsEnabled = isEnabled;
                await client.Updateable(site).UpdateColumns(s => new { s.IsEnabled }).ExecuteCommandAsync(ct);
            }
        }

        _metadataCache.InvalidateRouteTargets();
    }

    public async Task UpdateAsync(Guid accountId, string displayName, string? refreshToken, CancellationToken ct)
    {
        using var client = _dbContext.Client.CopyNew();
        var account = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(accountId)
            ?? throw new KeyNotFoundException("账号不存在");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("账号展示名不能为空", nameof(displayName));
        }

        account.DisplayName = displayName.Trim();
        account.UpdatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            account.RefreshToken = refreshToken.Trim();
            account.RequiresReauth = false;
        }

        await client.Updateable(account).ExecuteCommandAsync(ct);
        if (account.LinkedSiteId != Guid.Empty)
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
        using var client = _dbContext.Client.CopyNew();
        var account = await client.Queryable<DeviceOAuthAccount>().InSingleAsync(accountId);
        if (account == null) return;

        if (account.LinkedSiteId != Guid.Empty)
        {
            await _cascadeDeleter.RemoveSitesAsync([account.LinkedSiteId], ct);
        }

        await client.Deleteable<DeviceOAuthAccount>().Where(a => a.Id == accountId).ExecuteCommandAsync(ct);
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateModelMetadata();
    }

    /// <summary>读取厂商 ExtraJson 的字符串字段。</summary>
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

    internal async Task RefreshDueAccountsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<DeviceOAuthVendorRegistry>();
        var metadataCache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        var now = DateTimeOffset.UtcNow;
        var due = await db.DeviceOAuthAccounts
            .Where(a => !a.IsDeleted && a.IsEnabled && !a.RequiresReauth
                        && a.RefreshToken != null && a.RefreshToken != "")
            .ToListAsync(ct);

        foreach (var account in due)
        {
            if (ct.IsCancellationRequested) break;

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
                // 刷新→写回为不可取消段（stoppingToken 不进 vendor/DB 调用），防止半途丢轮换。
                var tokens = await vendor.RefreshTokenAsync(account.RefreshToken!, account.ExtraJson, CancellationToken.None);
                account.AccessToken = tokens.AccessToken;
                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) account.RefreshToken = tokens.RefreshToken;
                account.TokenExpiresAt = tokens.ExpiresAt;
                account.LastRefreshAt = DateTimeOffset.UtcNow;
                account.RequiresReauth = false;
                account.UpdatedAt = DateTimeOffset.UtcNow;
                await db.Client.Updateable(account).ExecuteCommandAsync(CancellationToken.None);

                if (account.LinkedSiteId != Guid.Empty)
                {
                    var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
                    if (site is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                    {
                        site.ApiKey = tokens.AccessToken;
                        await db.Client.Updateable(site).ExecuteCommandAsync(CancellationToken.None);
                    }
                }

                _retryAt.TryRemove(account.Id, out _);
                metadataCache.InvalidateRouteTargets();
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

/// <summary>
/// 代理 401 命中 device_oauth 托管站点时的实时凭证刷新（single-flight 按站点，对齐 Codex 模式）。
/// </summary>
public sealed class DeviceOAuthCredentialRefreshService
{
    private static readonly KeyedAsyncLock RefreshLocks = new();
    private static readonly TimeSpan RefreshSafetyTimeout = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _dbContext;
    private readonly DeviceOAuthVendorRegistry _registry;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<DeviceOAuthCredentialRefreshService> _logger;

    public DeviceOAuthCredentialRefreshService(
        AppDbContext dbContext,
        DeviceOAuthVendorRegistry registry,
        ProxyRequestMetadataCache metadataCache,
        ILogger<DeviceOAuthCredentialRefreshService> logger)
    {
        _dbContext = dbContext;
        _registry = registry;
        _metadataCache = metadataCache;
        _logger = logger;
    }

    public async Task<string?> RefreshAsync(Guid linkedSiteId, string staleAccessToken, CancellationToken cancellationToken)
    {
        // 无站点账号（LinkedSiteId=Empty，如 Qoder/Trae/Kiro）：按站点键查账号会命中任意同态账号，
        // 绝不能拿别的账号 token 用——直接返回 null（额度查询按 CredentialInvalid 报错即可）。
        if (linkedSiteId == Guid.Empty)
        {
            return null;
        }

        using (await RefreshLocks.WaitAsync(linkedSiteId.ToString("N"), cancellationToken))
        {
            var account = (await _dbContext.DeviceOAuthAccounts
                .Where(a => a.LinkedSiteId == linkedSiteId && !a.IsDeleted)
                .ToListAsync(cancellationToken))
                .FirstOrDefault();
            if (account is null || string.IsNullOrWhiteSpace(account.RefreshToken)) return null;
            if (!string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal)) return account.AccessToken;

            var vendor = _registry.Find(account.VendorKey);
            if (vendor is null) return null;

            // 不可取消关键段：刷新发出后必须完成写回（对齐 CodexCredentialRefreshService）。
            using var coreCts = new CancellationTokenSource(RefreshSafetyTimeout);
            try
            {
                var tokens = await vendor.RefreshTokenAsync(account.RefreshToken!, account.ExtraJson, coreCts.Token);
                account.AccessToken = tokens.AccessToken;
                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken)) account.RefreshToken = tokens.RefreshToken;
                account.TokenExpiresAt = tokens.ExpiresAt;
                account.LastRefreshAt = DateTimeOffset.UtcNow;
                account.RequiresReauth = false;
                await _dbContext.Client.Updateable(account).ExecuteCommandAsync(coreCts.Token);

                var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
                if (site is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    site.ApiKey = tokens.AccessToken;
                    await _dbContext.Client.Updateable(site).ExecuteCommandAsync(coreCts.Token);
                }

                _metadataCache.InvalidateRouteTargets();
                return tokens.AccessToken;
            }
            catch (DeviceOAuthRefreshRefusedException)
            {
                account.RequiresReauth = true;
                await _dbContext.Client.Updateable(account).UpdateColumns(a => new { a.RequiresReauth }).ExecuteCommandAsync(CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Device OAuth credential refresh failed for site {SiteId}", linkedSiteId);
                return null;
            }
        }
    }
}
