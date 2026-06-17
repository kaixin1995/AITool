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
    /// </summary>
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 驼峰命名序列化预设。
    /// </summary>
    public static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 缩进格式化序列化预设。
    /// </summary>
    public static readonly JsonSerializerOptions WriteIndented = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// 紧凑（单行）序列化预设。
    /// </summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false
    };
}
