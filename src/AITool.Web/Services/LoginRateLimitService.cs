using System.Collections.Concurrent;

namespace AITool.Web.Services;

/// <summary>
/// 登录暴力破解防护：按 IP 统计连续失败次数，超过阈值后锁定一段时间。
/// 内存存储（重启清零），适合单实例部署。
/// </summary>
public sealed class LoginRateLimitService
{
    /// <summary>
    /// 最大连续失败次数，超过后锁定。
    /// </summary>
    private const int MaxFailedAttempts = 5;

    /// <summary>
    /// 锁定时长。
    /// </summary>
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 失败记录：IP → (失败次数, 锁定截止时间)。
    /// </summary>
    private readonly ConcurrentDictionary<string, FailureRecord> _failures = new();

    /// <summary>
    /// 下一次惰性清理的时间戳，避免每次登录请求都遍历全部 IP。
    /// </summary>
    private long _nextCleanupTicks = DateTime.UtcNow.AddMinutes(5).Ticks;

    /// <summary>
    /// 检查指定 IP 是否被锁定。
    /// </summary>
    /// <returns>锁定时返回剩余秒数；未锁定返回 null。</returns>
    public int? CheckLocked(string ip)
    {
        CleanupIfDue();
        if (!_failures.TryGetValue(ip, out var record))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (record.LockedUntil > now)
        {
            return (int)Math.Ceiling((record.LockedUntil - now).TotalSeconds);
        }

        return null;
    }

    /// <summary>
    /// 记录一次登录失败。达到阈值时自动锁定。
    /// </summary>
    public void RecordFailure(string ip)
    {
        CleanupIfDue();
        var now = DateTimeOffset.UtcNow;
        _failures.AddOrUpdate(
            ip,
            // 首次失败：count=1，未达到阈值不锁定
            _ => new FailureRecord(1, now, now),
            (_, existing) =>
            {
                // 如果之前的锁定已过期，重新计数
                var count = existing.LockedUntil > now ? existing.Count + 1 : 1;
                // 只有达到阈值才设置锁定截止时间，否则设为当前时间（表示未锁定）
                var lockedUntil = count >= MaxFailedAttempts ? now.Add(LockDuration) : now;
                return new FailureRecord(count, now, lockedUntil);
            });
    }

    /// <summary>
    /// 登录成功时清除该 IP 的失败记录。
    /// </summary>
    public void RecordSuccess(string ip)
    {
        CleanupIfDue();
        _failures.TryRemove(ip, out _);
    }

    /// <summary>
    /// 惰性清理过期记录（避免字典无限增长）。
    /// </summary>
    public void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _failures)
        {
            if (pair.Value.LockedUntil <= now && pair.Value.LastFailedAt < now.AddHours(-1))
            {
                _failures.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <summary>
    /// 按固定时间间隔触发过期记录清理。
    /// </summary>
    private void CleanupIfDue()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var scheduledTicks = Volatile.Read(ref _nextCleanupTicks);
        if (nowTicks < scheduledTicks
            || Interlocked.CompareExchange(
                ref _nextCleanupTicks,
                nowTicks + TimeSpan.FromMinutes(5).Ticks,
                scheduledTicks) != scheduledTicks)
        {
            return;
        }

        Cleanup();
    }

    private sealed record FailureRecord(int Count, DateTimeOffset LastFailedAt, DateTimeOffset LockedUntil);
}
