using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AITool.Web.Services;

/// <summary>
/// 签发与刷新 JWT access/refresh token。
/// <para>
/// access token 无状态（HS256 签名，自包含），适合跨端（Web/App/PC）携带。
/// refresh token 有状态（内存存储 + 过期清理），用于在 access 过期后换新，避免频繁登录。
/// </para>
/// <para>
/// 设计为 Singleton：refresh token 存储是进程级内存状态。
/// 多实例部署时 refresh token 不共享（需换 Redis），但 access token 仍可用（无状态）。
/// </para>
/// </summary>
public sealed class JwtTokenService
{
    /// <summary>
    /// JWT 配置选项。
    /// </summary>
    private readonly JwtOptions _options;

    /// <summary>
    /// 对称签名密钥（缓存，避免每次签发都构造）。
    /// </summary>
    private readonly SymmetricSecurityKey _signingKey;

    /// <summary>
    /// refresh token 存储：token 字符串 → 关联的 SubjectId + 过期时间。
    /// 用 ConcurrentDictionary 支持并发签发/吊销/校验。
    /// </summary>
    private readonly ConcurrentDictionary<string, RefreshTokenRecord> _refreshTokens = new();

    /// <summary>
    /// 上次执行过期清理的时间。清理在签发时惰性触发，避免单独的后台线程。
    /// </summary>
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;

    /// <summary>
    /// 过期清理的最小间隔（避免每次签发都扫描全表）。
    /// </summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 初始化 JWT token 服务。
    /// </summary>
    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey 长度不足（当前 {keyBytes.Length} 字节，至少需要 32 字节/256 位）。请在 appsettings.json 或环境变量配置足够长的随机字符串。");
        }
        _signingKey = new SymmetricSecurityKey(keyBytes);
    }

    /// <summary>
    /// 签发一对新的 access + refresh token。
    /// </summary>
    public TokenPair IssueTokens(string subjectId)
    {
        CleanupExpiredIfNeeded();

        var now = DateTimeOffset.UtcNow;
        var accessExpires = now.AddMinutes(Math.Max(1, _options.AccessTokenMinutes));
        var refreshExpires = now.AddDays(Math.Max(1, _options.RefreshTokenDays));

        var access = BuildAccessToken(subjectId, now, accessExpires);
        var refresh = GenerateRefreshTokenString();

        _refreshTokens[refresh] = new RefreshTokenRecord(subjectId, refreshExpires);

        return new TokenPair(access, refresh, accessExpires, refreshExpires);
    }

    /// <summary>
    /// 用 refresh token 换发新的 access + refresh token。
    /// 校验通过后旧 refresh token 立即作废（轮换，降低泄漏风险）。
    /// </summary>
    /// <returns>新 token 对；refresh token 无效或过期时返回 null。</returns>
    public TokenPair? Refresh(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        // 取出即删（轮换）：即使后续校验失败，旧 token 也已作废。
        if (!_refreshTokens.TryRemove(refreshToken, out var record))
        {
            return null;
        }

        if (record.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        return IssueTokens(record.SubjectId);
    }

    /// <summary>
    /// 吊销指定的 refresh token（登出 / 改密时调用）。
    /// </summary>
    public void Revoke(string refreshToken)
    {
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            _refreshTokens.TryRemove(refreshToken, out _);
        }
    }

    /// <summary>
    /// 构造 access token 的 JWT 字符串。
    /// </summary>
    private string BuildAccessToken(string subjectId, DateTimeOffset notBefore, DateTimeOffset expires)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subjectId),
            new Claim(JwtRegisteredClaimNames.Name, "admin"),
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: notBefore.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// 生成不可猜测的 refresh token 字符串（256 位随机）。
    /// </summary>
    private static string GenerateRefreshTokenString()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// 惰性清理过期的 refresh token，控制在 CleanupInterval 间隔内只执行一次。
    /// </summary>
    private void CleanupExpiredIfNeeded()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCleanupAt < CleanupInterval)
        {
            return;
        }

        _lastCleanupAt = now;
        foreach (var pair in _refreshTokens)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _refreshTokens.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>
    /// refresh token 记录。
    /// </summary>
    private sealed record RefreshTokenRecord(string SubjectId, DateTimeOffset ExpiresAt);
}

/// <summary>
/// access + refresh token 对及其过期时间。
/// </summary>
public sealed record TokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset RefreshTokenExpiresAt);
