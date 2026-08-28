using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Auth;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SqlSugar;

namespace AITool.Admin.Services;

/// <summary>
/// 签发与刷新 JWT access/refresh token。
/// <para>
/// access token 无状态（HS256 签名，自包含）。
/// refresh token 持久化到数据库，程序重启不丢失。
/// </para>
/// </summary>
public sealed class JwtTokenService
{
    private static readonly object TokenStoreSync = new();
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static long _lastCleanupUtcTicks;

    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly AppDbContext _dbContext;

    public JwtTokenService(IOptions<JwtOptions> options, AppDbContext dbContext)
    {
        _options = options.Value;
        _dbContext = dbContext;
        var keyBytes = Encoding.UTF8.GetBytes(_options.SigningKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                $"Jwt:SigningKey 长度不足（当前 {keyBytes.Length} 字节，至少需要 32 字节/256 位）。");
        }
        _signingKey = new SymmetricSecurityKey(keyBytes);
    }

    /// <summary>
    /// 签发一对新的 access + refresh token。
    /// </summary>
    public TokenPair IssueTokens(string subjectId)
    {
        lock (TokenStoreSync)
        {
            CleanupExpiredIfNeeded();

            var now = DateTimeOffset.UtcNow;
            var accessExpires = now.AddMinutes(Math.Max(1, _options.AccessTokenMinutes));
            var refreshExpires = now.AddDays(Math.Max(1, _options.RefreshTokenDays));

            var access = BuildAccessToken(subjectId, now, accessExpires);
            var refresh = GenerateRefreshTokenString();

            // 用 CopyNew 独立连接写入，不碰单例 SqlSugarScope
            using var client = _dbContext.Client.CopyNew();
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            client.Insertable(new RefreshTokenRecord
            {
                Token = refresh,
                SubjectId = subjectId,
                ExpiresAt = refreshExpires,
                CreatedAt = now
            }).ExecuteCommand();

            return new TokenPair(access, refresh, accessExpires, refreshExpires);
        }
    }

    /// <summary>
    /// 用 refresh token 换发新的 access + refresh token（轮换：旧 token 立即作废）。
    /// </summary>
    public TokenPair? Refresh(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        lock (TokenStoreSync)
        {
            // 用 CopyNew 独立连接，不碰单例 SqlSugarScope
            using var client = _dbContext.Client.CopyNew();
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

            var record = client.Queryable<RefreshTokenRecord>()
                .Where(r => r.Token == refreshToken)
                .First();

            if (record == null)
            {
                return null;
            }

            // 删除旧 token（轮换），即使后续校验失败也作废。
            // IssueTokens/Revoke 同样受 TokenStoreSync 保护，避免同一 token 被并发消费两次。
            client.Deleteable<RefreshTokenRecord>()
                .Where(r => r.Token == refreshToken)
                .ExecuteCommand();

            if (record.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            return IssueTokens(record.SubjectId);
        }
    }

    /// <summary>
    /// 吊销指定的 refresh token。
    /// </summary>
    public void Revoke(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        lock (TokenStoreSync)
        {
            using var client = _dbContext.Client.CopyNew();
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            client.Deleteable<RefreshTokenRecord>()
                .Where(r => r.Token == refreshToken)
                .ExecuteCommand();
        }
    }

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

    private static string GenerateRefreshTokenString()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// 惰性清理过期的 refresh token。
    /// </summary>
    private void CleanupExpiredIfNeeded()
    {
        var now = DateTimeOffset.UtcNow;
        var nowTicks = now.UtcDateTime.Ticks;
        var lastCleanupTicks = Volatile.Read(ref _lastCleanupUtcTicks);
        if (lastCleanupTicks != 0 && nowTicks - lastCleanupTicks < CleanupInterval.Ticks)
        {
            return;
        }

        try
        {
            using var client = _dbContext.Client.CopyNew();
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            client.Deleteable<RefreshTokenRecord>()
                .Where(r => r.ExpiresAt <= now)
                .ExecuteCommand();
            Volatile.Write(ref _lastCleanupUtcTicks, nowTicks);
        }
        catch
        {
            // 清理失败不影响签发
        }
    }
}

/// <summary>
/// access + refresh token 对及其过期时间。
/// </summary>
public sealed record TokenPair(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset RefreshTokenExpiresAt);
