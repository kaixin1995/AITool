using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class SitesView : UserControl
{
    private static readonly IReadOnlyList<FilePickerFileType> JsonFileTypes =
    [
        new FilePickerFileType("JSON 文件")
        {
            Patterns = ["*.json"],
            MimeTypes = ["application/json"]
        }
    ];

    public SitesView()
    {
        InitializeComponent();
    }

    private async void ConfirmDeleteSite(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SitesViewModel viewModel ||
            sender is not Button button ||
            button.DataContext is not SiteListItem site)
        {
            return;
        }

        var message = $"确认删除站点“{site.Name}”吗？关联的映射和路由规则会一并级联清理。";
        if (await ConfirmAsync("确认删除站点", message, "确认删除"))
        {
            await viewModel.DeleteCommand.ExecuteAsync(site);
        }
    }

    private async void ConfirmBulkDeleteSites(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SitesViewModel viewModel || viewModel.SelectedSiteCount == 0)
        {
            return;
        }

        var message = $"确认批量删除选中的 {viewModel.SelectedSiteCount} 个站点吗？关联的映射和路由规则会一并级联清理。";
        if (await ConfirmAsync("确认批量删除站点", message, "确认删除"))
        {
            await viewModel.BulkDeleteCommand.ExecuteAsync(null);
        }
    }

    private async void ExportSites(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SitesViewModel viewModel) return;
        await viewModel.LoadExportPreviewAsync();
    }

    private void CloseExportPreview(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SitesViewModel viewModel)
        {
            viewModel.CloseExportPreview();
        }
    }

    private async void CopyExportSites(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SitesViewModel viewModel
            || string.IsNullOrWhiteSpace(viewModel.ExportPreviewJson))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(viewModel.ExportPreviewJson);
            viewModel.OperationMessage = "站点 JSON 已复制到剪贴板";
        }
    }

    private async void DownloadExportSites(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SitesViewModel viewModel
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider
            || string.IsNullOrWhiteSpace(viewModel.ExportPreviewJson))
        {
            return;
        }

        try
        {
            var file = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出站点 JSON",
                SuggestedFileName = $"sites_export_{DateTime.Now:yyyyMMdd}.json",
                DefaultExtension = "json",
                FileTypeChoices = JsonFileTypes
            });
            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteAsync(viewModel.ExportPreviewJson);
            viewModel.OperationMessage = $"已导出 {viewModel.SelectedExportCount} 个站点";
        }
        catch (Exception exception)
        {
            // 文件选择或写入失败时只显示局部错误，不输出导出内容。
            viewModel.OperationErrorMessage = exception.Message;
        }
    }

    private void ParseImportPreview(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SitesViewModel viewModel)
        {
            viewModel.ParseImportPreview(viewModel.ImportJsonText);
        }
    }

    private void CloseImportPreview(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SitesViewModel viewModel)
        {
            viewModel.CloseImportPreview();
        }
    }

    private async void ImportSites(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SitesViewModel viewModel ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        try
        {
            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入站点 JSON",
                AllowMultiple = false,
                FileTypeFilter = JsonFileTypes
            });
            var file = files.FirstOrDefault();
            if (file is null) return;

            await using var stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            await viewModel.ImportJsonAsync(json);
        }
        catch (Exception exception)
        {
            // 文件读取失败时保留站点列表，并在页面局部显示错误。
            viewModel.OperationErrorMessage = exception.Message;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
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
            Title = title,
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
