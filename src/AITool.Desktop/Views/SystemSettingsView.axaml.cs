using Avalonia.Controls;
using Avalonia.Interactivity;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class SystemSettingsView : UserControl
{
    public SystemSettingsView() => InitializeComponent();

    private async void ConfirmClearFilteredLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SystemSettingsViewModel viewModel && await ConfirmAsync("确定清空当前筛选条件匹配的调用日志吗？此操作不可恢复。"))
        {
            await viewModel.ClearFilteredLogsCommand.ExecuteAsync(null);
        }
    }

    private async void ConfirmClearAllLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SystemSettingsViewModel viewModel && await ConfirmAsync("确定清空全部调用日志吗？此操作不可恢复。"))
        {
            await viewModel.ClearAllLogsCommand.ExecuteAsync(null);
        }
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return false;
        }

        Window? dialog = null;
        var cancelButton = new Button { Content = "取消", Padding = new Avalonia.Thickness(14, 8) };
        var confirmButton = new Button { Content = "确认清空", Padding = new Avalonia.Thickness(14, 8) };
        dialog = new Window
        {
            Title = "确认危险操作",
            Width = 420,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(24),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
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
