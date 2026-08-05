using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using AITool.Desktop.Services;
using AITool.Desktop.ViewModels;
using AITool.Desktop.Views;

namespace AITool.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TokenStore>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<ApiService>();
        services.AddSingleton<SseClient>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<MainWindow>();
        _serviceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            var window = _serviceProvider.GetRequiredService<MainWindow>();
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
