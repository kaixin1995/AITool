using AITool.Domain.Models;
using AITool.Domain.Sites;

namespace AITool.Application.Detection;

// 模型探测结果，包含可用性状态与响应耗时
public sealed class ModelProbeResult
{
    // 探测是否成功
    public bool Success { get; init; }

    // 探测耗时（毫秒）
    public int DurationMs { get; init; }

    // 失败时的错误信息
    public string? ErrorMessage { get; init; }
}

// 模型可用性探测服务接口，用于检测站点模型是否正常响应
public interface IModelProbeService
{
    // 对指定站点的模型执行一次可用性探测
    Task<ModelProbeResult> ProbeAsync(Site site, ModelLibraryItem model, CancellationToken cancellationToken);
}
