namespace AITool.Web.Services;

/// <summary>
/// 保存当前 Web 应用版本号和编译时间，便于在页面或接口中统一输出。
/// </summary>
/// <param name="Value">当前运行版本号（对应 Program.cs 中的 applicationVersion）。</param>
/// <param name="BuildTime">程序集编译时间（UTC），用于确认运行的是否是最新版本。</param>
public sealed record AppVersionInfo(string Value, DateTimeOffset BuildTime);
