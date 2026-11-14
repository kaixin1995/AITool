using AITool.Application.Pricing;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

[ApiController]
[Route("api/admin/gateway-enhancements")]
public sealed class GatewayEnhancementsApiController(GatewayEnhancementSettings settings, AppDbContext db,
    IModelPricingService pricing) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var options = settings.Current;
        options.SearchApiKey = options.SearchApiKey.Length > 0 ? "********" : "";
        foreach (var server in options.McpServers) server.ApiKey = server.ApiKey.Length > 0 ? "********" : "";
        return Ok(options);
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(new { configurationError = settings.LoadError });

    [HttpPost]
    public async Task<IActionResult> Save(GatewayEnhancementOptions options, CancellationToken ct)
    {
        if (options.ToolAllowedKeyIds is null || options.Budgets is null || options.Budgets.Any(b => b is null) || options.McpServers is null || options.McpServers.Any(s => s is null || s.AllowedKeyIds is null))
            return BadRequest(new { message = "预算和 MCP 配置不能为 null。" });
        var previous = settings.Current;
        if (options.SearchApiKey == "********") options.SearchApiKey = previous.SearchApiKey;
        foreach (var server in options.McpServers)
            if (server.ApiKey == "********")
            {
                var original = previous.McpServers.FirstOrDefault(s => s.Name == server.Name);
                if (original is null) return BadRequest(new { message = "修改 MCP 服务名称后请重新填写密钥。" });
                server.ApiKey = original.ApiKey;
            }
        var ids = (await db.ProxyAccessKeys.Select(k => k.Id).ToListAsync(ct)).ToHashSet();
        // Deleted keys can remain in the saved snapshot. Remove only inherited stale
        // references; a new invalid ID (including one moved into another allowlist) is still an error.
        if (options.ToolAllowedKeyIds.Any(id => !ids.Contains(id) && !previous.ToolAllowedKeyIds.Contains(id))
            || options.Budgets.Any(b => !ids.Contains(b.KeyId) && !previous.Budgets.Any(p => p.KeyId == b.KeyId))
            || options.McpServers.Any(s => s.AllowedKeyIds.Any(id => !ids.Contains(id)
                && !previous.McpServers.Any(p => p.Name == s.Name && p.AllowedKeyIds.Contains(id)))))
            return BadRequest(new { message = "配置引用了不存在的访问密钥。" });
        options.ToolAllowedKeyIds.RemoveAll(id => !ids.Contains(id));
        options.Budgets.RemoveAll(b => !ids.Contains(b.KeyId));
        foreach (var server in options.McpServers) server.AllowedKeyIds.RemoveAll(id => !ids.Contains(id));
        try
        {
            var saved = await settings.SaveAsync(options, ct, options.Revision);
            saved.SearchApiKey = saved.SearchApiKey.Length > 0 ? "********" : "";
            foreach (var server in saved.McpServers) server.ApiKey = server.ApiKey.Length > 0 ? "********" : "";
            return Ok(new { saved = true, revision = saved.Revision, configuration = saved });
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions(int days = 7, CancellationToken ct = default)
    {
        var after = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90));
        // Budget windows can span a month; never erase the ledger of an active window.
        var retainAfter = DateTimeOffset.UtcNow.AddDays(-Math.Max(32, settings.Current.MetricsRetentionDays)).UtcTicks;
        await db.Client.Deleteable<GatewayRequestMetric>().Where(m => m.StartedUtcTicks < retainAfter).ExecuteCommandAsync(ct);
        var rows = await db.ProxyUsageLogs.Where(l => l.RequestedAt >= after && l.SessionDigest != null && l.SessionDigest != "")
            .OrderByDescending(l => l.RequestedAt).Take(20_000).ToListAsync(ct);
        var metrics = await db.Client.Queryable<GatewayRequestMetric>().Where(m => m.StartedUtcTicks >= after.UtcTicks)
            .OrderByDescending(m => m.StartedUtcTicks).Take(20_000).ToListAsync(ct);
        var grouped = rows.GroupBy(l => l.SessionDigest!).Select(g =>
        {
            var traffic = metrics.Where(m => m.SessionDigest == g.Key).ToList();
            var costs = g.Select(l => pricing.CalculateCostUsd(l.AttemptedModel, l.RequestedAt, l.InputTokens, l.CachedTokens, l.OutputTokens).CostUsd).ToList();
            return new { session = g.Key, parent = g.Select(l => l.ParentSessionDigest).FirstOrDefault(p => !string.IsNullOrEmpty(p)),
                requests = g.Select(l => l.RequestId).Distinct().Count(), attempts = g.Count(),
                inputTokens = g.Sum(l => (long)l.InputTokens), cachedTokens = g.Sum(l => (long)l.CachedTokens), outputTokens = g.Sum(l => (long)l.OutputTokens),
                costUsd = costs.Sum(c => c ?? 0), unpricedAttempts = costs.Count(c => c is null),
                requestBytes = traffic.Sum(t => t.RequestBytes), responseBytes = traffic.Sum(t => t.ResponseBytes),
                switches = g.OrderBy(l => l.RequestedAt).Select(l => l.TargetSiteId).Zip(g.OrderBy(l => l.RequestedAt).Select(l => l.TargetSiteId).Skip(1)).Count(p => p.First != p.Second),
                lastUsed = g.Max(l => l.RequestedAt), latestContextTokens = g.OrderByDescending(l => l.RequestedAt).First().InputTokens + g.OrderByDescending(l => l.RequestedAt).First().CachedTokens };
        }).OrderByDescending(s => s.lastUsed).Take(200);
        return Ok(grouped);
    }

    [HttpGet("budgets")]
    public async Task<IActionResult> Budgets(CancellationToken ct)
    {
        var result = new List<object>();
        foreach (var budget in settings.Current.Budgets)
        {
            var start = GatewayBudgetGate.WindowStart(budget.Period, DateTimeOffset.UtcNow).UtcTicks;
            var query = db.Client.Queryable<GatewayRequestMetric>().Where(r => r.AccessKeyId == budget.KeyId && r.StartedUtcTicks >= start);
            result.Add(new { budget.KeyId, budget.Period, budget.TokenLimit, budget.CostLimitUsd,
                usedTokens = await query.Clone().SumAsync(r => r.Tokens), usedCostUsd = await query.Clone().SumAsync(r => r.CostUsd),
                hasUnpricedUsage = await query.Clone().Where(r => r.Unpriced).AnyAsync() });
        }
        return Ok(result);
    }

    /// <summary>未携带可识别会话标识的请求仍记入普通使用日志，不将它们猜测合并为会话。</summary>
    [HttpGet("sessions/coverage")]
    public async Task<IActionResult> SessionCoverage(int days = 7, CancellationToken ct = default)
    {
        var after = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 90)).UtcTicks;
        var metrics = db.Client.Queryable<GatewayRequestMetric>().Where(m => m.StartedUtcTicks >= after);
        var total = await metrics.Clone().CountAsync();
        var unidentified = await metrics.Clone().Where(m => m.SessionDigest == null || m.SessionDigest == "").CountAsync();
        return Ok(new { totalRequests = total, unidentifiedRequests = unidentified });
    }

    [HttpGet("sessions/{digest}")]
    public async Task<IActionResult> Session(string digest, CancellationToken ct)
    {
        if (digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigit(c))) return BadRequest();
        var rows = await db.ProxyUsageLogs.Where(l => l.SessionDigest == digest)
            .OrderByDescending(l => l.RequestedAt).Take(1000).ToListAsync(ct);
        return Ok(rows.OrderBy(l => l.RequestedAt).Select(l => new {
            l.RequestId, l.AttemptIndex, l.RequestedAt, l.Status, l.RequestModel, l.AttemptedModel, l.TargetSiteId,
            l.InputTokens, l.CachedTokens, l.OutputTokens, l.TotalTokens, l.FirstTokenLatencyMs, l.TotalDurationMs,
            l.FallbackTriggered, l.ErrorCategory, l.ParentSessionDigest,
            contextTokens = (long)l.InputTokens + l.CachedTokens,
            cacheShare = l.InputTokens + l.CachedTokens == 0 ? 0 : (double)l.CachedTokens / ((long)l.InputTokens + l.CachedTokens),
            costUsd = pricing.CalculateCostUsd(l.AttemptedModel, l.RequestedAt, l.InputTokens, l.CachedTokens, l.OutputTokens).CostUsd
        }));
    }
}
