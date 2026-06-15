using System.Text.Json;

namespace AITool.Application.Common;

/// <summary>
/// 共享的 <see cref="JsonSerializerOptions"/> 预设单例。
/// <para>
/// <see cref="JsonSerializerOptions"/> 实例会缓存反射元数据（JsonTypeInfo），
/// 每次新建会重复构建这些元数据，是 System.Text.Json 的经典性能反模式。
/// 本类提供项目内复用的单例预设，所有 JSON 序列化/反序列化调用都应使用这里的静态字段，
/// 避免 <c>new JsonSerializerOptions</c>。
/// </para>
/// </summary>
public static class JsonSerializerPresets
{
    /// <summary>
    /// 属性名大小写不敏感的反序列化预设。
    /// 适用于从外部 JSON（配置快照、路由时间范围等）反序列化到 POCO 的场景。
    /// </summary>
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 驼峰命名序列化预设。
    /// 适用于将 POCO 序列化为 JSON 输出（路由时间范围规范化等要求驼峰键的场景）。
    /// </summary>
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 缩进格式化序列化预设。
    /// 适用于对 JSON 做美化输出（开发者追踪正文、用户可见的导出文件等）。
    /// </summary>
    public static readonly JsonSerializerOptions WriteIndented = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// 紧凑（单行）序列化预设。
    /// 适用于对体积敏感的导出/存储场景（站点导出 JSON 等）。
    /// </summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false
    };
}
