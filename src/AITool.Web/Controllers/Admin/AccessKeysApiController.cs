using System.Security.Cryptography;
using System.Text;
using AITool.Web.Services;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// CreateAccessKeyRequest。
/// </summary>
public sealed class CreateAccessKeyRequest
{
    /// <summary>
    /// 密钥名称。
    /// </summary>
    public string KeyName { get; set; } = string.Empty;
}

/// <summary>
/// CreateAccessKeyResult。
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
/// AccessKeyListItem。
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
}

/// <summary>
/// AccessKeysApiController。
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
            .Select(k => new AccessKeyListItem
            {
                KeyId = k.Id,
                KeyName = k.KeyName,
                PlainKey = k.PlainKey,
                IsEnabled = k.IsEnabled
            })
            .ToListAsync(cancellationToken);
        return Ok(keys);
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
            IsEnabled = true
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
        var key = await _dbContext.ProxyAccessKeys.FindAsync([keyId], cancellationToken);
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
        var key = await _dbContext.ProxyAccessKeys.FindAsync([keyId], cancellationToken);
        if (key is null) return NotFound(new { message = "密钥不存在" });

        _dbContext.ProxyAccessKeys.Remove(key);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateAccessKeys();

        return Ok(new { keyId });
    }
}
