using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

[ApiController]
[Route("api/admin/gateway-enhancements")]
public sealed class GatewayEnhancementsApiController(GatewayEnhancementSettings settings, AppDbContext db) : ControllerBase
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

    [HttpGet("budgets")]
    public async Task<IActionResult> Budgets(CancellationToken ct)
    {
        // Budget views are the only remaining consumer of the request metric ledger; prune
        // expired rows on access. Budget windows can span a month; never erase an active window.
        var retainAfter = DateTimeOffset.UtcNow.AddDays(-Math.Max(32, settings.Current.MetricsRetentionDays)).UtcTicks;
        await db.Client.Deleteable<GatewayRequestMetric>().Where(m => m.StartedUtcTicks < retainAfter).ExecuteCommandAsync(ct);
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
}
