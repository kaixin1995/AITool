using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Text;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Proxy;

public sealed class GatewayWebSocketTurn : IAsyncDisposable
{
    private readonly IDisposable? _admission;
    private readonly GatewayRequestContext _context;
    private readonly AppDbContext _db;
    private readonly GatewayBudgetGate _gate;
    private readonly GatewayRequestMetric _metric;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly bool _budgeted;
    private readonly CancellationToken _requestAborted;
    private readonly ILogger<GatewayWebSocketTurn> _logger;
    private GatewayWebSocketTurn(IDisposable? admission, GatewayRequestContext context, AppDbContext db,
        GatewayBudgetGate gate, GatewayRequestMetric metric, bool budgeted, CancellationToken requestAborted, ILogger<GatewayWebSocketTurn> logger)
    { _admission = admission; _context = context; _db = db; _gate = gate; _metric = metric; _budgeted = budgeted; _requestAborted = requestAborted; _logger = logger; }

    public static async Task<GatewayWebSocketTurn?> BeginAsync(HttpContext http, Guid keyId, string raw, CancellationToken ct)
    {
        var settings = http.RequestServices.GetRequiredService<GatewayEnhancementSettings>();
        if (settings.LoadError is { } loadError) throw new InvalidOperationException(loadError);
        var options = settings.Current;
        var budgets = options.Budgets.Where(b => b.KeyId == keyId && (b.TokenLimit > 0 || b.CostLimitUsd > 0)).ToList();
        if (budgets.Count == 0) return null;
        var gate = http.RequestServices.GetRequiredService<GatewayBudgetGate>();
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        IDisposable? admission = budgets.Count > 0 ? await gate.EnterAsync(keyId, ct) : null;
        try
        {
            if (budgets.Count > 0 && gate.HasUnsettled(keyId)) throw new InvalidOperationException("先前请求预算结算失败，请检查数据库后重启服务。");
            if (budgets.Count > 0 && await GatewayBudgetGate.CheckAsync(db, keyId, budgets, ct) is { } error) throw new InvalidOperationException(error);
            var context = http.RequestServices.GetRequiredService<GatewayRequestContext>();
            context.Reset(); context.Enabled = true;
            return new(admission, context, db, gate, new GatewayRequestMetric { AccessKeyId = keyId,
                StartedUtcTicks = DateTimeOffset.UtcNow.UtcTicks, RequestBytes = Encoding.UTF8.GetByteCount(raw) }, budgets.Count > 0, http.RequestAborted,
                http.RequestServices.GetRequiredService<ILogger<GatewayWebSocketTurn>>());
        }
        catch { admission?.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _metric.Tokens = _context.Tokens; _metric.CostUsd = _context.CostUsd; _metric.Unpriced = _context.Unpriced;
            _metric.DurationMs = _watch.ElapsedMilliseconds; _metric.ResponseBytes = _context.ResponseBytes;
            _metric.StatusCode = _requestAborted.IsCancellationRequested ? 499 : _context.Failed ? 502 : 200;
            _metric.Outcome = _requestAborted.IsCancellationRequested ? "canceled" : _context.Failed ? "failed" : "completed";
            await _db.Client.Insertable(_metric).ExecuteCommandAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket 用量结算失败");
            if (_budgeted) _gate.MarkUnsettled(_metric.AccessKeyId);
        }
        finally { _context.Enabled = false; _admission?.Dispose(); }
    }
}
