using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITool.Desktop.Models;

// Numeric wire values are intentional: Legacy is not an alias for explicit Direct.
public sealed record AccountNetworkSelection(int Mode, Guid? ProxyProfileId = null);

public sealed record AccountNetworkPolicy(
    string ProviderKey,
    Guid AccountId,
    AccountNetworkSelection NetworkSelection,
    string? Revision = null,
    string? BlockReason = null,
    string? ProxyProfileName = null);

public sealed record AccountNetworkCapabilities(bool Ready = false);

// This discovery DTO must never acquire a proxy URL, username or password field.
public sealed record AccountNetworkProxyOption(
    Guid Id,
    string Name,
    bool IsEnabled,
    long? Revision = null,
    bool HasCredentials = false)
{
    public string DisplayLabel => $"{Name} · {Id:D}{(IsEnabled ? "" : "（不可用）")}";
}

public sealed record AccountNetworkModeOption(int? Mode, string Label);

/// <summary>A draft selection only; no credentials or policy are persisted locally.</summary>
public sealed class AccountNetworkEditor : ObservableObject
{
    public IReadOnlyList<AccountNetworkModeOption> ModeOptions { get; } =
    [
        new(null, "未指定（保留服务端策略）"),
        new(0, "默认（跟随站点 / 系统代理）"),
        new(1, "直连"),
        new(2, "代理方案")
    ];

    public AccountNetworkEditor() => _selectedMode = ModeOptions[0];

    private AccountNetworkModeOption? _selectedMode;
    private AccountNetworkProxyOption? _selectedProfile;
    public ObservableCollection<AccountNetworkProxyOption> ProxyOptions { get; } = new();

    public AccountNetworkModeOption? SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (SetProperty(ref _selectedMode, value)) OnPropertyChanged(nameof(IsProfile));
        }
    }

    public AccountNetworkProxyOption? SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    public bool IsProfile => SelectedMode?.Mode == 2;

    public void SetOptions(IEnumerable<AccountNetworkProxyOption> options)
    {
        var selected = SelectedProfile;
        ProxyOptions.Clear();
        foreach (var option in options) ProxyOptions.Add(option);
        if (selected is not null)
        {
            var current = ProxyOptions.FirstOrDefault(option => option.Id == selected.Id);
            if (current is null)
            {
                current = selected with { IsEnabled = false };
                ProxyOptions.Add(current);
            }
            SelectedProfile = current;
        }
    }

    public void Load(AccountNetworkSelection? selection, string? profileName = null)
    {
        SelectedMode = ModeOptions.FirstOrDefault(option => option.Mode == selection?.Mode);
        SelectedProfile = null;
        if (selection?.ProxyProfileId is not { } id) return;
        SelectedProfile = ProxyOptions.FirstOrDefault(option => option.Id == id);
        if (SelectedProfile is null)
        {
            var unavailable = new AccountNetworkProxyOption(id, profileName ?? "当前配置", false);
            ProxyOptions.Add(unavailable);
            SelectedProfile = unavailable;
        }
    }

    // Comparing a draft must also work for an existing disabled/missing profile, so validation
    // happens only when submitting an actual change, not when editing unrelated credentials.
    public AccountNetworkSelection? GetSelection(bool validate = true)
    {
        if (SelectedMode?.Mode is not { } mode) return null;
        if (mode == 2 && validate && (SelectedProfile is null || !SelectedProfile.IsEnabled || SelectedProfile.Id == Guid.Empty))
            throw new InvalidOperationException("请选择一个已启用的代理方案；不会自动回退为直连。");
        return new(mode, mode == 2 ? SelectedProfile?.Id : null);
    }
}
