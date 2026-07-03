using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AITool.IntegrationTests;

/// <summary>
/// IntegrationTests 的 SqlSugar 数据库初始化辅助方法。
/// <para>
/// 替代原 EF Core 的工厂初始化模式（RemoveAll DbContextOptions + AddDbContext UseSqlite + EnsureDeleted/EnsureCreated）：
/// 测试宿主在 ConfigureServices 里用 <see cref="ReplaceWithSqlSugar"/> 覆盖默认注册，
/// 在 ConfigureClient 里用 <see cref="InitializeDatabaseAsync"/> 建表 + 清空数据。
/// </para>
/// </summary>
public static class IntegrationTestDbHelper
{
    /// <summary>
    /// 在测试宿主的 ConfigureServices 中覆盖默认的 SqlSugar 注册，指向指定临时数据库文件。
    /// 替代原 services.RemoveAll DbContextOptions + AddDbContext UseSqlite。
    /// </summary>
    public static void ReplaceWithSqlSugar(IServiceCollection services, string databasePath)
    {
        // 移除可能已注册的 SqlSugar 单例和 AppDbContext（避免生产配置干扰测试）。
        var sqlSugarDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(ISqlSugarClient));
        if (sqlSugarDescriptor is not null)
        {
            services.Remove(sqlSugarDescriptor);
        }
        var contextDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(AppDbContext));
        if (contextDescriptor is not null)
        {
            services.Remove(contextDescriptor);
        }

        var connectionString = $"Data Source={databasePath}";
        var sqlSugar = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = true
        });
        services.AddSingleton<ISqlSugarClient>(sqlSugar);
        services.AddScoped<AppDbContext>();
    }

    /// <summary>
    /// 初始化测试数据库：删除旧文件 + 建表。
    /// 替代原 EnsureDeletedAsync + EnsureCreatedAsync。
    /// </summary>
    public static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var sqlSugar = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
        SqlSugarSetup.InitializeDatabase(sqlSugar);
    }
}
