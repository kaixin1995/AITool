using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using AITool.Web.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>模型健康监控 API：监控列表（含 48 段时间线）、添加/移除监控。</summary>
[ApiController]
[Route("api/admin/model-health")]
public sealed class ModelHealthApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ModelHealthApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>获取健康监控面板数据。</summary>
    [HttpGet]
    public async Task<IActionResult> GetDashboard([FromQuery] string? range, CancellationToken cancellationToken)
    {
        var result = await LoadDashboardAsync(range, cancellationToken);
        return Ok(ApiResponse.Ok(result));
    }

    /// <summary>添加模型到健康监控。</summary>
    [HttpPost("{modelId:guid}/monitor")]
    public async Task<IActionResult> AddMonitor(Guid modelId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.ModelHealthMonitors.AnyAsync(x => x.ModelLibraryItemId == modelId, cancellationToken);
        if (exists)
        {
            return Ok(ApiResponse.Ok("该模型已在监控列表中"));
        }

        var modelExists = await _dbContext.ModelLibraryItems.AnyAsync(x => x.Id == modelId, cancellationToken);
        if (!modelExists)
        {
            return NotFound(ApiResponse.Fail("模型不存在", "model_not_found"));
        }

        _dbContext.ModelHealthMonitors.Add(new ModelHealthMonitor { ModelLibraryItemId = modelId });
        return Ok(ApiResponse.Ok("监控已添加"));
    }

    /// <summary>从健康监控移除模型。</summary>
    [HttpDelete("{modelId:guid}/monitor")]
    public async Task<IActionResult> RemoveMonitor(Guid modelId, CancellationToken cancellationToken)
    {
        var monitor = (await _dbContext.ModelHealthMonitors
            .Where(m => m.ModelLibraryItemId == modelId)
            .Take(1)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();
        if (monitor is null)
        {
            return NotFound(ApiResponse.Fail("该模型未在监控列表中", "monitor_not_found"));
        }

        _dbContext.ModelHealthMonitors.Remove(monitor);
        return Ok(ApiResponse.Ok("监控已移除"));
    }

    private async Task<object> LoadDashboardAsync(string? range, CancellationToken cancellationToken)
    {
        var monitors = await _dbContext.ModelHealthMonitors.ToListAsync(cancellationToken);
        var monitoredModelIds = monitors.Select(m => m.ModelLibraryItemId).Distinct().ToList();
        var models = await _dbContext.ModelLibraryItems
            .Where(m => monitoredModelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);

        var orphanMonitors = monitors.Where(m => !models.ContainsKey(m.ModelLibraryItemId)).ToList();
        if (orphanMonitors.Count > 0)
        {
            _dbContext.ModelHealthMonitors.RemoveRange(orphanMonitors);
            monitors = monitors.Where(m => models.ContainsKey(m.ModelLibraryItemId)).ToList();
        }
        monitoredModelIds = monitors.Select(m => m.ModelLibraryItemId).Distinct().ToList();

        var monitoredModels = monitors
            .Select(m => new
            {
                modelLibraryItemId = m.ModelLibraryItemId,
                displayName = models[m.ModelLibraryItemId].ModelName
            })
            .OrderBy(m => m.displayName)
            .ToList();

        var availableModels = await _dbContext.ModelLibraryItems
            .Where(m => !monitoredModelIds.Contains(m.Id))
            .OrderBy(m => m.ModelName)
            .Select(m => new { id = m.Id, modelName = m.ModelName, displayName = m.ModelName })
            .ToListAsync(cancellationToken);

        var healthData = new Dictionary<Guid, List<object>>();
        if (monitoredModelIds.Count == 0)
        {
            return new
            {
                monitoredModels,
                availableModels,
                healthData,
                rangeOptions = new[] { new { value = "1d", label = "近 1 天" }, new { value = "7d", label = "近 7 天" }, new { value = "30d", label = "近 30 天" } }
            };
        }

        // 健康看板展示历史调用，不按当前启用状态排除映射、站点或路由。
        var mappings = await _dbContext.SiteModelMappings
            .Where(m => monitoredModelIds.Contains(m.ModelLibraryItemId))
            .ToListAsync(cancellationToken);
        var siteIds = mappings.Select(m => m.SiteId).Distinct().ToList();
        var sites = await _dbContext.Sites
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

        var recentCutoff = ResolveRecentCutoff(range);
        var matchedModelNames = monitoredModelIds
            .Select(id => models.TryGetValue(id, out var m) ? m.ModelName : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);
        var routeRules = await _dbContext.ProxyRouteRules
            .Where(x => matchedModelNames.Contains(x.ExternalModelName))
            .ToListAsync(cancellationToken);
        // Logs do not retain a route ID or rule validity period. An attempted name alone
        // (even a current remote name) cannot prove which external model owned the call.
        var allLogs = await _dbContext.ProxyUsageLogs
            .Where(x => x.RequestedAt >= recentCutoff && matchedModelNames.Contains(x.RequestModel))
            .ToListAsync(cancellationToken);

        var modelSummaries = new List<object>();
        foreach (var modelId in monitoredModelIds)
        {
            var modelMappings = mappings
                .Where(m => m.ModelLibraryItemId == modelId && sites.ContainsKey(m.SiteId))
                .ToList();
            var modelName = models.TryGetValue(modelId, out var currentModel) ? currentModel.ModelName : string.Empty;
            var relatedRouteRules = routeRules
                .Where(x => string.Equals(x.ExternalModelName, modelName, StringComparison.Ordinal))
                .ToList();
            var modelLogs = allLogs
                .Where(l => string.Equals(l.RequestModel, modelName, StringComparison.Ordinal))
                .ToList();

            // Historical logs have no mapping ID. A name is evidence only if it identifies
            // exactly one mapping, including aliases that collide with remote names.
            var candidatesByAttempt = new Dictionary<(Guid SiteId, string Name), HashSet<Guid>>();
            void AddCandidate(Guid siteId, string name, Guid mappingId)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                var key = (siteId, name);
                if (!candidatesByAttempt.TryGetValue(key, out var candidates))
                    candidatesByAttempt[key] = candidates = [];
                candidates.Add(mappingId);
            }
            foreach (var mapping in modelMappings)
            {
                AddCandidate(mapping.SiteId, mapping.RemoteModelName, mapping.Id);
                foreach (var rule in relatedRouteRules.Where(r => r.SiteId == mapping.SiteId &&
                    string.Equals(r.SiteModelName, mapping.RemoteModelName, StringComparison.Ordinal)))
                    AddCandidate(mapping.SiteId, rule.UpstreamModelName, mapping.Id);
            }

            Guid? ResolveMapping(ProxyUsageLog log)
            {
                if (log.TargetSiteId != Guid.Empty)
                {
                    return candidatesByAttempt.TryGetValue((log.TargetSiteId, log.AttemptedModel ?? string.Empty), out var candidates) && candidates.Count == 1
                        ? candidates.Single() : null;
                }

                // Legacy logs without a site can match remote names or upstream aliases, but only
                // when it is unique across all sites for this model.
                var matches = candidatesByAttempt.Where(x => string.Equals(x.Key.Name, log.AttemptedModel, StringComparison.Ordinal)).SelectMany(x => x.Value).Distinct().Take(2).ToList();
                return matches.Count == 1 ? matches[0] : null;
            }

            var logsByMapping = new Dictionary<Guid, List<ProxyUsageLog>>();
            var unassignedLogs = new List<ProxyUsageLog>();
            foreach (var log in modelLogs)
            {
                var owner = ResolveMapping(log);
                if (owner is null)
                {
                    unassignedLogs.Add(log);
                    continue;
                }
                if (!logsByMapping.TryGetValue(owner.Value, out var logs))
                    logsByMapping[owner.Value] = logs = [];
                logs.Add(log);
            }

            var healthList = modelMappings.Select(map =>
            {
                sites.TryGetValue(map.SiteId, out var site);
                var siteLogs = logsByMapping.TryGetValue(map.Id, out var logs)
                    ? logs.OrderByDescending(l => l.RequestedAt).ToList()
                    : new List<ProxyUsageLog>();
                var latestLog = siteLogs.FirstOrDefault();
                var successCount = siteLogs.Count(l => l.Status == "success");
                var totalLogs = siteLogs.Count;

                return new HealthSiteItem
                {
                    SiteName = site?.Name ?? "(未知站点)",
                    RemoteModelName = map.RemoteModelName,
                    LastStatus = latestLog?.Status ?? map.LastStatus,
                    LastCheckedAt = latestLog?.RequestedAt,
                    LastDurationMs = latestLog?.TotalDurationMs,
                    TimelineSegments = BuildTimelineSegments(siteLogs),
                    SuccessRate = totalLogs > 0 ? (double)successCount / totalLogs : 0,
                    SuccessCount = successCount,
                    FailureCount = totalLogs - successCount
                };
            })
            .OrderBy(s => s.SiteName)
            .ToList();

            healthData[modelId] = healthList.Select(h => (object)new
            {
                siteName = h.SiteName,
                remoteModelName = h.RemoteModelName,
                lastStatus = h.LastStatus,
                lastCheckedAt = h.LastCheckedAt,
                lastDurationMs = h.LastDurationMs,
                successRate = h.SuccessRate,
                successCount = h.SuccessCount,
                failureCount = h.FailureCount,
                totalRequestCount = h.SuccessCount + h.FailureCount,
                timelineSegments = h.TimelineSegments
            }).ToList();

            var siteCount = healthList.Count;
            var healthySiteCount = healthList.Count(x => string.Equals(x.LastStatus, "success", StringComparison.OrdinalIgnoreCase));
            var unhealthySiteCount = healthList.Count(x => string.Equals(x.LastStatus, "fail", StringComparison.OrdinalIgnoreCase));
            var lastCheckedAt = healthList
                .Where(x => x.LastCheckedAt.HasValue)
                .Select(x => x.LastCheckedAt)
                .OrderByDescending(x => x)
                .FirstOrDefault();
            var durations = healthList.Where(x => x.LastDurationMs.HasValue).Select(x => x.LastDurationMs!.Value).ToList();
            var averageDurationMs = durations.Count > 0
                ? (int)Math.Round(durations.Average(), MidpointRounding.AwayFromZero)
                : (int?)null;
            var unassignedSuccessCount = unassignedLogs.Count(l => l.Status == "success");
            var successTotal = healthList.Sum(x => x.SuccessCount) + unassignedSuccessCount;
            var failureTotal = healthList.Sum(x => x.FailureCount) + unassignedLogs.Count - unassignedSuccessCount;
            var totalRequestCount = successTotal + failureTotal;

            modelSummaries.Add(new
            {
                modelLibraryItemId = modelId,
                displayName = models[modelId].ModelName,
                siteCount,
                healthySiteCount,
                unhealthySiteCount,
                lastCheckedAt,
                averageDurationMs,
                successCount = successTotal,
                failureCount = failureTotal,
                totalRequestCount,
                averageSuccessRate = totalRequestCount > 0 ? (double)successTotal / totalRequestCount : 0,
                unassigned = new
                {
                    successCount = unassignedSuccessCount,
                    failureCount = unassignedLogs.Count - unassignedSuccessCount,
                    totalRequestCount = unassignedLogs.Count,
                    timelineSegments = BuildTimelineSegments(unassignedLogs)
                },
                timelineSegments = BuildTimelineSegments(modelLogs)
            });
        }

        return new
        {
            monitoredModels = modelSummaries,
            availableModels,
            healthData,
            rangeOptions = new[] { new { value = "1d", label = "近 1 天" }, new { value = "7d", label = "近 7 天" }, new { value = "30d", label = "近 30 天" } }
        };
    }

    private static DateTimeOffset ResolveRecentCutoff(string? range)
    {
        return (range ?? "7d").Trim().ToLowerInvariant() switch
        {
            "1d" => DateTimeOffset.UtcNow.AddDays(-1),
            "30d" => DateTimeOffset.UtcNow.AddDays(-30),
            _ => DateTimeOffset.UtcNow.AddDays(-7)
        };
    }

    private static List<object> BuildTimelineSegments(List<ProxyUsageLog> siteLogs)
    {
        const int segmentCount = 48;
        if (siteLogs.Count == 0) return [];

        var orderedLogs = siteLogs.OrderBy(l => l.RequestedAt).ToList();
        var startAt = orderedLogs.First().RequestedAt;
        var endAt = orderedLogs.Last().RequestedAt;
        if (startAt >= endAt)
        {
            var singleSuccess = orderedLogs.Count(log => string.Equals(log.Status, "success", StringComparison.OrdinalIgnoreCase));
            return
            [
                (object)new
                {
                    status = singleSuccess == orderedLogs.Count ? "success" : "fail",
                    count = orderedLogs.Count,
                    successCount = singleSuccess,
                    failureCount = orderedLogs.Count - singleSuccess,
                    startAt,
                    endAt
                }
            ];
        }

        var totalTicks = endAt.UtcTicks - startAt.UtcTicks;
        var bucketSize = Math.Max(totalTicks / segmentCount, 1L);
        var buckets = new List<object>(segmentCount);
        for (var i = 0; i < segmentCount; i++)
        {
            var bucketStartTicks = startAt.UtcTicks + (bucketSize * i);
            var bucketEndTicks = i == segmentCount - 1
                ? endAt.UtcTicks
                : Math.Min(startAt.UtcTicks + (bucketSize * (i + 1)), endAt.UtcTicks);
            var bucketLogs = orderedLogs
                .Where(log =>
                {
                    var ticks = log.RequestedAt.UtcTicks;
                    return i == segmentCount - 1
                        ? ticks >= bucketStartTicks && ticks <= bucketEndTicks
                        : ticks >= bucketStartTicks && ticks < bucketEndTicks;
                })
                .ToList();
            if (bucketLogs.Count == 0) continue;
            var successCount = bucketLogs.Count(log => string.Equals(log.Status, "success", StringComparison.OrdinalIgnoreCase));
            buckets.Add(new
            {
                status = successCount == bucketLogs.Count ? "success" : "fail",
                count = bucketLogs.Count,
                successCount,
                failureCount = bucketLogs.Count - successCount,
                startAt = bucketLogs.First().RequestedAt,
                endAt = bucketLogs.Last().RequestedAt
            });
        }
        return buckets;
    }
}

internal sealed class HealthSiteItem
{
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public string LastStatus { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int? LastDurationMs { get; set; }
    public List<object> TimelineSegments { get; set; } = [];
    public double SuccessRate { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}
