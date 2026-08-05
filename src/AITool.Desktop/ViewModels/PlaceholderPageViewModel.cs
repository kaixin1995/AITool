namespace AITool.Desktop.ViewModels;

public sealed class PlaceholderPageViewModel : ViewModelBase
{
    public PlaceholderPageViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }
    public string Description { get; }
}
