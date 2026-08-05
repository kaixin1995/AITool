using Avalonia.Controls;
using Avalonia.Interactivity;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class CompatibilityView : UserControl
{
    public CompatibilityView() => InitializeComponent();

    private async void ConfirmDeleteProfile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CompatibilityViewModel viewModel ||
            sender is not Button button ||
            button.DataContext is not CompatibilityProfileItem profile)
        {
            return;
        }

        if (await ConfirmAsync("删除规则集后，引用它的模型将不再应用兼容规则。确定继续吗？"))
        {
            await viewModel.DeleteCommand.ExecuteAsync(profile);
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
            Padding = new Avalonia.Thickness(14, 8)
        };
        var confirmButton = new Button
        {
            Content = "确认删除",
            Padding = new Avalonia.Thickness(14, 8)
        };
        dialog = new Window
        {
            Title = "确认删除规则集",
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
