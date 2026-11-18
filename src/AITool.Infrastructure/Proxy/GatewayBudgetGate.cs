using System.Collections.Concurrent;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Proxy;

/// <summary>Configured keys serialize admission through settlement so concurrent calls cannot bypass observed usage.</summary>
public sealed class GatewayBudgetGate
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, byte> _unsettled = new();
    public bool HasUnsettled(Guid key) => _unsettled.ContainsKey(key);
    public void MarkUnsettled(Guid key) => _unsettled[key] = 0;
    public async Task<IDisposable> EnterAsync(Guid key, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }
    private sealed class Release(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }

    public static DateTimeOffset WindowStart(string period, DateTimeOffset utc)
    {
        var date = utc.UtcDateTime.Date;
        return period switch
        {
            "week" => new DateTimeOffset(date.AddDays(-((int)date.DayOfWeek + 6) % 7), TimeSpan.Zero),
            "month" => new DateTimeOffset(new DateTime(date.Year, date.Month, 1), TimeSpan.Zero),
            _ => new DateTimeOffset(date, TimeSpan.Zero)
        };
    }
    public static async Task<string?> CheckAsync(AppDbContext db, Guid key, IEnumerable<GatewayKeyBudget> budgets, CancellationToken ct)
    {
        foreach (var budget in budgets)
        {
            var start = WindowStart(budget.Period, DateTimeOffset.UtcNow).UtcTicks;
            var query = db.Client.Queryable<GatewayRequestMetric>().Where(r => r.AccessKeyId == key && r.StartedUtcTicks >= start);
            var tokens = await query.Clone().SumAsync(r => r.Tokens);
            var cost = await query.Clone().SumAsync(r => r.CostUsd);
            var unknown = await query.Clone().Where(r => r.Unpriced).AnyAsync();
            if (budget.TokenLimit > 0 && tokens >= budget.TokenLimit) return "当前周期的 token 预算已用完。";
            if (budget.CostLimitUsd > 0 && (cost >= budget.CostLimitUsd || unknown)) return unknown ? "当前周期存在未定价用量，金额预算已暂停。请先配置价格，等待新周期或改用 token 预算。" : "当前周期的金额预算已用完。";
        }
        return null;
    }
}
