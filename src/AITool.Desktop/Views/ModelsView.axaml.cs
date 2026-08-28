using Avalonia.Controls;
using Avalonia.Interactivity;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class ModelsView : UserControl
{
    public ModelsView()
    {
        InitializeComponent();
    }

    private async void ConfirmDeleteMapping(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModelsViewModel viewModel ||
            sender is not Button button ||
            button.DataContext is not ModelSiteMapping mapping)
        {
            return;
        }

        if (await ConfirmAsync("确认删除映射", "删除该站点映射后，相关路由规则可能会一并清理。确定继续吗？", "确认删除"))
        {
            await viewModel.DeleteMappingCommand.ExecuteAsync(mapping);
        }
    }

    private async void ConfirmDeleteModel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModelsViewModel viewModel ||
            sender is not Button button ||
            button.DataContext is not ModelListItem model)
        {
            return;
        }

        var message = $"确认删除模型“{model.DisplayName}”吗？此操作会级联清理其站点映射、路由规则和健康监控，且不可恢复。";
        if (await ConfirmAsync("确认删除模型", message, "确认删除"))
        {
            await viewModel.DeleteCommand.ExecuteAsync(model);
        }
    }

    private async void ConfirmClearAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ModelsViewModel viewModel)
        {
            return;
        }

        const string message = "确认清空全部模型吗？此操作会清理所有模型、站点映射、路由规则和健康监控，且不可恢复。";
        if (await ConfirmAsync("确认清空全部模型", message, "确认清空"))
        {
            await viewModel.ClearAllCommand.ExecuteAsync(null);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
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
            Content = confirmText,
            Padding = new Avalonia.Thickness(14, 8)
        };
        dialog = new Window
        {
            Title = title,
            Width = 440,
            Height = 230,
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
