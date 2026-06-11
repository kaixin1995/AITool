using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AITool.Admin.IntegrationTests.Infrastructure;

/// <summary>
/// 测试专用宿主环境，强制后台写入器走直写模式，避免测试等待后台批处理调度。
/// </summary>
internal sealed class CoreTestsHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "AITool.CoreTests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}
