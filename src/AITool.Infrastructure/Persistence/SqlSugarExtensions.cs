using System.Linq.Expressions;
using System.Reflection;
using SqlSugar;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// SqlSugar 的 ISugarQueryable 扩展方法，提供与 EF Core 兼容的异步终端操作 + DateTimeOffset offset 规范化。
/// <para>
/// SqlSugar 在 SQLite 下读回 DateTimeOffset 时配本地时区 offset（如 +8h），而存储的是 UTC 值（无 offset 字符串），
/// 导致瞬时时刻偏移。Aop.DataExecuted 在 ToList 等路径下不触发，因此这里通过包装终端操作，
/// 在结果物化后统一把 DateTimeOffset 的 offset 规范化回 +00:00（UTC）。
/// 扩展方法内部调用 query.ToListAsync 会命中 SqlSugar 原生实例方法（优先级高于扩展方法），不会递归。
/// </para>
/// </summary>
public static class SqlSugarQueryableExtensions
{
    /// <summary>
    /// 把实体上所有 DateTimeOffset 属性的 offset 规范化为 +00:00（UTC），补偿 SqlSugar 读回时配本地 offset 的问题。
    /// </summary>
    private static void NormalizeDateTimeOffsets<T>(List<T> list)
    {
        if (list is null || list.Count == 0) return;
        var type = typeof(T);
        var dateProps = type.GetProperties().Where(p => p.PropertyType == typeof(DateTimeOffset)).ToList();
        if (dateProps.Count == 0) return;

        foreach (var item in list)
        {
            if (item is null) continue;
            foreach (var prop in dateProps)
            {
                var current = (DateTimeOffset)prop.GetValue(item)!;
                if (current.Offset != TimeSpan.Zero)
                {
                    // 保留时间值（DateTime 部分），只把 offset 改回 +00:00，恢复正确瞬时时刻。
                    prop.SetValue(item, new DateTimeOffset(current.DateTime, TimeSpan.Zero));
                }
            }
        }
    }

    /// <summary>单实体的 DateTimeOffset offset 规范化（用于 First/Single 等单结果）。</summary>
    private static T NormalizeSingle<T>(T? item) where T : class
    {
        if (item is null) return default!;
        var type = typeof(T);
        foreach (var prop in type.GetProperties().Where(p => p.PropertyType == typeof(DateTimeOffset)))
        {
            var current = (DateTimeOffset)prop.GetValue(item)!;
            if (current.Offset != TimeSpan.Zero)
            {
                prop.SetValue(item, new DateTimeOffset(current.DateTime, TimeSpan.Zero));
            }
        }
        return item;
    }

    // —— DateTimeOffset offset 规范化的终端操作包装（关键：命中原生实例方法，不递归）——

    /// <summary>ToListAsync 包装：物化后规范化 DateTimeOffset offset。</summary>
    public static async Task<List<T>> ToListAsync<T>(this ISugarQueryable<T> query, CancellationToken cancellationToken = default)
    {
        var list = await query.ToListAsync(cancellationToken);
        NormalizeDateTimeOffsets(list);
        return list;
    }

    /// <summary>ToList（同步）包装：物化后规范化 DateTimeOffset offset。</summary>
    public static List<T> ToList<T>(this ISugarQueryable<T> query)
    {
        var list = query.ToList();
        NormalizeDateTimeOffsets(list);
        return list;
    }

    // 注意：FirstAsync/InSingleAsync 的包装方法会导致递归（SqlSugar 的这些方法是扩展方法而非实例方法，
    // 编译时解析到自定义包装而非原生实现）。因此不包装 FirstAsync/InSingleAsync，
    // 直接使用 SqlSugar 原生扩展方法。DateTimeOffset 规范化仅在 ToListAsync 包装中处理
    // （ToListAsync 是 SqlSugar 的实例方法，包装方法内部调用不会递归）。
    // 对于通过 FirstAsync/InSingleAsync 查回的实体，调用方在内存比较 DateTimeOffset 时
    // 需自行用 DateTime 部分比较（AnalyticsApiController 已在读后统一规范化）。

    // —— 其他 EF 兼容扩展（不涉及 DateTimeOffset，纯转发）——

    /// <summary>兼容 EF 的 ThenBy（SqlSugar 用 OrderBy 链式实现多级排序）。</summary>
    public static ISugarQueryable<T> ThenBy<T, TKey>(this ISugarQueryable<T> query, Expression<Func<T, TKey>> keySelector)
        => query.OrderBy(ConvertToObjectSelector(keySelector));

    /// <summary>兼容 EF 的 ThenByDescending。</summary>
    public static ISugarQueryable<T> ThenByDescending<T, TKey>(this ISugarQueryable<T> query, Expression<Func<T, TKey>> keySelector)
        => query.OrderBy(ConvertToObjectSelector(keySelector), OrderByType.Desc);

    private static Expression<Func<T, object>> ConvertToObjectSelector<T, TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var parameter = keySelector.Parameters[0];
        var body = Expression.Convert(keySelector.Body, typeof(object));
        return Expression.Lambda<Func<T, object>>(body, parameter);
    }

    /// <summary>兼容 EF 的 ToDictionaryAsync 三参数重载。</summary>
    public static async Task<Dictionary<TKey, TValue>> ToDictionaryAsync<T, TKey, TValue>(
        this ISugarQueryable<T> query,
        Func<T, TKey> keySelector,
        Func<T, TValue> valueSelector,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        var list = await query.ToListAsync(cancellationToken);
        return list.ToDictionary(keySelector, valueSelector);
    }

    /// <summary>兼容 EF 的 AnyAsync(谓词)。</summary>
    public static async Task<bool> AnyAsync<T>(
        this ISugarQueryable<T> query,
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
        => await query.Where(predicate).AnyAsync(cancellationToken);

    // —— 写操作兼容（立即执行，替代 EF 的 Add/Remove + SaveChanges 两步模式）——
    // 注意：必须用同步 ExecuteCommand（不是异步），因为原 EF 代码里 dbSet.Add(entity)
    // 是同步调用且无 await（EF 的 Add 返回 void）。如果用异步 ExecuteCommandAsync
    // 且调用方不 await，Task 会被丢弃导致写操作不执行。同步执行确保立即落库。

    /// <summary>立即插入单条实体（兼容 EF 的 dbSet.Add，同步执行确保落库）。</summary>
    public static int Add<T>(this ISugarQueryable<T> query, T entity) where T : class, new()
        => query.Context.Insertable(entity).ExecuteCommand();

    /// <summary>立即批量插入（兼容 EF 的 dbSet.AddRange，接受 params 多对象）。</summary>
    public static int AddRange<T>(this ISugarQueryable<T> query, params T[] entities) where T : class, new()
        => query.Context.Insertable(entities.ToList()).ExecuteCommand();

    /// <summary>立即批量插入（兼容 EF 的 dbSet.AddRange，接受 IEnumerable）。</summary>
    public static int AddRange<T>(this ISugarQueryable<T> query, IEnumerable<T> entities) where T : class, new()
        => query.Context.Insertable(entities.ToList()).ExecuteCommand();

    /// <summary>立即删除单条实体（兼容 EF 的 dbSet.Remove，同步执行确保落库）。</summary>
    public static int Remove<T>(this ISugarQueryable<T> query, T entity) where T : class, new()
        => query.Context.Deleteable(entity).ExecuteCommand();

    /// <summary>立即批量删除指定实体集合（兼容 EF 的 dbSet.RemoveRange(list)，同步执行）。</summary>
    public static int RemoveRange<T>(this ISugarQueryable<T> query, IEnumerable<T> entities) where T : class, new()
        => query.Context.Deleteable(entities.ToList()).ExecuteCommand();

    /// <summary>立即清空整表（兼容 EF 的 dbSet.RemoveRange(dbSet)，同步执行）。</summary>
    public static int RemoveRange<T>(this ISugarQueryable<T> query, ISugarQueryable<T> _) where T : class, new()
        => query.Context.Deleteable<T>().ExecuteCommand();
}

/// <summary>AppDbContext 的 EF 兼容扩展（SaveChangesAsync 空操作）。</summary>
public static class AppDbContextEfCompatExtensions
{
    /// <summary>兼容 EF 的 SaveChangesAsync。SqlSugar 写操作立即执行，此方法为空操作。</summary>
    public static Task SaveChangesAsync(this AppDbContext _, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
