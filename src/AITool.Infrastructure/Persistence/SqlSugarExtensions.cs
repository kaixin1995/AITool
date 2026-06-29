using System.Linq.Expressions;
using SqlSugar;

namespace AITool.Infrastructure.Persistence;

/// <summary>
/// SqlSugar 的 ISugarQueryable 扩展方法，提供与 EF Core 兼容的异步终端操作。
/// <para>
/// SqlSugar 原生的 <c>ToDictionaryAsync</c> 不支持 (keySelector, valueSelector, cancellationToken) 三参数重载，
/// 此扩展先 <c>ToListAsync</c> 物化再在内存构建字典，保持与 EF 调用点完全兼容，迁移时无需改业务代码。
/// </para>
/// </summary>
public static class SqlSugarQueryableExtensions
{
    // —— 查询兼容 ——

    /// <summary>
    /// 兼容 EF 的 ThenBy（SqlSugar 用 OrderBy 链式实现多级排序）。
    /// SqlSugar 的 OrderBy 接受 Expression&lt;Func&lt;T, object&gt;&gt;。
    /// </summary>
    public static ISugarQueryable<T> ThenBy<T, TKey>(this ISugarQueryable<T> query, Expression<Func<T, TKey>> keySelector)
    {
        return query.OrderBy(ConvertToObjectSelector(keySelector));
    }

    /// <summary>
    /// 兼容 EF 的 ThenByDescending。
    /// </summary>
    public static ISugarQueryable<T> ThenByDescending<T, TKey>(this ISugarQueryable<T> query, Expression<Func<T, TKey>> keySelector)
    {
        return query.OrderBy(ConvertToObjectSelector(keySelector), OrderByType.Desc);
    }

    /// <summary>
    /// 将 Expression&lt;Func&lt;T, TKey&gt;&gt; 转换为 Expression&lt;Func&lt;T, object&gt;&gt;，适配 SqlSugar 的 OrderBy 签名。
    /// </summary>
    private static Expression<Func<T, object>> ConvertToObjectSelector<T, TKey>(Expression<Func<T, TKey>> keySelector)
    {
        var parameter = keySelector.Parameters[0];
        var body = Expression.Convert(keySelector.Body, typeof(object));
        return Expression.Lambda<Func<T, object>>(body, parameter);
    }

    /// <summary>
    /// 异步将查询结果投影为字典（兼容 EF 的 ToDictionaryAsync 三参数重载）。
    /// </summary>
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

    /// <summary>
    /// 异步判断是否存在满足条件的记录（兼容 EF 的 AnyAsync(predicate, ct)）。
    /// </summary>
    public static async Task<bool> AnyAsync<T>(
        this ISugarQueryable<T> query,
        Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return await query.Where(predicate).AnyAsync(cancellationToken);
    }

    // —— 写操作兼容（立即执行，替代 EF 的 Add/Remove + SaveChanges 两步模式）——
    // SqlSugar 没有变更跟踪器，写操作是立即执行的。这些扩展让原 EF 代码
    // `_dbContext.X.Add(e); await _dbContext.SaveChangesAsync(ct);` 无需改动即可工作：
    // Add/Remove/RemoveRange 立即执行，SaveChangesAsync 变为空操作。

    /// <summary>
    /// 立即插入单条实体（兼容 EF 的 dbSet.Add）。配合 <see cref="AppDbContextEfCompatExtensions.SaveChangesAsync"/> 使用。
    /// </summary>
    public static Task<int> Add<T>(this ISugarQueryable<T> query, T entity) where T : class, new()
    {
        return query.Context.Insertable(entity).ExecuteCommandAsync();
    }

    /// <summary>
    /// 立即批量插入（兼容 EF 的 dbSet.AddRange，接受 params 多对象）。
    /// </summary>
    public static Task<int> AddRange<T>(this ISugarQueryable<T> query, params T[] entities) where T : class, new()
    {
        return query.Context.Insertable(entities.ToList()).ExecuteCommandAsync();
    }

    /// <summary>
    /// 立即批量插入（兼容 EF 的 dbSet.AddRange，接受 IEnumerable）。
    /// </summary>
    public static Task<int> AddRange<T>(this ISugarQueryable<T> query, IEnumerable<T> entities) where T : class, new()
    {
        return query.Context.Insertable(entities.ToList()).ExecuteCommandAsync();
    }

    /// <summary>
    /// 立即删除单条实体（兼容 EF 的 dbSet.Remove）。
    /// </summary>
    public static Task<int> Remove<T>(this ISugarQueryable<T> query, T entity) where T : class, new()
    {
        return query.Context.Deleteable(entity).ExecuteCommandAsync();
    }

    /// <summary>
    /// 立即批量删除指定实体集合（兼容 EF 的 dbSet.RemoveRange(list)）。
    /// </summary>
    public static Task<int> RemoveRange<T>(this ISugarQueryable<T> query, IEnumerable<T> entities) where T : class, new()
    {
        return query.Context.Deleteable(entities.ToList()).ExecuteCommandAsync();
    }

    /// <summary>
    /// 立即清空整表（兼容 EF 的 dbSet.RemoveRange(dbSet) 这种把整个 DbSet 当集合清空的写法）。
    /// </summary>
    public static Task<int> RemoveRange<T>(this ISugarQueryable<T> query, ISugarQueryable<T> _) where T : class, new()
    {
        return query.Context.Deleteable<T>().ExecuteCommandAsync();
    }
}

/// <summary>
/// AppDbContext 的 EF 兼容扩展。
/// </summary>
public static class AppDbContextEfCompatExtensions
{
    /// <summary>
    /// 兼容 EF 的 SaveChangesAsync。SqlSugar 写操作是立即执行的（无变更跟踪），
    /// 此方法仅为兼容现有调用点，实际为空操作（写操作已在 Add/Remove 时立即落库）。
    /// </summary>
    public static Task SaveChangesAsync(this AppDbContext _, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
