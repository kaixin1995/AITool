using System.Collections.Concurrent;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 单条检测结果
public sealed class ProbeResultItem
{
    // 映射ID
    public Guid MappingId { get; set; }
    // 站点名称
    public string SiteName { get; set; } = string.Empty;
    // 远程模型名
    public string RemoteModelName { get; set; } = string.Empty;
    // 检测状态
    public string Status { get; set; } = string.Empty;
    // 耗时毫秒
    public int? DurationMs { get; set; }
    // 错误信息
    public string? Error { get; set; }
}

// 模型检测进度
public sealed class ProbeProgress
{
    public string TaskId { get; set; } = string.Empty;
    public int Total { get; set; }
    public int Completed { get; set; }
    public bool IsCompleted { get; set; }
    // 所有已完成结果（完整列表）
    public List<ProbeResultItem> AllResults { get; set; } = [];
    // 上次被查询时已完成的数量
    public int LastReportedCount { get; set; }
}

// 模型检测 API，提供 AJAX 检测与进度查询
[ApiController]
[Route("api/admin/detection")]
public sealed class DetectionApiController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private static readonly ConcurrentDictionary<string, ProbeProgress> _progressStore = new();

    public DetectionApiController(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    // 检测单个映射
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

    // 检测指定模型的所有映射
    [HttpPost("probe-model/{modelId}")]
    public IActionResult ProbeModel(Guid modelId)
    {
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

    // 检测全部映射
    [HttpPost("probe-all")]
    public IActionResult ProbeAll()
    {
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

    // 查询检测进度，返回自上次查询以来的新增结果
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
}
