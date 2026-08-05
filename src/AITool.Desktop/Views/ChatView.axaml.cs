using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AITool.Desktop.Models;
using AITool.Desktop.ViewModels;

namespace AITool.Desktop.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void HandleInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            || DataContext is not ChatViewModel viewModel
            || !viewModel.SendCommand.CanExecute(null))
        {
            return;
        }

        e.Handled = true;
        viewModel.SendCommand.Execute(null);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
            foreach (var message in _viewModel.Messages)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is null) return;

        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
        foreach (var message in _viewModel.Messages)
        {
            message.PropertyChanged += OnMessagePropertyChanged;
        }

        QueueScrollToEnd(true);
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ChatMessage message in e.OldItems)
            {
                message.PropertyChanged -= OnMessagePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ChatMessage message in e.NewItems)
            {
                message.PropertyChanged += OnMessagePropertyChanged;
            }
        }

        QueueScrollToEnd(true);
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessage.Content)
            or nameof(ChatMessage.Reasoning)
            or nameof(ChatMessage.Attempts)
            or nameof(ChatMessage.TotalDurationMs)
            or nameof(ChatMessage.IsStreaming)
            or nameof(ChatMessage.IsError))
        {
            QueueScrollToEnd(false);
        }
    }

    private void QueueScrollToEnd(bool force)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var isNearBottom = MessagesScrollViewer.Offset.Y
                + MessagesScrollViewer.Viewport.Height
                >= MessagesScrollViewer.Extent.Height - 32;
            if (force || isNearBottom)
            {
                MessagesScrollViewer.ScrollToEnd();
            }
        }, DispatcherPriority.Background);
    }
}
