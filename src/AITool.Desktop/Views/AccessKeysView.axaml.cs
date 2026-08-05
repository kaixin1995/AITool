using Avalonia.Controls;
using Avalonia.Interactivity;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class AccessKeysView : UserControl
{
    public AccessKeysView()
    {
        InitializeComponent();
    }

    private async void ConfirmDeleteAccessKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccessKeysViewModel viewModel ||
            sender is not Button button ||
            button.DataContext is not AccessKeyItem item)
        {
            return;
        }

        var message = $"确认删除访问密钥“{item.KeyName}”吗？删除后不可恢复，使用该密钥的客户端将无法继续访问。";
        if (await ConfirmAsync(message))
        {
            await viewModel.DeleteCommand.ExecuteAsync(item);
        }
    }

    private async void CopyCreatedPlainKey(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccessKeysViewModel viewModel ||
            string.IsNullOrWhiteSpace(viewModel.CreatedPlainKey))
        {
            return;
        }

        // 仅将新建时临时返回的明文密钥写入系统剪贴板，不输出到日志或界面消息。
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(viewModel.CreatedPlainKey);
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
        var cancelButton = new Button
        {
            Content = "取消",
            Padding = new Avalonia.Thickness(14, 8)
        };
        var confirmButton = new Button
        {
            Content = "确认删除",
            Padding = new Avalonia.Thickness(14, 8)
        };
        dialog = new Window
        {
            Title = "确认删除访问密钥",
            Width = 480,
            Height = 250,
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
