namespace AITool.Domain.Sites;

/// <summary>
/// WorkBuddy（腾讯 CodeBuddy）官方客户端特征常量。
/// 版本号对齐真实客户端抓包（2026-11-03，渠道校验通过形态）；
/// 请求头模拟预设与设备 OAuth 托管站点共用，升级时只改这里。
/// </summary>
public static class WorkBuddyClientConstants
{
    public const string ClientVersion = "5.9.3";
    public const string CliVersion = "2.165.0";

    /// <summary>官方客户端 OpenAI JS SDK 包版本（x-stainless-package-version）。</summary>
    public const string StainlessPackageVersion = "6.25.0";

    /// <summary>官方客户端内置 Node 运行时版本（x-stainless-runtime-version）。</summary>
    public const string StainlessRuntimeVersion = "v22.21.1";

    /// <summary>聊天 UA 三段式（WorkBuddy/&lt;ver&gt; &lt;平台&gt;/&lt;ver&gt; CLI/&lt;cli&gt;；平台段中国站 WorkBuddy、国际站 WorkBuddy AI）。</summary>
    public static string ChatUserAgent(string platformSegment) =>
        $"WorkBuddy/{ClientVersion} {platformSegment}/{ClientVersion} CLI/{CliVersion}";
}
