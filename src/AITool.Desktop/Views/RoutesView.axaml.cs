using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class RoutesView : UserControl
{
    public RoutesView()
    {
        InitializeComponent();
    }

    private async void SelectEntry(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutesViewModel viewModel
            || sender is not ToggleButton button
            || button.DataContext is not RouteEntry entry)
        {
            return;
        }

        if (viewModel.IsDirty
            && !await ConfirmAsync("当前路由入口有未保存修改，切换入口会丢弃这些修改。确定继续吗？", "放弃修改"))
        {
            return;
        }

        await viewModel.SelectEntryAsync(entry);
    }

    private async void ConfirmDeleteEntry(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RoutesViewModel viewModel ||
            viewModel.SelectedEntry is null)
        {
            return;
        }

        if (await ConfirmAsync("删除当前路由入口会同时删除其全部候选规则。确定继续吗？"))
        {
            await viewModel.DeleteEntryCommand.ExecuteAsync(null);
        }
    }

    private async Task<bool> ConfirmAsync(string message, string confirmText = "确认删除")
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
            Title = confirmText == "确认删除" ? "确认删除路由入口" : "确认放弃修改",
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
