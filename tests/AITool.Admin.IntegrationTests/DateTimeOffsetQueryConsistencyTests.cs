using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AITool.Admin.IntegrationTests.Persistence;

/// <summary>
/// 验证生产 SqlSugar 配置（SqlSugarSetup.AddSqlSugar）下 DateTimeOffset 的存查一致性：
/// 实体写入被 AOP 转为本地时钟存储，查询参数必须同样对齐，否则 UTC 探针在非 UTC 部署
/// （如 UTC+8）下所有时间过滤会系统性偏移一个时区（token 刷新/冷却恢复/日志保留均受影响）。
/// </summary>
public sealed class DateTimeOffsetQueryConsistencyTests
{
    [Fact]
    public void Utc_now_probe_matches_local_clock_storage_under_production_config()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aitool-tz-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        SqlSugarSetup.AddSqlSugar(services, $"Data Source={dbPath}");
        using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ISqlSugarClient>();
        try
        {
            SqlSugarSetup.InitializeDatabase(db);

            // UTC 04:00 = 本地 12:00（UTC+8 部署下存储原文为本地时钟）。
            var writtenAt = DateTimeOffset.Parse("2026-08-15T04:00:00+00:00");
            db.Insertable(new SqlMigrationExecution
            {
                FileName = $"tz-probe-{Guid.NewGuid():N}.sql",
                FileHash = "test",
                ExecutedAt = writtenAt
            }).ExecuteCommand();

            // 探针 = 写入后 1 小时（UTC 表达，模拟后台服务用 DateTimeOffset.UtcNow 查询）。
            var probe = DateTimeOffset.Parse("2026-08-15T05:00:00+00:00");
            var matchedAfter = db.Queryable<SqlMigrationExecution>()
                .Count(x => x.FileHash == "test" && x.ExecutedAt <= probe);
            matchedAfter.Should().Be(1, "UTC 探针必须命中本地时钟存储的行（查询参数已由 OnExecutingChangeSql 对齐）");

            // 边界：写入前 1 小时不应命中。
            var early = DateTimeOffset.Parse("2026-08-15T03:00:00+00:00");
            var matchedBefore = db.Queryable<SqlMigrationExecution>()
                .Count(x => x.FileHash == "test" && x.ExecutedAt <= early);
            matchedBefore.Should().Be(0, "未到期的行不应被误判为到期");
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* 临时文件清理失败忽略 */ }
        }
    }
}
