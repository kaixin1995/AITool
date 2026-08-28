using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

public sealed class CompatibilityProfileItem : ObservableObject
{
    private bool _isEnabled;

    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ToggleActionText));
        }
    }

    public int RuleCount { get; set; }
    public string StatusText => IsEnabled ? "已启用" : "已停用";
    public string ToggleActionText => IsEnabled ? "停用" : "启用";
}

public sealed class CompatibilityProfileDetail
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RulesJson { get; set; } = "[]";
    public bool IsEnabled { get; set; }
}

public sealed class CompatibilityRuleForm : ObservableObject
{
    private string _operation = "strip";
    private string _target = string.Empty;
    private string _from = string.Empty;
    private string _to = string.Empty;
    private string _key = string.Empty;
    private string _value = string.Empty;
    private string _scope = "all";

    [JsonPropertyName("op")]
    public string Operation
    {
        get => _operation;
        set
        {
            if (!SetProperty(ref _operation, value)) return;
            OnPropertyChanged(nameof(IsTargetVisible));
            OnPropertyChanged(nameof(IsRenameVisible));
            OnPropertyChanged(nameof(IsDefaultVisible));
        }
    }
    [JsonPropertyName("target")]
    public string Target { get => _target; set => SetProperty(ref _target, value); }
    [JsonPropertyName("from")]
    public string From { get => _from; set => SetProperty(ref _from, value); }
    [JsonPropertyName("to")]
    public string To { get => _to; set => SetProperty(ref _to, value); }
    [JsonPropertyName("key")]
    public string Key { get => _key; set => SetProperty(ref _key, value); }
    [JsonPropertyName("value")]
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    [JsonPropertyName("scope")]
    public string Scope { get => _scope; set => SetProperty(ref _scope, value); }

    [JsonIgnore]
    public bool IsTargetVisible => string.Equals(Operation, "strip", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsRenameVisible => string.Equals(Operation, "rename", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsDefaultVisible => string.Equals(Operation, "default", StringComparison.OrdinalIgnoreCase);
}

public sealed class CompatibilityProfileEditForm : ObservableObject
{
    private string _name = string.Empty;
    private string _description = string.Empty;
    private bool _isEnabled = true;

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public ObservableCollection<CompatibilityRuleForm> Rules { get; } = new();

    public void Reset()
    {
        Name = string.Empty;
        Description = string.Empty;
        IsEnabled = true;
        Rules.Clear();
    }
}

public sealed class CompatibilityProfilePayload
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string RulesJson { get; set; } = "[]";
    public bool IsEnabled { get; set; } = true;
}
