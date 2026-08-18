using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace AITool.IntegrationTests.Auth;

/// <summary>
/// PasswordHasher 单元测试：验证 PBKDF2 哈希/校验往返、旧 MD5 兼容、透明升级标记。
/// </summary>
public sealed class PasswordHasherTests
{
    /// <summary>
    /// 验证 Hash → Verify 正向往返。
    /// </summary>
    [Fact]
    public void Hash_then_verify_correct_password_succeeds()
    {
        var hash = PasswordHasher.Hash("MySecret123!");
        var ok = PasswordHasher.Verify("MySecret123!", hash, out var needsUpgrade);
        ok.Should().BeTrue();
        needsUpgrade.Should().BeFalse("新格式不应触发升级");
    }

    /// <summary>
    /// 验证错误密码校验失败。
    /// </summary>
    [Fact]
    public void Verify_wrong_password_fails()
    {
        var hash = PasswordHasher.Hash("correct");
        var ok = PasswordHasher.Verify("wrong", hash, out _);
        ok.Should().BeFalse();
    }

    /// <summary>
    /// 验证空密码/空哈希返回 false（不抛异常）。
    /// </summary>
    [Theory]
    [InlineData("", "anything")]
    [InlineData("anything", "")]
    [InlineData("", "")]
    [InlineData("x", "pbkdf2$badformat")]
    [InlineData("x", "pbkdf2$100$notbase64$alsobad")]
    public void Verify_invalid_inputs_returns_false(string password, string storedHash)
    {
        var ok = PasswordHasher.Verify(password, storedHash, out var needsUpgrade);
        ok.Should().BeFalse();
        needsUpgrade.Should().BeFalse();
    }

    /// <summary>
    /// 验证旧 MD5 格式兼容：校验通过且标记 needsUpgrade=true。
    /// </summary>
    [Fact]
    public void Verify_legacy_md5_succeeds_and_marks_upgrade()
    {
        // MD5("admin123") = 0192023a7bbd73250516f069df18b500
        var legacyMd5 = "0192023a7bbd73250516f069df18b500";
        var ok = PasswordHasher.Verify("admin123", legacyMd5, out var needsUpgrade);
        ok.Should().BeTrue("旧 MD5 应兼容");
        needsUpgrade.Should().BeTrue("旧格式应触发透明升级");
    }

    /// <summary>
    /// 验证旧 MD5 错误密码不通过。
    /// </summary>
    [Fact]
    public void Verify_legacy_md5_wrong_password_fails()
    {
        var legacyMd5 = "0192023a7bbd73250516f069df18b500";
        var ok = PasswordHasher.Verify("wrongpassword", legacyMd5, out _);
        ok.Should().BeFalse();
    }

    /// <summary>
    /// 验证两次 Hash 同一密码生成不同的哈希（盐随机）。
    /// </summary>
    [Fact]
    public void Hash_same_password_twice_produces_different_hashes()
    {
        var h1 = PasswordHasher.Hash("same");
        var h2 = PasswordHasher.Hash("same");
        h1.Should().NotBe(h2, "盐是随机的");
        // 但两个哈希都应能校验通过
        PasswordHasher.Verify("same", h1, out _).Should().BeTrue();
        PasswordHasher.Verify("same", h2, out _).Should().BeTrue();
    }
}

/// <summary>
/// JwtTokenService 单元测试：验证签发、刷新轮换、吊销。
/// </summary>
public sealed class JwtTokenServiceTests
{
    private static JwtTokenService CreateService()
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = "this-is-a-test-signing-key-at-least-32-bytes-long!!",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 7
        });
        // 用共享内存 SQLite 做测试 DB（CopyNew 需要看到同一数据库）
        var connString = $"DataSource=file:test-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var sqlSugar = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true
        }, _ => { });
        sqlSugar.CodeFirst.InitTables(typeof(AITool.Domain.Auth.RefreshTokenRecord));
        var dbContext = new AppDbContext(sqlSugar, new SemaphoreSlim(1, 1));
        return new JwtTokenService(options, dbContext);
    }

    /// <summary>
    /// 验证签发的 token 对包含有效 access + refresh。
    /// </summary>
    [Fact]
    public void IssueTokens_returns_valid_pair()
    {
        var svc = CreateService();
        var pair = svc.IssueTokens("admin");
        pair.AccessToken.Should().NotBeNullOrEmpty();
        pair.RefreshToken.Should().NotBeNullOrEmpty();
        pair.AccessTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(14));
        pair.RefreshTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(6));
    }

    /// <summary>
    /// 验证 refresh 成功换发新 token 对。
    /// </summary>
    [Fact]
    public void Refresh_valid_token_returns_new_pair()
    {
        var svc = CreateService();
        var original = svc.IssueTokens("admin");
        var refreshed = svc.Refresh(original.RefreshToken);
        refreshed.Should().NotBeNull();
        refreshed!.AccessToken.Should().NotBe(original.AccessToken);
        refreshed.RefreshToken.Should().NotBe(original.RefreshToken, "应轮换为新 token");
    }

    /// <summary>
    /// 验证旧 refresh token 轮换后立即作废（不能再次使用）。
    /// </summary>
    [Fact]
    public void Refresh_old_token_after_rotation_is_invalid()
    {
        var svc = CreateService();
        var original = svc.IssueTokens("admin");
        svc.Refresh(original.RefreshToken); // 第一次刷新，旧 token 被消费
        var secondAttempt = svc.Refresh(original.RefreshToken); // 再用旧 token
        secondAttempt.Should().BeNull("旧 refresh token 轮换后应作废");
    }

    /// <summary>
    /// 验证空/无效 refresh token 返回 null。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("invalid-token")]
    [InlineData(" ")]
    public void Refresh_invalid_token_returns_null(string badToken)
    {
        var svc = CreateService();
        var result = svc.Refresh(badToken);
        result.Should().BeNull();
    }

    /// <summary>
    /// 并发请求同一个 refresh token 时只能有一个请求完成轮换。
    /// </summary>
    [Fact]
    public async Task Refresh_concurrent_requests_consume_token_once()
    {
        var svc = CreateService();
        var original = svc.IssueTokens("admin");

        var results = await Task.WhenAll(
            Task.Run(() => svc.Refresh(original.RefreshToken)),
            Task.Run(() => svc.Refresh(original.RefreshToken)));

        results.Count(result => result is not null).Should().Be(1);
        results.Count(result => result is null).Should().Be(1);
    }

    /// <summary>
    /// 验证 Revoke 后 refresh token 不可用。
    /// </summary>
    [Fact]
    public void Revoke_invalidates_refresh_token()
    {
        var svc = CreateService();
        var pair = svc.IssueTokens("admin");
        svc.Revoke(pair.RefreshToken);
        var refreshed = svc.Refresh(pair.RefreshToken);
        refreshed.Should().BeNull("吊销后不应能刷新");
    }
}
