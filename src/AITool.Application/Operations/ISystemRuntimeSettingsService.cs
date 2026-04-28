using AITool.Domain.Operations;

namespace AITool.Application.Operations;

// 系统运行时设置服务接口，统一读取和更新持久化配置
public interface ISystemRuntimeSettingsService
{
    // 获取当前系统设置，不存在时按默认值创建
    Task<SystemRuntimeSettings> GetOrCreateAsync(CancellationToken cancellationToken = default);

    // 更新系统运行时设置并持久化到数据库
    Task<SystemRuntimeSettings> UpdateAsync(UpdateSystemRuntimeSettingsRequest request, CancellationToken cancellationToken = default);
}

// 系统运行时设置更新请求
public sealed class UpdateSystemRuntimeSettingsRequest
{
    // 代理请求超时时间（秒）
    public int ProxyRequestTimeoutSeconds { get; set; }

    // 代理失败重试次数
    public int ProxyRetryCount { get; set; }

    // 使用日志保留天数
    public int UsageLogRetentionDays { get; set; }

    // 检测日志保留天数
    public int DetectionLogRetentionDays { get; set; }
}
