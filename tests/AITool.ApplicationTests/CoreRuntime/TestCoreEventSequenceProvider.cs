using AITool.Infrastructure.CoreRuntime;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 测试专用的 <see cref="CoreEventSequenceProvider"/> 工厂方法。
/// 使用临时目录作为 spool 根目录，测试结束后自动清理。
/// </summary>
internal static class TestCoreEventSequenceProvider
{
    /// <summary>
    /// 创建一个使用独立临时目录的序号提供器实例。
    /// </summary>
    public static CoreEventSequenceProvider Create()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"aitool-test-seq-{Guid.NewGuid():N}");
        var options = new CoreEventSpoolOptions { RootPath = tempRoot };
        var spoolStore = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);
        return new CoreEventSequenceProvider(
            options,
            spoolStore,
            NullLogger<CoreEventSequenceProvider>.Instance);
    }
}
