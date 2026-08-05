using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class DetectionTasksView : UserControl
{
    public DetectionTasksView() => InitializeComponent();

    private async void ConfirmDeleteTask(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DetectionTasksViewModel viewModel
            || sender is not Button button
            || button.DataContext is not DetectionTaskItem task)
        {
            return;
        }

        var message = $"确认删除检测任务“{task.Name}”吗？该任务的执行历史也会被删除，且操作无法恢复。";
        if (await ConfirmAsync(message))
        {
            await viewModel.DeleteCommand.ExecuteAsync(task);
        }
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return false;

        Window? dialog = null;
        var cancelButton = new Button
        {
            Content = "取消",
            Padding = new Thickness(14, 8)
        };
        var confirmButton = new Button
        {
            Content = "确认删除",
            Padding = new Thickness(14, 8)
        };

        dialog = new Window
        {
            Title = "确认删除检测任务",
            Width = 500,
            Height = 270,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(24),
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
