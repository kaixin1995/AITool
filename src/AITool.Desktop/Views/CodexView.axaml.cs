using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class CodexView : UserControl
{
    public CodexView() => InitializeComponent();

    private async void ImportCredentialFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CodexViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Codex 凭证 JSON 文件",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 文件")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        if (files.Count == 0) return;

        var contents = new List<(string FileName, string JsonText)>();
        foreach (var file in files)
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                using var reader = new StreamReader(stream);
                contents.Add((file.Name, await reader.ReadToEndAsync()));
            }
            catch
            {
                // 让服务端统一返回该文件的解析失败结果，界面不会显示文件内容。
                contents.Add((file.Name, string.Empty));
            }
        }

        await viewModel.ImportCredentialFilesAsync(contents);
    }

    private async void ConfirmResetQuota(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CodexViewModel viewModel
            || sender is not Button button
            || button.DataContext is not CodexAccount account)
        {
            return;
        }

        if (await ConfirmAsync("重置额度会清除冷却状态并刷新凭证，确认继续吗？", "重置额度"))
        {
            await viewModel.ResetQuotaCommand.ExecuteAsync(account);
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
            Title = "确认重置额度",
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
