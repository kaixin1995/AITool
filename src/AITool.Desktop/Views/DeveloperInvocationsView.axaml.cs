using Avalonia.Controls;
using Avalonia.Interactivity;
using AITool.Desktop.Models;

namespace AITool.Desktop.Views;

public partial class DeveloperInvocationsView : UserControl
{
    public DeveloperInvocationsView() => InitializeComponent();

    private async void CopyEndpointUrl(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not DeveloperSimulatorTab tab)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && !string.IsNullOrWhiteSpace(tab.EndpointUrl))
        {
            // 通过桌面窗口剪贴板复制当前模拟端点，避免把 URL 复制逻辑放入 ViewModel。
            await clipboard.SetTextAsync(tab.EndpointUrl);
        }
    }
}
