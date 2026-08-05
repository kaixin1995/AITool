using System.Text.Json;
using System.Text.Json.Serialization;

namespace AITool.Desktop.Models;

public partial class AccessKeyItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string KeyName { get; set; } = string.Empty;
    public string MaskedValue { get; set; } = string.Empty;

    // 兼容后端新旧数据：允许路由可能是数组，也可能是 JSON 字符串。
    [JsonConverter(typeof(StringListOrJsonConverter))]
    public List<string> AllowedRouteNames { get; set; } = new();

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isEnabled;

    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";
    public string AllowedRoutesText => AllowedRouteNames.Count == 0 ? "全部路由" : string.Join("、", AllowedRouteNames);

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ToggleActionText));
    }
}

public sealed class CreateAccessKeyResult
{
    public string PlainKey { get; set; } = string.Empty;
}

/// <summary>
/// 将数组或 JSON 字符串统一转换为路由名称列表。
/// </summary>
public sealed class StringListOrJsonConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            return JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? new List<string>();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(value, options) ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return new List<string>();
        }

        throw new JsonException("允许路由字段格式无效");
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, options);
    }
}
