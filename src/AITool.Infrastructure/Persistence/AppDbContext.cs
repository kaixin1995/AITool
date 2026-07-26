using System.Linq.Expressions;
using AITool.Domain.Codex;
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
/// 内部持有一个 <see cref="ISqlSugarClient"/>（由 DI 注册的 SqlSugarScope 单例），
/// 对外暴露与原 DbSet 同名的 <see cref="ISugarQueryable{T}"/> 便捷访问器，
/// 业务代码保持 <c>dbContext.Sites</c> 等用法不变，底层换成 SqlSugar 的查询/插入/删除能力。
/// </para>
/// </summary>
public sealed class AppDbContext : IDisposable, IAsyncDisposable
{
    private readonly ISqlSugarClient _client;
    /// <summary>
    /// 全局 SQLite 串行化锁。SqlSugarScope 单例在多后台服务并发时会踩 SqliteCommand 集合竞态
    /// （Index out of range / Collection was modified / ObjectDisposed），
    /// 用一把全局异步锁让所有 DB 操作串行执行，从根上消除并发问题。
    /// </summary>
    private readonly SemaphoreSlim _dbLock;

    /// <summary>
    /// 释放资源。注意：底层 SqlSugarScope 是 DI 管理的单例，这里不真正释放它；
    /// 此方法仅为兼容原 EF 代码中 dbContext.Dispose()/await using 的调用模式（空操作）。
    /// </summary>
    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// 底层 SqlSugar 客户端，供需要高级操作（事务、原生 SQL、整表 Deleteable）的代码使用。
    /// </summary>
    public ISqlSugarClient Client => _client;

    /// <summary>
    /// 在全局 SQLite 串行化锁内执行一次完整的 DB 访问块。
    /// 供后台服务（巡检/批量写/冷却恢复）使用，确保彼此串行，避免 SqlSugarScope 单例的并发竞态。
    /// 调用方需把"从查到写"的完整逻辑作为委托传入。
    /// </summary>
    public async Task<T> SerialExecuteAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// 在全局 SQLite 串行化锁内执行无返回值的 DB 访问块（重载）。
    /// </summary>
    public async Task SerialExecuteAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        await _dbLock.WaitAsync(cancellationToken);
        try
        {
            await action();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // —— 与原 DbSet 同名的便捷查询访问器 ——
    public ISugarQueryable<Site> Sites => _client.Queryable<Site>();
    public ISugarQueryable<CodexAccount> CodexAccounts => _client.Queryable<CodexAccount>();
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
    public ISugarQueryable<CompatibilityProfile> CompatibilityProfiles => _client.Queryable<CompatibilityProfile>();

    /// <summary>
    /// 由 DI 注入的 SqlSugar 客户端构造。
    /// </summary>
    /// <param name="client">SqlSugar 单例客户端。</param>
    /// <param name="dbLock">全局 SQLite 串行化锁，供 SerialExecuteAsync 使用。</param>
    public AppDbContext(ISqlSugarClient client, SemaphoreSlim dbLock)
    {
        _client = client;
        _dbLock = dbLock;
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

    /// <summary>更新单条实体（替代 EF 赋值 + SaveChanges）。</summary>
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
    /// <para>
    /// <b>坑#3 注意</b>：SqlSugar 在 SQLite 上对 <c>Deleteable&lt;T&gt;().Where(predicate)</c> 翻译复杂表达式
    /// （含 DateTimeOffset 闭包变量、null 比较等）时会生成错误 SQL（如 <c>near "IS": syntax error</c>）。
    /// 因此<b>不要</b>对复杂谓词调用本方法；改在调用点用「先 Queryable.Select(Id) 取 Id，再 Deleteable.In(ids) 删除」。
    /// 本方法仅适用于简单、可稳定翻译的谓词。
    /// </para>
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
            // 保持自动关闭连接=true（项目里 27 个文件、141 处执行点都依赖它自动开关连接，改成 false 需全部手动 Open/Close）。
            // SQLite 多线程并发竞态改由 AppDbContext.SerialExecuteAsync 全局锁根治：
            // 同一时刻只有一个 DB 操作在跑，"自动关连接误释放别线程 command"的竞态自然消失。
            IsAutoCloseConnection = true,
            MoreSettings = new ConnMoreSettings
            {
                IsAutoRemoveDataCache = true
            }
        });

        // WAL 模式是持久化的，但首次建库时仍需确保设置一次；在 InitTables 阶段执行。
        services.AddSingleton<ISqlSugarClient>(sqlSugar);
        // 全局 SQLite 串行化锁（单例），供 AppDbContext.SerialExecuteAsync 串行化后台 DB 操作。
        services.AddSingleton<SemaphoreSlim>(_ => new SemaphoreSlim(1, 1));
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
            typeof(CodexAccount),
            typeof(ModelLibraryItem),
            typeof(SiteModelMapping),
            typeof(DetectionTask),
            typeof(DetectionTaskExecution),
            typeof(ProxyRouteEntry),
            typeof(ProxyRouteRule),
            typeof(ProxyAccessKey),
            typeof(ProxyUsageLog),
            typeof(ModelHealthMonitor),
            typeof(SystemRuntimeSettings),
            typeof(CompatibilityProfile));
    }
}
