using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AITool.ApplicationTests;

/// <summary>
/// 单元测试用的 SqlSugar 数据库辅助工厂。
/// <para>
/// 替代原 EF Core 的 UseInMemoryDatabase：每个测试创建独立的临时 SQLite 文件，
/// 通过 SqlSugarSetup.InitializeDatabase 建表，测试结束后删除文件，避免数据互相污染。
/// </para>
/// </summary>
public static class TestDatabaseFactory
{
    /// <summary>
    /// 创建一个独立的临时 SqlSugar 客户端 + AppDbContext（基于临时 SQLite 文件）。
    /// 返回的元组包含 AppDbContext 和清理回调。
    /// </summary>
    public static (AppDbContext DbContext, Action Dispose) Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aitool-test-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath}";

        var sqlSugar = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = true
        });
        SqlSugarSetup.InitializeDatabase(sqlSugar);

        var services = new ServiceCollection();
        services.AddSqlSugar(connectionString);
        // 重新注册用临时数据库的客户端（覆盖 AddSqlSugar 里的单例）。
        services.AddSingleton<ISqlSugarClient>(_ => new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = true
        }));
        var serviceProvider = services.BuildServiceProvider();
        var dbContext = serviceProvider.GetRequiredService<AppDbContext>();

        return (dbContext, () =>
        {
            try { sqlSugar.Dispose(); } catch { }
            try { File.Delete(dbPath); } catch { }
        });
    }
}
