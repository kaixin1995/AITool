namespace AITool.Domain.Proxy;

// 平台访问密钥，用于验证传入的代理请求
public sealed class ProxyAccessKey
{
    // 密钥主键
    public Guid Id { get; set; } = Guid.NewGuid();

    // 密钥名称，便于管理区分
    public string KeyName { get; set; } = string.Empty;

    // 密钥哈希值，存储时只保存哈希
    public string AccessKeyHash { get; set; } = string.Empty;

    // 掩码显示值，用于界面展示部分内容
    public string MaskedValue { get; set; } = string.Empty;

    // 是否启用该密钥
    public bool IsEnabled { get; set; } = true;
}
