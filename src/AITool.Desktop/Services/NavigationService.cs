namespace AITool.Desktop.Services;

public sealed class NavigationService
{
    public object? CurrentViewModel { get; private set; }

    public event EventHandler? Navigated;

    public void Navigate(object viewModel)
    {
        CurrentViewModel = viewModel;
        Navigated?.Invoke(this, EventArgs.Empty);
    }
}
