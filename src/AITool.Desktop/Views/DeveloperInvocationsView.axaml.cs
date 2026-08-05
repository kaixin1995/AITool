using Avalonia.Controls;
using Avalonia.Interactivity;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

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

    private async void ConfirmResetCircuit(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeveloperInvocationsViewModel viewModel
            || sender is not Button button
            || button.DataContext is not CircuitBreakerRoute route)
        {
            return;
        }

        if (await ConfirmAsync("确认解除该路由的熔断/失败计数吗？", "解除熔断"))
        {
            await viewModel.ResetCircuitCommand.ExecuteAsync(route);
        }
    }

    private async void ConfirmResetAllCircuits(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeveloperInvocationsViewModel viewModel)
        {
            return;
        }

        if (await ConfirmAsync("确认解除所有路由的熔断和失败计数吗？", "解除全部熔断"))
        {
            await viewModel.ResetAllCircuitsCommand.ExecuteAsync(null);
        }
    }

    private async Task<bool> ConfirmAsync(string message, string confirmText)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return false;

        Window? dialog = null;
        var cancelButton = new Button
        {
            Content = "取消",
            Padding = new Avalonia.Thickness(14, 8)
        };
        var confirmButton = new Button
        {
            Content = confirmText,
            Padding = new Avalonia.Thickness(14, 8)
        };
        dialog = new Window
        {
            Title = "确认熔断操作",
            Width = 460,
            Height = 240,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 10,
                        Children = { cancelButton, confirmButton }
                    }
                }
            }
        };
        cancelButton.Click += (_, _) => dialog.Close(false);
        confirmButton.Click += (_, _) => dialog.Close(true);

        return await dialog.ShowDialog<bool>(owner);
    }
}
