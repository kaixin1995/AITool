namespace AITool.Domain.Proxy;

// 主入口实体，记录可独立存在的本地逻辑入口
public sealed class ProxyRouteEntry
{
    // 主入口主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 对外暴露的主入口名称
    public string EntryName { get; set; } = string.Empty;

    // 创建时间
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
