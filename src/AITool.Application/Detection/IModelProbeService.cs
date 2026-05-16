using AITool.Domain.Models;
using AITool.Domain.Sites;

namespace AITool.Application.Detection;

/// <summary>
/// 模型探测结果，用于返回一次检测的状态、耗时和失败原因。
/// </summary>
public sealed class ModelProbeResult
{
    /// <summary>
    /// 标记本次探测是否得到成功响应。
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 记录本次探测从发起到结束的总耗时，单位为毫秒。
    /// </summary>
    public int DurationMs { get; init; }

    /// <summary>
    /// 当探测失败时，保存上层可直接展示或记录的错误信息。
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 模型可用性探测服务接口，用于检查指定站点上的模型是否可以正常调用。
/// </summary>
public interface IModelProbeService
{
    /// <summary>
    /// 对指定站点和模型执行一次探测，并返回统一的检测结果。
    /// </summary>
    Task<ModelProbeResult> ProbeAsync(Site site, ModelLibraryItem model, CancellationToken cancellationToken);
}
