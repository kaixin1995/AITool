using System.Data;
using System.Linq.Expressions;
using AITool.Domain.Detection;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// 基于 SqlSugar 的数据访问入口，替代原 EF Core 的 AppDbContext。
/// <para>
/// 内部持有一个 <see cref="SqlSugarScope"/>（线程安全的单例客户端），
/// 对外暴露与原 DbSet 同名的 <see cref="ISugarQueryable{T}"/> 便捷访问器，
/// 业务代码从 <c>dbContext.Sites</c> 改为 <c>dbContext.Sites</c>（保持属性名不变），
/// 底层换成 SqlSugar 的查询/插入/删除能力。
/// </para>
/// </summary>
public sealed class AppDbContext : IDisposable, IAsyncDisposable
{
    private readonly ISqlSugarClient _client;

    /// <summary>
    /// 释放资源。注意：底层 SqlSugarScope 是 DI 管理的单例，这里不真正释放它；
    /// 此方法仅为兼容原 EF 代码中 dbContext.Dispose()/await using 的调用模式（空操作）。
    /// </summary>
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// 底层 SqlSugar 客户端，供需要高级操作（事务、原生 SQL）的代码使用。
    /// </summary>
    public ISqlSugarClient Client => _client;

    // —— 与原 DbSet 同名的便捷查询访问器 ——
    public ISugarQueryable<Site> Sites => _client.Queryable<Site>();
    public ISugarQueryable<ModelLibraryItem> ModelLibraryItems => _client.Queryable<ModelLibraryItem>();
    public ISugarQueryable<SiteModelMapping> SiteModelMappings => _client.Queryable<SiteModelMapping>();
    public ISugarQueryable<DetectionTask> DetectionTasks => _client.Queryable<DetectionTask>();
    public ISugarQueryable<DetectionTaskExecution> DetectionTaskExecutions => _client.Queryable<DetectionTaskExecution>();
    public ISugarQueryable<ProxyRouteEntry> ProxyRouteEntries => _client.Queryable<ProxyRouteEntry>();
    public ISugarQueryable<ProxyRouteRule> ProxyRouteRules => _client.Queryable<ProxyRouteRule>();
    public ISugarQueryable<ProxyAccessKey> ProxyAccessKeys => _client.Queryable<ProxyAccessKey>();
    public ISugarQueryable<ProxyUsageLog> ProxyUsageLogs => _client.Queryable<ProxyUsageLog>();
    public ISugarQueryable<ModelHealthMonitor> ModelHealthMonitors => _client.Queryable<ModelHealthMonitor>();
    public ISugarQueryable<SystemRuntimeSettings> SystemRuntimeSettings => _client.Queryable<SystemRuntimeSettings>();

    /// <summary>
    /// 由 DI 注入的 SqlSugar 客户端构造。
    /// </summary>
    public AppDbContext(ISqlSugarClient client)
    {
        _client = client;
    }

    // —— 增删改便捷方法（替代 EF 的 Add/Remove + SaveChanges）——
    // SqlSugar 的写操作是立即执行的，不需要单独 SaveChanges。提供这些方法让业务层迁移时改动最小。

    /// <summary>插入单条实体（替代 EF Add + SaveChanges）。</summary>
    public Task<int> InsertAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class, new()
    {
        return _client.Insertable(entity).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>批量插入（替代 EF AddRange + SaveChanges）。</summary>
    public Task<int> InsertRangeAsync<T>(IEnumerable<T> entities, CancellationToken cancellationToken = default) where T : class, new()
    {
        return _client.Insertable(entities.ToList()).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>更新单条实体。</summary>
    public Task<int> UpdateAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class, new()
    {
        return _client.Updateable(entity).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>按主键删除单条实体。</summary>
    public Task<int> DeleteAsync<T>(T entity, CancellationToken cancellationToken = default) where T : class, new()
    {
        return _client.Deleteable(entity).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>批量删除。</summary>
    public Task<int> DeleteRangeAsync<T>(IEnumerable<T> entities, CancellationToken cancellationToken = default) where T : class, new()
    {
        return _client.Deleteable(entities.ToList()).ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 按条件删除（替代 EF 的 Where + RemoveRange + SaveChanges）。
    /// SqlSugar 删除查询结果要用 Deleteable.Where(predicate)，不能在 Queryable 上 Delete。
    /// </summary>
    public Task<int> DeleteAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class, new()
    {
        return _client.Deleteable<T>().Where(predicate).ExecuteCommandAsync();
    }
}

/// <summary>
/// SqlSugar 的 DI 注册与初始化扩展。
/// </summary>
public static class SqlSugarSetup
{
    /// <summary>
    /// 注册 SqlSugarScope（线程安全单例）和 <see cref="AppDbContext"/>（Scoped 适配），
    /// 并在连接级别保持与原 EF 配置一致的 SQLite PRAGMA（WAL、cache_size、busy_timeout）。
    /// </summary>
    public static IServiceCollection AddSqlSugar(this IServiceCollection services, string connectionString)
    {
        var sqlSugar = new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = true,
            MoreSettings = new ConnMoreSettings
            {
                IsAutoRemoveDataCache = true
            }
        },
        config =>
        {
            // SQLite 无原生 DateTimeOffset，SqlSugar 用 TEXT 存储时不保留 offset，
            // 读回时配本地时区 offset，导致瞬时时刻偏移（写 +0h 读回 +8h）。
            // 这里在读后把所有 DateTimeOffset 属性规范化回 UTC offset（+0h），保持瞬时时刻正确。
            config.Aop.DataExecuted = (value, entityInfo) =>
            {
                // SqlSugar 读回 DateTimeOffset 时配本地时区 offset，导致瞬时偏移。
                // 注意：此回调在部分查询路径（如 ToList）下可能不触发，作为尽力而为的补偿。
                // 确定性补偿在 SqlSugarQueryableExtensions.NormalizeDates（查询后处理）中实现。
                var entity = entityInfo.Entity;
                if (entity is null) return;
                var type = entity.GetType();
                foreach (var prop in type.GetProperties())
                {
                    if (prop.PropertyType == typeof(DateTimeOffset))
                    {
                        var current = (DateTimeOffset)prop.GetValue(entity)!;
                        if (current.Offset != TimeSpan.Zero)
                        {
                            prop.SetValue(entity, new DateTimeOffset(current.DateTime, TimeSpan.Zero));
                        }
                    }
                }
            };
        });

        // WAL 模式是持久化的，但首次建库时仍需确保设置一次；在 InitTables 阶段执行。
        services.AddSingleton<ISqlSugarClient>(sqlSugar);
        // AppDbContext 作为 Scoped 暴露给业务代码，与原 EF 的 Scoped 生命周期一致。
        services.AddScoped<AppDbContext>();

        return services;
    }

    /// <summary>
    /// 初始化数据库：CodeFirst 建表 + 持久化 PRAGMA（WAL、synchronous）。
    /// 等价于原 EF 的 EnsureCreated + 启动期 PRAGMA。
    /// </summary>
    public static void InitializeDatabase(ISqlSugarClient db)
    {
        // 持久化 PRAGMA：WAL 模式与 synchronous=NORMAL 设置一次永久生效。
        // 连接级 PRAGMA：cache_size、busy_timeout 在每次连接生命周期内生效（SqlSugarScope 单例 + 连接池复用）。
        try
        {
            db.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
            db.Ado.ExecuteCommand("PRAGMA synchronous=NORMAL;");
            db.Ado.ExecuteCommand("PRAGMA cache_size=-65536;");
            db.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        }
        catch { }

        // CodeFirst 建表（表已存在时只增不删，自动补齐缺失列）。
        db.CodeFirst.InitTables(
            typeof(Site),
            typeof(ModelLibraryItem),
            typeof(SiteModelMapping),
            typeof(DetectionTask),
            typeof(DetectionTaskExecution),
            typeof(ProxyRouteEntry),
            typeof(ProxyRouteRule),
            typeof(ProxyAccessKey),
            typeof(ProxyUsageLog),
            typeof(ModelHealthMonitor),
            typeof(SystemRuntimeSettings));
    }
}
