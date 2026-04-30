namespace AITool.Domain.Proxy;

// 平台访问密钥，用于验证传入的代理请求
public sealed class ProxyAccessKey
{
    // 密钥主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 密钥名称，便于管理区分
    public string KeyName { get; set; } = string.Empty;

    // 明文密钥值
    public string PlainKey { get; set; } = string.Empty;

    // 密钥哈希值，兼容历史数据
    public string AccessKeyHash { get; set; } = string.Empty;

    // 掩码显示值，兼容历史数据
    public string MaskedValue { get; set; } = string.Empty;

    // 是否启用该密钥
    public bool IsEnabled { get; set; } = true;
}
