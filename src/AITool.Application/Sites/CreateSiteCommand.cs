namespace AITool.Application.Sites;

// 站点创建命令，对应后台表单提交
public sealed class CreateSiteCommand
{
    // 站点名称
    public string Name { get; set; } = string.Empty;

    // 站点根地址
    public string BaseUrl { get; set; } = string.Empty;

    // 站点访问密钥
    public string ApiKey { get; set; } = string.Empty;

    // 是否支持 OpenAI 原协议
    public bool SupportsOpenAi { get; set; } = true;

    // 是否支持 Anthropic 原协议
    public bool SupportsAnthropic { get; set; }

    // 是否启用
    public bool IsEnabled { get; set; } = true;
}
