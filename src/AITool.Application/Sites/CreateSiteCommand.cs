namespace AITool.Application.Sites;

/// <summary>
/// 站点创建命令，用于承载新增站点时提交的基础配置。
/// </summary>
public sealed class CreateSiteCommand
{
    /// <summary>
    /// 站点名称，通常用于后台列表展示和人工识别。
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 站点根地址，后续转发请求时会基于该地址拼接具体接口路径。
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// 接口路径模式，用于区分根地址是否已经包含接口版本路径。
    /// </summary>
    public string EndpointPathMode { get; set; } = SiteEndpointPathResolver.StandardRoot;

    /// <summary>
    /// 站点访问密钥，用于调用上游站点接口。
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 标记该站点是否支持 OpenAI 原生协议。
    /// </summary>
    public bool SupportsOpenAi { get; set; } = true;

    /// <summary>
    /// 标记该站点是否支持 Anthropic 原生协议。
    /// </summary>
    public bool SupportsAnthropic { get; set; }

    /// <summary>
    /// 控制站点创建后是否立即参与后续路由与调用。
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}
