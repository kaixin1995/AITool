using System.Data;
using System.Linq.Expressions;
using AITool.Domain.Codex;
using AITool.Domain.Detection;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    /// 后台 DB 操作串行化锁。仅 <see cref="SerialExecuteAsync"/> 使用，
    /// 供后台服务（巡检/批量写/冷却恢复）彼此串行，避免与代理热路径的批量写踩 SqlSugarScope 竞态。
    /// <para>
    /// 注意：Web 请求路径（控制器的 Insert/Update/Delete）<b>不</b>走此锁——它们依赖
    /// SqlSugarScope 自身的线程安全性 + SQLite WAL 模式 + busy_timeout 处理写冲突。
    /// 给所有写加全局锁会严重拖慢并发，且管理后台写并发量低，无需如此。
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _dbLock;

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

    /// <summary>
    /// 在后台 DB 串行化锁内执行一次完整的 DB 访问块。
    /// <b>仅供后台服务</b>（巡检/批量写/冷却恢复）使用，确保彼此串行，避免与代理热路径批量写踩 SqlSugarScope 竞态。
    /// Web 请求路径（控制器）<b>不要</b>调用此方法——会破坏并发性能，且 Web 写并发量低无需串行。
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
    public ISugarQueryable<SiteKey> SiteKeys => _client.Queryable<SiteKey>();
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
    public ISugarQueryable<SqlMigrationExecution> SqlMigrationExecutions => _client.Queryable<SqlMigrationExecution>();

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
            // 保持自动关闭连接=true（项目里 27 个文件、141 处执行点都依赖它自动开关连接，改成 false 需全部手动 Open/Close）。
            // SQLite 多线程并发竞态：后台批量写走 AppDbContext.SerialExecuteAsync 串行；
            // Web 请求路径依赖 SqlSugarScope 自身线程安全 + WAL + busy_timeout，不加全局锁以保并发性能。
            IsAutoCloseConnection = true,
            MoreSettings = new ConnMoreSettings
            {
                IsAutoRemoveDataCache = true
            }
        },
        config =>
        {
            // SqlSugar 存储 DateTimeOffset 时只存时钟值（不带 offset），读取时配本地时区 offset。
            // 若写入的是 UTC（+00:00），读回配 +08:00 会导致瞬时偏移（时钟值不变但 offset 变了）。
            // 这里在写入前把所有 DateTimeOffset 转为本地时区，使存储的时钟值与读回的 offset 一致，
            // 保证往返瞬时正确。
            config.Aop.DataExecuting = (oldValue, entityInfo) =>
            {
                if (entityInfo.OperationType == DataFilterType.InsertByObject
                    || entityInfo.OperationType == DataFilterType.UpdateByObject)
                {
                    if (oldValue is DateTimeOffset dto)
                    {
                        entityInfo.SetValue(dto.ToLocalTime());
                    }
                }
            };

            // 查询参数侧的对称修复：DataExecuting 只作用于实体写入，不作用于 Where 条件参数。
            // 若查询参数是 UTC（如 DateTimeOffset.UtcNow）而存储值是本地时钟，SQLite 按字符串比较
            // 会导致所有时间过滤系统性偏移一个时区（UTC+8 部署下即 8 小时：token 刷新判定、冷却恢复、
            // 日志保留清理等全部延后）。这里在命令执行前把所有 DateTimeOffset 参数统一转本地时钟，
            // 与存储侧对齐；对已是本地时区的参数（前端传入的本地时间）ToLocalTime 是幂等操作。
            config.Aop.OnExecutingChangeSql = (sql, pars) =>
            {
                if (pars is { Length: > 0 })
                {
                    foreach (var parameter in pars)
                    {
                        if (parameter?.Value is DateTimeOffset dto)
                        {
                            parameter.Value = dto.ToLocalTime();
                        }
                    }
                }

                return new KeyValuePair<string, SugarParameter[]>(sql, pars);
            };
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
    public static void InitializeDatabase(ISqlSugarClient db, ILogger? logger = null)
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
        catch (Exception ex)
        {
            // PRAGMA 失败会影响并发/锁行为，记录告警便于定位；不抛出以放行建表。
            logger?.LogWarning(ex, "Failed to apply SQLite PRAGMA during database initialization");
        }

        // CodeFirst 建表（表已存在时只增不删，自动补齐缺失列）。
        db.CodeFirst.InitTables(
            typeof(Site),
            typeof(SiteKey),
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
            typeof(CompatibilityProfile),
            typeof(SqlMigrationExecution),
            typeof(AITool.Domain.Auth.RefreshTokenRecord));

        // 一次性数据迁移：把老站点的 Site.ApiKey 复制成一条默认 SiteKey，保证老站点立即具备多 Key 能力。
        // 仅迁移用户自建站点（ManagedSource 为空且 ApiKey 非空）；Codex 托管站点不迁移，仍直接用 Site.ApiKey。
        // 迁移幂等：已存在 SiteKey 记录的站点跳过，可重复执行。
        MigrateLegacySiteKeys(db);
    }

    /// <summary>
    /// 把老站点的 <see cref="Site.ApiKey"/> 迁移为一条默认 <see cref="SiteKey"/>。
    /// <para>
    /// 仅处理用户自建站点（<see cref="Site.ManagedSource"/> 为空且 ApiKey 非空）。
    /// Codex 托管站点不迁移——它们恰好一个 token，仍直接使用 Site.ApiKey，
    /// 缓存层对没有 SiteKey 的站点会回退用 Site.ApiKey 产出单条候选，行为不变。
    /// </para>
    /// <para>
    /// 幂等：通过检查"目标站点是否已有任意 SiteKey"避免重复迁移，可安全多次执行。
    /// 迁移失败不影响启动（异常被吞掉并记录到控制台），下次启动会重试。
    /// </para>
    /// </summary>
    private static void MigrateLegacySiteKeys(ISqlSugarClient db)
    {
        try
        {
            // 仅查自建站点且 ApiKey 非空
            var legacySites = db.Queryable<Site>()
                .Where(x => SqlFunc.IsNullOrEmpty(x.ManagedSource) && !SqlFunc.IsNullOrEmpty(x.ApiKey))
                .Select(x => new { x.Id, x.ApiKey })
                .ToList();
            if (legacySites.Count == 0)
            {
                return;
            }

            // 已有 SiteKey 记录的站点集合，避免重复迁移
            var migratedSiteIds = db.Queryable<SiteKey>()
                .Select(x => x.SiteId)
                .ToList()
                .ToHashSet();

            var toInsert = new List<SiteKey>();
            foreach (var site in legacySites)
            {
                if (migratedSiteIds.Contains(site.Id))
                {
                    continue;
                }

                toInsert.Add(new SiteKey
                {
                    SiteId = site.Id,
                    KeyValue = site.ApiKey,
                    Remark = "默认",
                    Priority = 0,
                    IsEnabled = true,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

            if (toInsert.Count > 0)
            {
                db.Insertable(toInsert).ExecuteCommand();
                Console.WriteLine($"[Migration] 已为 {toInsert.Count} 个老站点创建默认 SiteKey（迁移自 Site.ApiKey）。");
            }
        }
        catch (Exception ex)
        {
            // 迁移失败不阻断启动，下次启动会重试（幂等）
            Console.WriteLine($"[Migration] 老站点 SiteKey 迁移失败，将在下次启动重试：{ex.Message}");
        }
    }
}
