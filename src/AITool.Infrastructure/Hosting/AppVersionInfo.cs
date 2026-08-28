namespace AITool.Infrastructure.Hosting;

/// <summary>
/// 当前宿主应用版本信息。
/// </summary>
public sealed record AppVersionInfo(string Value, DateTimeOffset BuildTime);
