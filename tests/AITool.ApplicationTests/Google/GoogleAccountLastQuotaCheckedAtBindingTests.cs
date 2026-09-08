using AITool.Domain.Google;
using FluentAssertions;

namespace AITool.ApplicationTests.Google;

/// <summary>
/// GoogleAccount.LastQuotaCheckedAt（DateTimeOffset?）的 DataReader 绑定契约。
/// 背景：线上报「LastQuotaCheckedAt绑定到GoogleAccount失败」导致额度巡检无法列出账号——
/// 用例逐一验证 NULL / 正常值 / 脏值（空串、非法文本）下的行为，确定哪类数据会炸绑定。
/// </summary>
public sealed class GoogleAccountLastQuotaCheckedAtBindingTests
{
    private static GoogleAccount NewAccount(Guid id)
        => new()
        {
            Id = id,
            DisplayName = "bind-test",
            Email = "bind@example.com",
            AccountKind = "Antigravity",
            AccessToken = "tok",
            RefreshToken = "rtok",
            CreatedAt = DateTimeOffset.Now
        };

    [Fact]
    public async Task Null_last_quota_checked_at_binds_fine()
    {
        var (db, dispose) = TestDatabaseFactory.Create();
        try
        {
            await db.Client.Insertable(NewAccount(Guid.NewGuid())).ExecuteCommandAsync();

            var act = async () => await db.GoogleAccounts.OrderBy(a => a.LastQuotaCheckedAt).ToListAsync();
            await act.Should().NotThrowAsync();
        }
        finally
        {
            dispose();
        }
    }

    [Fact]
    public async Task Normal_written_value_round_trips()
    {
        var (db, dispose) = TestDatabaseFactory.Create();
        try
        {
            var account = NewAccount(Guid.NewGuid());
            account.LastQuotaCheckedAt = DateTimeOffset.Now;
            await db.Client.Insertable(account).ExecuteCommandAsync();

            var list = await db.GoogleAccounts.OrderBy(a => a.LastQuotaCheckedAt).ToListAsync();
            list.Should().ContainSingle();
            list[0].LastQuotaCheckedAt.Should().NotBeNull();
        }
        finally
        {
            dispose();
        }
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-45 99:99:99")]
    public async Task Garbage_text_value_reproduces_bind_error(string badValue)
    {
        var (db, dispose) = TestDatabaseFactory.Create();
        try
        {
            var account = NewAccount(Guid.NewGuid());
            await db.Client.Insertable(account).ExecuteCommandAsync();
            // 直接写脏值（模拟历史数据/手工编辑/异常写入），绕过实体类型系统。
            await db.Client.Ado.ExecuteCommandAsync(
                $"UPDATE GoogleAccounts SET LastQuotaCheckedAt = '{badValue}' WHERE Id = '{account.Id}'");

            var act = async () => await db.GoogleAccounts.OrderBy(a => a.LastQuotaCheckedAt).ToListAsync();
            await act.Should().ThrowAsync<Exception>().Where(e => e.Message.Contains("LastQuotaCheckedAt"));
        }
        finally
        {
            dispose();
        }
    }

    [Fact]
    public async Task Sanitize_clears_garbage_value_and_restores_query()
    {
        var (db, dispose) = TestDatabaseFactory.Create();
        try
        {
            var good = NewAccount(Guid.NewGuid());
            good.LastQuotaCheckedAt = DateTimeOffset.Now;
            var bad = NewAccount(Guid.NewGuid());
            await db.Client.Insertable(new[] { good, bad }).ExecuteCommandAsync();
            await db.Client.Ado.ExecuteCommandAsync(
                $"UPDATE GoogleAccounts SET LastQuotaCheckedAt = 'corrupt' WHERE Id = '{bad.Id}'");

            // 修复前：整表查询被脏值拖垮。
            await FluentActions.Awaiting(() => db.GoogleAccounts.OrderBy(a => a.LastQuotaCheckedAt).ToListAsync())
                .Should().ThrowAsync<Exception>();

            // 启动清理（InitializeDatabase 末尾自动执行；此处直接验证该方法本身）。
            AITool.Infrastructure.Persistence.SqlSugarSetup.SanitizeAccountQuotaTimestamps(db.Client);

            // 修复后：查询恢复，正常行的时间保留、脏行被置 NULL。
            var list = await db.GoogleAccounts.OrderBy(a => a.LastQuotaCheckedAt).ToListAsync();
            list.Should().HaveCount(2);
            list.Should().ContainSingle(a => a.Id == good.Id && a.LastQuotaCheckedAt != null);
            list.Should().ContainSingle(a => a.Id == bad.Id && a.LastQuotaCheckedAt == null);
        }
        finally
        {
            dispose();
        }
    }
}
