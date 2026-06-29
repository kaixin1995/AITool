using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Web.Services;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 创建访问密钥的请求参数，仅需指定密钥名称。
/// </summary>
public sealed class CreateAccessKeyRequest
{
    /// <summary>
    /// 密钥名称。
    /// </summary>
    public string KeyName { get; set; } = string.Empty;
    /// <summary>
    /// 允许访问的路由入口名称列表。空列表=允许全部路由，非空=只允许列表中的路由。
    /// </summary>
    public List<string> AllowedRouteNames { get; set; } = [];
}

/// <summary>
/// 创建访问密钥的响应结果，包含新创建密钥的标识、名称和明文密钥。
/// </summary>
public sealed class CreateAccessKeyResult
{
    /// <summary>
    /// 密钥标识。
    /// </summary>
    public Guid KeyId { get; set; }
    /// <summary>
    /// 密钥名称。
    /// </summary>
    public string KeyName { get; set; } = string.Empty;
    /// <summary>
    /// 明文密钥。
    /// </summary>
    public string PlainKey { get; set; } = string.Empty;
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// 访问密钥列表项，用于密钥管理页面展示每条密钥的基本信息。
/// </summary>
public sealed class AccessKeyListItem
{
    /// <summary>
    /// 密钥标识。
    /// </summary>
    public Guid KeyId { get; set; }
    /// <summary>
    /// 密钥名称。
    /// </summary>
    public string KeyName { get; set; } = string.Empty;
    /// <summary>
    /// 明文密钥。
    /// </summary>
    public string PlainKey { get; set; } = string.Empty;
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
    /// <summary>
    /// 允许访问的路由入口名称列表。空列表=允许全部路由。
    /// </summary>
    public List<string> AllowedRouteNames { get; set; } = [];
}

/// <summary>
/// 更新访问密钥路由权限的请求参数。
/// </summary>
public sealed class UpdateAccessKeyRoutesRequest
{
    /// <summary>
    /// 允许访问的路由入口名称列表。空列表=允许全部路由。
    /// </summary>
    public List<string> AllowedRouteNames { get; set; } = [];
}

/// <summary>
/// 访问密钥管理控制器，提供密钥的创建、列表查询、启用/禁用和删除功能。
/// </summary>
[ApiController]
[Route("api/admin/access-keys")]
public sealed class AccessKeysApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 创建访问密钥管理控制器。
    /// </summary>
    public AccessKeysApiController(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 获取访问密钥列表。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var keys = await _dbContext.ProxyAccessKeys
            
            .OrderBy(k => k.KeyName)
            .ToListAsync(cancellationToken);

        var items = keys.Select(k => new AccessKeyListItem
        {
            KeyId = k.Id,
            KeyName = k.KeyName,
            PlainKey = k.PlainKey,
            IsEnabled = k.IsEnabled,
            AllowedRouteNames = DeserializeRouteNames(k.AllowedRouteNames)
        }).ToList();
        return Ok(items);
    }

    /// <summary>
    /// 创建访问密钥。
    /// </summary>
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateAccessKeyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.KeyName))
            return BadRequest(new { message = "密钥名称不能为空" });

        // 生成随机密钥：sk-前缀 + 32位随机十六进制字符串
        var plainKey = "sk-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // 同步保留哈希值，兼容现有校验逻辑
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainKey));
        var hash = Convert.ToHexString(hashBytes);

        // 同步保留掩码值，兼容历史字段
        var masked = $"{plainKey[..6]}...{plainKey[^4..]}";

        var key = new ProxyAccessKey
        {
            KeyName = request.KeyName,
            PlainKey = plainKey,
            AccessKeyHash = hash,
            MaskedValue = masked,
            IsEnabled = true,
            AllowedRouteNames = SerializeRouteNames(request.AllowedRouteNames)
        };
        _dbContext.ProxyAccessKeys.Add(key);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateAccessKeys();

        return Ok(new CreateAccessKeyResult
        {
            KeyId = key.Id,
            KeyName = key.KeyName,
            PlainKey = key.PlainKey,
            IsEnabled = true
        });
    }

    /// <summary>
    /// 切换访问密钥启用状态。
    /// </summary>
    [HttpPost("toggle/{keyId}")]
    public async Task<IActionResult> Toggle(Guid keyId, CancellationToken cancellationToken)
    {
        var key = await _dbContext.ProxyAccessKeys.InSingleAsync(keyId);
        if (key is null) return NotFound(new { message = "密钥不存在" });

        key.IsEnabled = !key.IsEnabled;
        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateAccessKeys();

        return Ok(new { keyId, isEnabled = key.IsEnabled });
    }

    /// <summary>
    /// 删除访问密钥。
    /// </summary>
    [HttpPost("delete/{keyId}")]
    public async Task<IActionResult> Delete(Guid keyId, CancellationToken cancellationToken)
    {
        var key = await _dbContext.ProxyAccessKeys.InSingleAsync(keyId);
        if (key is null) return NotFound(new { message = "密钥不存在" });

        _dbContext.ProxyAccessKeys.Remove(key);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateAccessKeys();

        return Ok(new { keyId });
    }

    /// <summary>
    /// 更新访问密钥允许的路由入口。不重新生成密钥，只修改路由权限。
    /// </summary>
    [HttpPost("update-routes/{keyId}")]
    public async Task<IActionResult> UpdateRoutes(Guid keyId, [FromBody] UpdateAccessKeyRoutesRequest request, CancellationToken cancellationToken)
    {
        var key = await _dbContext.ProxyAccessKeys.InSingleAsync(keyId);
        if (key is null) return NotFound(new { message = "密钥不存在" });

        key.AllowedRouteNames = SerializeRouteNames(request.AllowedRouteNames);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateAccessKeys();

        return Ok(new { keyId, allowedRouteNames = DeserializeRouteNames(key.AllowedRouteNames) });
    }

    /// <summary>
    /// 将路由名称列表序列化为 JSON 字符串存储。空列表序列化为空串（表示允许全部）。
    /// </summary>
    private static string SerializeRouteNames(List<string>? routeNames)
    {
        if (routeNames is null || routeNames.Count == 0)
        {
            return string.Empty;
        }

        var filtered = routeNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        return filtered.Count == 0
            ? string.Empty
            : JsonSerializer.Serialize(filtered);
    }

    /// <summary>
    /// 将存储的 JSON 字符串反序列化为路由名称列表。空串返回空列表。
    /// </summary>
    private static List<string> DeserializeRouteNames(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(stored) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
