using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 创建密钥的请求体
public sealed class CreateAccessKeyRequest
{
    // 密钥名称
    public string KeyName { get; set; } = string.Empty;
}

// 创建密钥的返回结果
public sealed class CreateAccessKeyResult
{
    // 密钥ID
    public Guid KeyId { get; set; }
    // 密钥名称
    public string KeyName { get; set; } = string.Empty;
    // 原始密钥值（仅创建时返回一次）
    public string PlainKey { get; set; } = string.Empty;
    // 掩码显示值
    public string MaskedValue { get; set; } = string.Empty;
    // 是否启用
    public bool IsEnabled { get; set; }
}

// 密钥列表项
public sealed class AccessKeyListItem
{
    // 密钥ID
    public Guid KeyId { get; set; }
    // 密钥名称
    public string KeyName { get; set; } = string.Empty;
    // 掩码显示值
    public string MaskedValue { get; set; } = string.Empty;
    // 是否启用
    public bool IsEnabled { get; set; }
}

// 访问密钥管理 API，支持 AJAX 创建/切换/删除密钥
[ApiController]
[Route("api/admin/access-keys")]
public sealed class AccessKeysApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public AccessKeysApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 获取所有密钥列表
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var keys = await _dbContext.ProxyAccessKeys
            .OrderBy(k => k.KeyName)
            .Select(k => new AccessKeyListItem
            {
                KeyId = k.Id,
                KeyName = k.KeyName,
                MaskedValue = k.MaskedValue,
                IsEnabled = k.IsEnabled
            })
            .ToListAsync(cancellationToken);
        return Ok(keys);
    }

    // 创建密钥，自动生成密钥值并返回明文（仅此一次）
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateAccessKeyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.KeyName))
            return BadRequest(new { message = "密钥名称不能为空" });

        // 生成随机密钥：sk-前缀 + 32位随机十六进制字符串
        var plainKey = "sk-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // SHA256 哈希存储
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainKey));
        var hash = Convert.ToHexString(hashBytes);

        // 生成掩码显示值，保留前6位和后4位
        var masked = $"{plainKey[..6]}...{plainKey[^4..]}";

        var key = new ProxyAccessKey
        {
            KeyName = request.KeyName,
            AccessKeyHash = hash,
            MaskedValue = masked,
            IsEnabled = true
        };
        _dbContext.ProxyAccessKeys.Add(key);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new CreateAccessKeyResult
        {
            KeyId = key.Id,
            KeyName = key.KeyName,
            PlainKey = plainKey,
            MaskedValue = masked,
            IsEnabled = true
        });
    }

    // 切换密钥启用/禁用状态
    [HttpPost("toggle/{keyId}")]
    public async Task<IActionResult> Toggle(Guid keyId, CancellationToken cancellationToken)
    {
        var key = await _dbContext.ProxyAccessKeys.FindAsync([keyId], cancellationToken);
        if (key is null) return NotFound(new { message = "密钥不存在" });

        key.IsEnabled = !key.IsEnabled;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { keyId, isEnabled = key.IsEnabled });
    }

    // 删除密钥
    [HttpPost("delete/{keyId}")]
    public async Task<IActionResult> Delete(Guid keyId, CancellationToken cancellationToken)
    {
        var key = await _dbContext.ProxyAccessKeys.FindAsync([keyId], cancellationToken);
        if (key is null) return NotFound(new { message = "密钥不存在" });

        _dbContext.ProxyAccessKeys.Remove(key);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { keyId });
    }
}
