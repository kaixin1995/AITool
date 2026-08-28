using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

/// <summary>
/// 请求头模板方案（HeaderProfile）：内置客户端特征预设 + 用户自定义扩展。
/// </summary>
public sealed partial class HeaderProfileItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HeadersJson { get; set; }
    public bool IsBuiltIn { get; set; }
    public int SortOrder { get; set; }

    [ObservableProperty]
    private bool _isEnabled = true;

    public string TypeBadge => IsBuiltIn ? "系统内置" : "自定义";
    public IBrush BadgeBrush => new SolidColorBrush(IsBuiltIn ? Color.Parse("#2080F0") : Color.Parse("#18A058"));
    public string KeyText => $"Key: {Key}";
    public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? "" : Description!;
    public bool IsEditable => !IsBuiltIn;
    public bool IsDeletable => !IsBuiltIn;
    public string DeleteToolTip => IsBuiltIn ? "系统内置方案禁止删除，可禁用或克隆" : "删除该方案";

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    public string StatusText => IsEnabled ? "已启用" : "已停用";
}

/// <summary>
/// 网络出口代理方案（ProxyProfile）：供站点/映射快捷引用的代理池。
/// </summary>
public sealed partial class ProxyProfileItemUi : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProxyUrl { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }

    [ObservableProperty]
    private bool _isEnabled = true;

    public string KeyText => $"Key: {Key}";
    public string DescriptionText => string.IsNullOrWhiteSpace(Description) ? "" : Description!;

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StatusText));
    public string StatusText => IsEnabled ? "已启用" : "已停用";
}

public sealed class HeaderProfilePayload
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HeadersJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}

public sealed class ProxyProfilePayload
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProxyUrl { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}
