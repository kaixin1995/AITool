using Avalonia;
using AITool.Desktop.Services;

namespace AITool.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 使用文件锁而不是 Windows 专用 Mutex，保证 Linux、macOS 和 Windows 行为一致。
        using var singleInstance = SingleInstanceGuard.TryAcquire("AITool.Desktop");
        if (singleInstance is null)
        {
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
