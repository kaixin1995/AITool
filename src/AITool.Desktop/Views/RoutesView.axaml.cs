using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class RoutesView : UserControl
{
    private RouteRuleItem? _draggedRule;
    private Point _dragStartPoint;
    private bool _isDraggingRule;

    public RoutesView()
    {
        InitializeComponent();
    }

    private void StartRuleDrag(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || FindInteractiveAncestor(e.Source as Visual) is not null
            || FindRule(e.Source as Visual) is not RouteRuleItem rule)
        {
            return;
        }

        _draggedRule = rule;
        _dragStartPoint = e.GetPosition(this);
        _isDraggingRule = false;
    }

    private void MoveRuleDrag(object? sender, PointerEventArgs e)
    {
        if (_draggedRule is null
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (!_isDraggingRule
            && Math.Abs(point.X - _dragStartPoint.X) + Math.Abs(point.Y - _dragStartPoint.Y) < 8)
        {
            return;
        }

        _isDraggingRule = true;
        if (DataContext is RoutesViewModel viewModel
            && FindRule(e.Source as Visual) is RouteRuleItem targetRule)
        {
            viewModel.MoveRuleByDrag(_draggedRule, targetRule);
        }
    }

    private void EndRuleDrag(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggedRule is not null && _isDraggingRule && DataContext is RoutesViewModel viewModel)
        {
            viewModel.CompleteRuleDrag();
        }

        _draggedRule = null;
        _isDraggingRule = false;
    }

    private static RouteRuleItem? FindRule(Visual? visual)
    {
        while (visual is not null)
        {
            if (visual.DataContext is RouteRuleItem rule)
            {
                return rule;
            }

            visual = visual.GetVisualParent();
        }

        return null;
    }

    private static Visual? FindInteractiveAncestor(Visual? visual)
    {
        while (visual is not null)
        {
            if (visual is Button or ToggleButton or ComboBox or TextBox or CheckBox)
            {
                return visual;
            }

            visual = visual.GetVisualParent();
        }

        return null;
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
