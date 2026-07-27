using System.Collections.Concurrent;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 单个站点映射的探测结果，记录探测状态、耗时和可能的错误信息。
/// </summary>
public sealed class ProbeResultItem
{
    /// <summary>
    /// 映射标识。
    /// </summary>
    public Guid MappingId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 远端模型名称。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 探测状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 耗时（毫秒）。
    /// </summary>
    public int? DurationMs { get; set; }
    /// <summary>
    /// 错误信息。
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// 批量探测任务的进度信息，用于前端轮询展示任务完成状态和各映射的探测结果。
/// </summary>
public sealed class ProbeProgress
{
    /// <summary>
    /// 任务标识。
    /// </summary>
    public string TaskId { get; set; } = string.Empty;
    /// <summary>
    /// 总任务数。
    /// </summary>
    public int Total { get; set; }
    /// <summary>
    /// 已完成任务数。
    /// </summary>
    public int Completed { get; set; }
    /// <summary>
    /// 是否已完成。
    /// </summary>
    public bool IsCompleted { get; set; }
    /// <summary>
    /// 全部探测结果。
    /// </summary>
    public List<ProbeResultItem> AllResults { get; set; } = [];
    /// <summary>
    /// 上次已返回的结果数。
    /// </summary>
    public int LastReportedCount { get; set; }
}

/// <summary>
/// 模型探测控制器，提供单个映射探测、按模型批量探测和全量探测功能。
/// </summary>
[ApiController]
[Route("api/admin/detection")]
public sealed class DetectionApiController : ControllerBase
{
    /// <summary>
    /// 服务作用域工厂。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>
    /// 探测进度缓存。
    /// </summary>
    private static readonly ConcurrentDictionary<string, ProbeProgress> _progressStore = new();

    /// <summary>
    /// 创建模型探测控制器。
    /// </summary>
    public DetectionApiController(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// 获取模型检测矩阵（按模型分组，展示每个站点映射的最新检测状态）。
    /// 迁移自 Detection/Index.cshtml.cs 的 OnGetAsync。
    /// 数据源：ProxyUsageLogs 中 IsFinalResult=true 的记录，按 (TargetSiteId, RequestModel) 取最新。
    /// </summary>
    [HttpGet("matrix")]
    public async Task<IActionResult> GetMatrix(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 只取最终结果日志，按 (站点, 请求模型) 取最新一条。
        var allLogs = await dbContext.ProxyUsageLogs
            .Where(x => x.IsFinalResult)
            .ToListAsync(cancellationToken);
        var latestLogs = allLogs
            .GroupBy(d => (d.TargetSiteId, d.RequestModel))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.RequestedAt).First());

        // SqlSugar 的 ToDictionaryAsync 需要 key+value 两个 selector，这里用 ToListAsync + 内存 ToDictionary。
        var models = (await dbContext.ModelLibraryItems.ToListAsync(cancellationToken))
            .ToDictionary(m => m.Id);
        var sites = (await dbContext.Sites
            .Where(s => s.IsEnabled)
            .ToListAsync(cancellationToken))
            .ToDictionary(s => s.Id);
        var mappings = await dbContext.SiteModelMappings
            .Where(m => m.IsEnabled)
            .ToListAsync(cancellationToken);

        // 静默丢弃孤儿映射（指向已删除模型或站点），不解绑不删除。
        var modelGroups = mappings
            .Where(m => models.ContainsKey(m.ModelLibraryItemId) && sites.ContainsKey(m.SiteId))
            .GroupBy(m => m.ModelLibraryItemId)
            .Select(g =>
            {
                var model = models[g.Key];
                return new
                {
                    modelLibraryItemId = g.Key,
                    modelName = model.ModelName,
                    displayName = model.DisplayName,
                    sites = g.Select(m =>
                    {
                        latestLogs.TryGetValue((m.SiteId, model.ModelName), out var log);
                        var site = sites[m.SiteId];
                        return new
                        {
                            mappingId = m.Id,
                            siteName = site.Name,
                            remoteModelName = m.RemoteModelName,
                            lastStatus = log?.Status ?? m.LastStatus,
                            lastCheckedAt = log?.RequestedAt,
                            lastDurationMs = log?.TotalDurationMs
                        };
                    }).OrderBy(s => s.siteName).ToList()
                };
            })
            .OrderBy(g => g.displayName)
            .ToList();

        var filterModels = modelGroups
            .Select(g => new { id = g.modelLibraryItemId, displayName = g.displayName })
            .OrderBy(m => m.displayName)
            .ToList();

        return Ok(new { modelGroups, filterModels });
    }

    /// <summary>
    /// 探测单个映射。
    /// </summary>
    [HttpPost("probe/{mappingId}")]
    public async Task<IActionResult> ProbeSingle(Guid mappingId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var requestService = scope.ServiceProvider.GetRequiredService<ModelHealthRequestService>();

        var result = await requestService.ProbeMappingAsync(mappingId, "detection-manual", cancellationToken);
        if (result.MappingId == Guid.Empty || (result.Status == "fail" && string.Equals(result.ErrorMessage, "映射不存在", StringComparison.Ordinal)))
        {
            return NotFound(new { message = "映射不存在" });
        }

        return Ok(new ProbeResultItem
        {
            MappingId = result.MappingId,
            SiteName = result.SiteName,
            RemoteModelName = result.RemoteModelName,
            Status = result.Status,
            DurationMs = result.DurationMs,
            Error = result.ErrorMessage
        });
    }

    /// <summary>
    /// 探测指定模型的全部映射。
    /// </summary>
    [HttpPost("probe-model/{modelId}")]
    public IActionResult ProbeModel(Guid modelId)
    {
        PurgeCompletedProgress();
        var taskId = Guid.NewGuid().ToString("N")[..8];

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mappings = db.SiteModelMappings
            .Where(m => m.ModelLibraryItemId == modelId)
            .ToList();

        var progress = new ProbeProgress
        {
            TaskId = taskId,
            Total = mappings.Count
        };
        _progressStore[taskId] = progress;

        _ = Task.Run(async () =>
        {
            using var workScope = _scopeFactory.CreateScope();
            var requestService = workScope.ServiceProvider.GetRequiredService<ModelHealthRequestService>();

            foreach (var mapping in mappings)
            {
                try
                {
                    var result = await requestService.ProbeMappingAsync(mapping.Id, "detection-manual", default);

                    if (_progressStore.TryGetValue(taskId, out var p))
                    {
                        p.AllResults.Add(new ProbeResultItem
                        {
                            MappingId = result.MappingId,
                            SiteName = result.SiteName,
                            RemoteModelName = result.RemoteModelName,
                            Status = result.Status,
                            DurationMs = result.DurationMs,
                            Error = result.ErrorMessage
                        });
                        p.Completed++;
                    }
                }
                catch (Exception ex)
                {
                    if (_progressStore.TryGetValue(taskId, out var p))
                    {
                        p.AllResults.Add(new ProbeResultItem
                        {
                            MappingId = mapping.Id,
                            RemoteModelName = mapping.RemoteModelName,
                            Status = "fail",
                            Error = ex.Message
                        });
                        p.Completed++;
                    }
                }
            }

            if (_progressStore.TryGetValue(taskId, out var prog))
            {
                prog.IsCompleted = true;
            }
        });

        return Ok(new { taskId });
    }

    /// <summary>
    /// 探测全部映射。
    /// </summary>
    [HttpPost("probe-all")]
    public IActionResult ProbeAll()
    {
        PurgeCompletedProgress();
        var taskId = Guid.NewGuid().ToString("N")[..8];

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mappings = db.SiteModelMappings.ToList();

        var progress = new ProbeProgress
        {
            TaskId = taskId,
            Total = mappings.Count
        };
        _progressStore[taskId] = progress;

        _ = Task.Run(async () =>
        {
            using var workScope = _scopeFactory.CreateScope();
            var requestService = workScope.ServiceProvider.GetRequiredService<ModelHealthRequestService>();

            foreach (var mapping in mappings)
            {
                try
                {
                    var result = await requestService.ProbeMappingAsync(mapping.Id, "detection-manual", default);

                    if (_progressStore.TryGetValue(taskId, out var p))
                    {
                        p.AllResults.Add(new ProbeResultItem
                        {
                            MappingId = result.MappingId,
                            SiteName = result.SiteName,
                            RemoteModelName = result.RemoteModelName,
                            Status = result.Status,
                            DurationMs = result.DurationMs,
                            Error = result.ErrorMessage
                        });
                        p.Completed++;
                    }
                }
                catch (Exception ex)
                {
                    if (_progressStore.TryGetValue(taskId, out var p))
                    {
                        p.AllResults.Add(new ProbeResultItem
                        {
                            MappingId = mapping.Id,
                            RemoteModelName = mapping.RemoteModelName,
                            Status = "fail",
                            Error = ex.Message
                        });
                        p.Completed++;
                    }
                }
            }

            if (_progressStore.TryGetValue(taskId, out var prog))
            {
                prog.IsCompleted = true;
            }
        });

        return Ok(new { taskId });
    }

    /// <summary>
    /// 获取探测进度。
    /// </summary>
    [HttpGet("progress/{taskId}")]
    public IActionResult GetProgress(string taskId)
    {
        if (!_progressStore.TryGetValue(taskId, out var progress))
        {
            return NotFound(new { message = "任务不存在" });
        }

        // 取出上次报告后新增的结果
        var newResults = progress.AllResults.Skip(progress.LastReportedCount).ToList();
        progress.LastReportedCount = progress.AllResults.Count;

        return Ok(new
        {
            progress.TaskId,
            progress.Total,
            progress.Completed,
            progress.IsCompleted,
            NewResults = newResults
        });
    }

    /// <summary>
    /// 懒清理已完成的探测任务，避免 _progressStore 静态字典无限增长导致内存泄漏。
    /// </summary>
    private static void PurgeCompletedProgress()
    {
        foreach (var pair in _progressStore)
        {
            if (pair.Value.IsCompleted)
            {
                _progressStore.TryRemove(pair.Key, out _);
            }
        }
    }
}
