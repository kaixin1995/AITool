namespace AITool.Application.CoreRuntime;

/// <summary>
/// Core 运行时配置来源接口，用于让核心服务从内存快照而不是数据库读取配置。
/// </summary>
public interface ICoreRuntimeConfigProvider
{
    /// <summary>
    /// 读取当前已生效的配置快照；未就绪时返回 null。
    /// </summary>
    CoreRuntimeConfigSnapshot? GetCurrent();

    /// <summary>
    /// 更新当前生效配置快照。
    /// </summary>
    void SetCurrent(CoreRuntimeConfigSnapshot snapshot);

    /// <summary>
    /// 尝试从本地文件恢复最后一次成功配置。
    /// </summary>
    Task<bool> TryLoadFromFileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断当前是否已有可用配置。
    /// </summary>
    bool IsReady { get; }
}
