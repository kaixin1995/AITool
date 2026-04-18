using System.Collections.Concurrent;
using AITool.Application.Detection;
using AITool.Domain.Detection;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
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
    public List<ProbeResultItem> Results { get; set; } = [];
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
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var probeService = scope.ServiceProvider.GetRequiredService<IModelProbeService>();

        var mapping = await db.SiteModelMappings.FindAsync([mappingId], cancellationToken);
        if (mapping is null) return NotFound(new { message = "映射不存在" });

        var site = await db.Sites.FindAsync([mapping.SiteId], cancellationToken);
        var model = await db.ModelLibraryItems.FindAsync([mapping.ModelLibraryItemId], cancellationToken);
        if (site is null || model is null) return NotFound(new { message = "站点或模型不存在" });

        var result = await probeService.ProbeAsync(site, model, cancellationToken);

        var log = new DetectionLog
        {
            SiteId = mapping.SiteId,
            ModelLibraryItemId = mapping.ModelLibraryItemId,
            Status = result.Success ? "success" : "fail",
            DurationMs = result.DurationMs,
            ErrorMessage = result.ErrorMessage,
            CheckedAt = DateTimeOffset.UtcNow
        };
        db.DetectionLogs.Add(log);
        mapping.LastStatus = result.Success ? "success" : "fail";
        await db.SaveChangesAsync(cancellationToken);

        return Ok(new ProbeResultItem
        {
            MappingId = mapping.Id,
            SiteName = site.Name,
            RemoteModelName = mapping.RemoteModelName,
            Status = result.Success ? "success" : "fail",
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
            var workDb = workScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var workProbe = workScope.ServiceProvider.GetRequiredService<IModelProbeService>();

            foreach (var mapping in mappings)
            {
                try
                {
                    var site = await workDb.Sites.FindAsync([mapping.SiteId]);
                    var model = await workDb.ModelLibraryItems.FindAsync([mapping.ModelLibraryItemId]);
                    if (site is null || model is null) continue;

                    var result = await workProbe.ProbeAsync(site, model, default);

                    var log = new DetectionLog
                    {
                        SiteId = mapping.SiteId,
                        ModelLibraryItemId = mapping.ModelLibraryItemId,
                        Status = result.Success ? "success" : "fail",
                        DurationMs = result.DurationMs,
                        ErrorMessage = result.ErrorMessage,
                        CheckedAt = DateTimeOffset.UtcNow
                    };
                    workDb.DetectionLogs.Add(log);

                    var dbMapping = await workDb.SiteModelMappings.FindAsync([mapping.Id]);
                    if (dbMapping is not null) dbMapping.LastStatus = result.Success ? "success" : "fail";

                    if (_progressStore.TryGetValue(taskId, out var p))
                    {
                        p.Results.Add(new ProbeResultItem
                        {
                            MappingId = mapping.Id,
                            SiteName = site.Name,
                            RemoteModelName = mapping.RemoteModelName,
                            Status = result.Success ? "success" : "fail",
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
                        p.Results.Add(new ProbeResultItem
                        {
                            MappingId = mapping.Id,
                            Status = "fail",
                            Error = ex.Message
                        });
                        p.Completed++;
                    }
                }
            }

            try { await workDb.SaveChangesAsync(); } catch { }

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
            var workDb = workScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var workProbe = workScope.ServiceProvider.GetRequiredService<IModelProbeService>();

            foreach (var mapping in mappings)
            {
                try
                {
                    var site = await workDb.Sites.FindAsync([mapping.SiteId]);
                    var model = await workDb.ModelLibraryItems.FindAsync([mapping.ModelLibraryItemId]);
                    if (site is null || model is null) continue;

                    var result = await workProbe.ProbeAsync(site, model, default);

                    var log = new DetectionLog
                    {
                        SiteId = mapping.SiteId,
                        ModelLibraryItemId = mapping.ModelLibraryItemId,
                        Status = result.Success ? "success" : "fail",
                        DurationMs = result.DurationMs,
                        ErrorMessage = result.ErrorMessage,
                        CheckedAt = DateTimeOffset.UtcNow
                    };
                    workDb.DetectionLogs.Add(log);

                    var dbMapping = await workDb.SiteModelMappings.FindAsync([mapping.Id]);
                    if (dbMapping is not null) dbMapping.LastStatus = result.Success ? "success" : "fail";

                    if (_progressStore.TryGetValue(taskId, out var p))
                    {
                        p.Results.Add(new ProbeResultItem
                        {
                            MappingId = mapping.Id,
                            SiteName = site.Name,
                            RemoteModelName = mapping.RemoteModelName,
                            Status = result.Success ? "success" : "fail",
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
                        p.Results.Add(new ProbeResultItem
                        {
                            MappingId = mapping.Id,
                            Status = "fail",
                            Error = ex.Message
                        });
                        p.Completed++;
                    }
                }
            }

            try { await workDb.SaveChangesAsync(); } catch { }

            if (_progressStore.TryGetValue(taskId, out var prog))
            {
                prog.IsCompleted = true;
            }
        });

        return Ok(new { taskId });
    }

    // 查询检测进度
    [HttpGet("progress/{taskId}")]
    public IActionResult GetProgress(string taskId)
    {
        if (!_progressStore.TryGetValue(taskId, out var progress))
        {
            return NotFound(new { message = "任务不存在" });
        }
        return Ok(progress);
    }
}
