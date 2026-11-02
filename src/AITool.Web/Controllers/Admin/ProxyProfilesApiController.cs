using System.Diagnostics;
using System.Net;
using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Contracts;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace AITool.Web.Controllers.Admin;

/// <summary>网络出口代理方案管理；所有响应只暴露无凭据端点和凭据存在状态。</summary>
[ApiController]
[Route("api/admin/developer/proxy-profiles")]
public class ProxyProfilesApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache? _metadataCache;
    private readonly ISystemRuntimeSettingsService _settingsService;
    private readonly ProxyProfileSecretService _secrets;

    public ProxyProfilesApiController(AppDbContext dbContext, ISystemRuntimeSettingsService settingsService,
        ProxyProfileSecretService secrets, ProxyRequestMetadataCache? metadataCache = null)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
        _secrets = secrets;
        _metadataCache = metadataCache;
    }

    private async Task<bool> IsProxyProfilesEnabledAsync(CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetOrCreateAsync(cancellationToken);
        return settings is not null && settings.DeveloperFeaturesEnabled && settings.DeveloperProxyProfilesEnabled;
    }

    private static object ToResponse(ProxyProfile profile)
    {
        // Defense in depth for pre-migration rows: never return URL userinfo even if a host
        // incorrectly serves requests before the required startup migration.
        ProxyProfileSecretService.TryNormalizeProxyUrl(profile.ProxyUrl, out var endpoint, out var legacy, out _);
        return new
        {
            id = profile.Id,
            key = profile.Key,
            name = profile.Name,
            proxyUrl = endpoint,
            hasCredentials = ProxyProfileSecretService.HasCredentials(profile) || legacy is not null,
            revision = profile.Revision,
            configurationError = profile.ConfigurationError is null ? null : "invalid_legacy_proxy_endpoint",
            description = profile.Description,
            isEnabled = profile.IsEnabled,
            sortOrder = profile.SortOrder,
            createdAt = profile.CreatedAt,
            updatedAt = profile.UpdatedAt
        };
    }

    private void InvalidateMetadata()
    {
        _metadataCache?.InvalidateRouteTargets();
        _metadataCache?.InvalidateModelMetadata();
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken)) return NotFound();
        var profiles = await _dbContext.ProxyProfiles.OrderBy(x => x.SortOrder).ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        // Preserve legacy unwrapped list response.
        return Ok(profiles.Select(ToResponse));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken)) return NotFound();
        var profile = await _dbContext.ProxyProfiles.InSingleAsync(id);
        return profile is null
            ? NotFound(ApiResponse.Fail("代理方案不存在", "proxy_not_found"))
            : Ok(ApiResponse.Ok(ToResponse(profile)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProxyProfilePayload payload, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken)) return NotFound();
        if (string.IsNullOrWhiteSpace(payload?.Key) || string.IsNullOrWhiteSpace(payload.Name) || string.IsNullOrWhiteSpace(payload.ProxyUrl))
            return BadRequest(ApiResponse.Fail("方案标识 Key、名称和代理地址不能为空", "invalid_input"));

        var key = payload.Key.Trim();
        if (await _dbContext.ProxyProfiles.AnyAsync(x => x.Key == key, cancellationToken))
            return Conflict(ApiResponse.Fail("方案标识 Key 已存在，请更换", "duplicate_key"));
        var profile = new ProxyProfile
        {
            Key = key,
            Name = payload.Name.Trim(),
            Description = payload.Description?.Trim(),
            IsEnabled = payload.IsEnabled,
            SortOrder = payload.SortOrder
        };
        if (!_secrets.TryApplyForSave(profile, payload.ProxyUrl, payload.CredentialAction, payload.Username, payload.Password, out var error))
            return BadRequest(ApiResponse.Fail(error!, "invalid_proxy_url"));
        await _dbContext.InsertAsync(profile, cancellationToken);
        InvalidateMetadata();
        return Ok(ApiResponse.Ok(ToResponse(profile), "代理方案已创建"));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ProxyProfilePayload payload, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken)) return NotFound();
        if (string.IsNullOrWhiteSpace(payload?.Name) || string.IsNullOrWhiteSpace(payload.ProxyUrl))
            return BadRequest(ApiResponse.Fail("方案名称和代理地址不能为空", "invalid_input"));
        var profile = await _dbContext.ProxyProfiles.InSingleAsync(id);
        if (profile is null) return NotFound(ApiResponse.Fail("代理方案不存在", "proxy_not_found"));
        var oldRevision = profile.Revision;
        if (payload.Revision.HasValue && payload.Revision != oldRevision)
            return Conflict(ApiResponse.Fail("代理方案已修改，请刷新后重试", "proxy_revision_conflict"));

        if (!string.IsNullOrWhiteSpace(payload.Key))
        {
            var newKey = payload.Key.Trim();
            if (!string.Equals(newKey, profile.Key, StringComparison.OrdinalIgnoreCase))
            {
                if (await _dbContext.ProxyProfiles.AnyAsync(x => x.Key == newKey && x.Id != id, cancellationToken))
                    return Conflict(ApiResponse.Fail("方案标识 Key 已存在", "duplicate_key"));
                profile.Key = newKey;
            }
        }
        if (!_secrets.TryApplyForSave(profile, payload.ProxyUrl, payload.CredentialAction, payload.Username, payload.Password, out var error))
            return BadRequest(ApiResponse.Fail(error!, "invalid_proxy_url"));
        profile.Name = payload.Name.Trim();
        profile.Description = payload.Description?.Trim();
        profile.IsEnabled = payload.IsEnabled;
        profile.SortOrder = payload.SortOrder;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        profile.Revision = checked(Math.Max(1, oldRevision) + 1);
        var updated = await _dbContext.Client.Updateable(profile)
            .Where(x => x.Id == id && x.Revision == oldRevision).ExecuteCommandAsync(cancellationToken);
        if (updated == 0) return Conflict(ApiResponse.Fail("代理方案已修改，请刷新后重试", "proxy_revision_conflict"));
        InvalidateMetadata();
        return Ok(ApiResponse.Ok(ToResponse(profile), "代理方案已更新"));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken)) return NotFound();
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        await client.Ado.BeginTranAsync();
        try
        {
            // Acquire SQLite's writer lock before reading references. Policy assignment uses
            // the same write-first transaction, preventing assign-vs-delete TOCTOU races.
            var found = await client.Updateable<ProxyProfile>().SetColumns(p => new ProxyProfile { Revision = p.Revision })
                .Where(p => p.Id == id).ExecuteCommandAsync(cancellationToken);
            if (found == 0)
            {
                await client.Ado.RollbackTranAsync();
                return NotFound(ApiResponse.Fail("代理方案不存在", "proxy_not_found"));
            }
            var references = await client.Queryable<AccountNetworkPolicy>()
                .Where(p => p.ProxyProfileId == id).ToListAsync(cancellationToken);
            if (references.Count != 0)
            {
                await client.Ado.RollbackTranAsync();
                return Conflict(new ApiResponse<object>
                {
                    Success = false,
                    Message = "代理方案仍被账户网络策略引用",
                    ErrorCode = "proxy_profile_in_use",
                    Data = new { references = references.Select(p => new { p.ProviderKey, p.AccountId }) }
                });
            }
            await client.Deleteable<ProxyProfile>().Where(p => p.Id == id).ExecuteCommandAsync(cancellationToken);
            await client.Ado.CommitTranAsync();
        }
        catch
        {
            await client.Ado.RollbackTranAsync();
            throw;
        }
        InvalidateMetadata();
        return Ok(ApiResponse.Ok("代理方案已删除"));
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestConnectivity([FromBody] TestProxyRequest request, CancellationToken cancellationToken)
    {
        if (!await IsProxyProfilesEnabledAsync(cancellationToken)) return NotFound();
        if (request is null || (request.ProfileId is null && string.IsNullOrWhiteSpace(request.ProxyUrl)))
            return BadRequest(ApiResponse.Fail("代理地址或方案 Id 不能为空", "invalid_proxy_url"));
        var proxyUrl = request.ProxyUrl?.Trim() ?? string.Empty;
        NetworkCredential? credentials;
        if (request.ProfileId is not null || !proxyUrl.Contains("://", StringComparison.Ordinal))
        {
            var profile = request.ProfileId is { } profileId
                ? await _dbContext.ProxyProfiles.InSingleAsync(profileId)
                : await _dbContext.ProxyProfiles.FirstAsync(x => x.Key == proxyUrl, cancellationToken);
            if (profile is null) return BadRequest(ApiResponse.Fail("代理方案不存在", "proxy_not_found"));
            if (profile.ConfigurationError is not null)
                return BadRequest(ApiResponse.Fail("代理方案配置需要修复", "invalid_legacy_proxy_endpoint"));
            if (!profile.IsEnabled) return BadRequest(ApiResponse.Fail("代理方案已停用", "proxy_disabled"));
            if (!ProxyProfileSecretService.TryNormalizeProxyUrl(profile.ProxyUrl, out proxyUrl, out var embedded, out _) || embedded is not null)
                return BadRequest(ApiResponse.Fail("代理方案端点不可用", "invalid_proxy_url"));
            try { credentials = _secrets.ReadCredentials(profile); }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return BadRequest(ApiResponse.Fail("代理凭据不可用", "proxy_credentials_unavailable"));
            }
        }
        else if (!ProxyProfileSecretService.TryNormalizeProxyUrl(proxyUrl, out proxyUrl, out credentials, out var error))
        {
            return BadRequest(ApiResponse.Fail(error!, "invalid_proxy_url"));
        }

        var targetUrl = string.IsNullOrWhiteSpace(request.TargetUrl) ? "https://cp.cloudflare.com/generate_204" : request.TargetUrl.Trim();
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) ||
            target.UserInfo.Length != 0 || string.IsNullOrEmpty(target.Host))
            return BadRequest(ApiResponse.Fail("测试目标必须是不含凭据的 HTTP 或 HTTPS 地址", "invalid_target_url"));

        var sw = Stopwatch.StartNew();
        try
        {
            using var httpClient = new HttpClient(ProxyProfileSecretService.CreateProxyHandler(proxyUrl, credentials))
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await httpClient.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            var isSuccess = response.IsSuccessStatusCode;
            var message = isSuccess ? $"连接成功，延迟 {sw.ElapsedMilliseconds} ms" : "代理或测试目标返回非成功状态";
            return Ok(ApiResponse.Ok(new
            {
                isSuccess, httpResponseReceived = true, statusCode = (int)response.StatusCode,
                latencyMs = sw.ElapsedMilliseconds, targetUrl,
                errorMessage = isSuccess ? null : "代理或测试目标返回非成功状态"
            }, message));
        }
        catch (Exception)
        {
            sw.Stop();
            // Transport exception text may contain credentials/URLs. Never echo it to clients.
            return Ok(ApiResponse.Ok(new
            {
                isSuccess = false, httpResponseReceived = false, statusCode = 0, latencyMs = sw.ElapsedMilliseconds,
                errorMessage = "代理连接失败或超时", targetUrl
            }, "代理连接失败或超时"));
        }
    }
}

public sealed class ProxyProfilePayload
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProxyUrl { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
    public long? Revision { get; set; }
    public string? CredentialAction { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class TestProxyRequest
{
    public Guid? ProfileId { get; set; }
    public string ProxyUrl { get; set; } = string.Empty;
    public string? TargetUrl { get; set; }
}
