using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using AITool.Desktop.Services;
using AITool.Desktop.ViewModels;
using AITool.Desktop.Views;

namespace AITool.Desktop;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private TrayIcon? _trayIcon;
    private bool _isExiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RegisterGlobalExceptionHandlers();

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
            ConfigureWindowAndTray(window, desktop);
            _ = viewModel.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureWindowAndTray(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        window.Icon = LoadIcon();

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "AI Tool",
            IsVisible = true
        };

        var menu = new NativeMenu();
        var showItem = new NativeMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMainWindow(window);
        var exitItem = new NativeMenuItem("退出程序");
        exitItem.Click += (_, _) => ExitApplication(desktop);
        menu.Items.Add(showItem);
        menu.Items.Add(exitItem);
        _trayIcon.Menu = menu;
        _trayIcon.Clicked += (_, _) => ShowMainWindow(window);

        window.Closing += (_, eventArgs) =>
        {
            if (_isExiting)
            {
                return;
            }

            eventArgs.Cancel = true;
            window.Hide();
        };
    }

    private static WindowIcon LoadIcon()
    {
        return new WindowIcon(AssetLoader.Open(new Uri("avares://AITool.Desktop/Assets/app-icon.png")));
    }

    private static void RegisterGlobalExceptionHandlers()
    {
        // 捕获主线程和非托管线程上的未处理异常（例如 async void 抛出的异常）。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[未捕获异常] {ex}");
                Console.Error.WriteLine($"[未捕获异常] {ex}");
            }
        };

        // 捕获未观察的 Task 异常，标记为已观察，避免进程崩溃。
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[未观察的Task异常] {e.Exception}");
            Console.Error.WriteLine($"[未观察的Task异常] {e.Exception}");
            e.SetObserved();
        };

        // Avalonia UI 线程上的未处理异常。
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[UI线程异常] {e.Exception}");
            Console.Error.WriteLine($"[UI线程异常] {e.Exception}");
        };
    }

    private static void ShowMainWindow(MainWindow window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void ExitApplication(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _isExiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        desktop.Shutdown();
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }
}
