namespace AITool.Web.Services;

/// <summary>
/// 保存当前 Web 应用版本号，便于在页面或接口中统一输出。
/// </summary>
/// <param name="Value">当前运行版本号。</param>
public sealed record AppVersionInfo(string Value);
