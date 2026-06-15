using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Core 事件序号提供器的文件持久化行为。
/// 覆盖从 meta 文件恢复、从 spool 文件恢复、连续递增等场景。
/// </summary>
public sealed class CoreEventSequenceProviderPersistenceTests
{
    /// <summary>
    /// 全新的 spool 目录（无 meta 文件、无 spool 文件），序号应从 0 开始递增。
    /// </summary>
    [Fact]
    public void Next_starts_from_zero_on_fresh_directory()
    {
        var tempRoot = CreateTempRoot();
        var (options, spoolStore) = CreateSpoolInfrastructure(tempRoot);
        var provider = new CoreEventSequenceProvider(options, spoolStore, NullLogger<CoreEventSequenceProvider>.Instance);

        provider.Next().Should().Be(1);
        provider.Next().Should().Be(2);
        provider.Next().Should().Be(3);
        provider.Current.Should().Be(3);
    }

    /// <summary>
    /// 连续递增后，序号应能持久化并恢复。
    /// 新的 provider 实例从同一目录构造时，应从持久化数据恢复并继续递增。
    /// <para>
    /// 注意：生产实现采用定时批量落盘策略，<see cref="CoreEventSequenceProvider.Next"/>
    /// 不立即写盘。测试在 Dispose 时强制 flush，模拟进程正常退出场景。
    /// </para>
    /// </summary>
    [Fact]
    public void Next_restores_from_meta_file_on_restart()
    {
        var tempRoot = CreateTempRoot();
        var (options, spoolStore) = CreateSpoolInfrastructure(tempRoot);

        // 第一个实例递增到 5
        var provider1 = new CoreEventSequenceProvider(options, spoolStore, NullLogger<CoreEventSequenceProvider>.Instance);
        for (var i = 0; i < 5; i++)
        {
            provider1.Next();
        }
        provider1.Current.Should().Be(5);

        // 模拟进程正常退出：Dispose 触发最后一次强制落盘。
        provider1.Dispose();

        // 验证 meta 文件已写入最新序号
        var metaPath = Path.Combine(tempRoot, "sequence.meta");
        File.Exists(metaPath).Should().BeTrue();
        File.ReadAllText(metaPath).Trim().Should().Be("5");

        // 第二个实例从同一目录构造，应从 meta 文件恢复到 5
        var provider2 = new CoreEventSequenceProvider(options, spoolStore, NullLogger<CoreEventSequenceProvider>.Instance);
        provider2.Current.Should().Be(5);
        provider2.Next().Should().Be(6);
    }

    /// <summary>
    /// meta 文件内容损坏时，应回退到从 spool 文件恢复。
    /// 如果 spool 中也没有事件，则从 0 开始。
    /// </summary>
    [Fact]
    public void Next_falls_back_to_zero_when_meta_corrupt_and_no_spool()
    {
        var tempRoot = CreateTempRoot();
        var (options, spoolStore) = CreateSpoolInfrastructure(tempRoot);

        // 直接写入损坏的 meta 文件
        var metaPath = Path.Combine(tempRoot, "sequence.meta");
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(metaPath, "corrupted-data");

        var provider = new CoreEventSequenceProvider(options, spoolStore, NullLogger<CoreEventSequenceProvider>.Instance);
        provider.Current.Should().Be(0);
        provider.Next().Should().Be(1);
    }

    /// <summary>
    /// meta 文件包含负数时，应视为无效，回退到 spool 扫描或从 0 开始。
    /// </summary>
    [Fact]
    public void Next_treats_negative_meta_as_invalid()
    {
        var tempRoot = CreateTempRoot();
        var (options, spoolStore) = CreateSpoolInfrastructure(tempRoot);

        var metaPath = Path.Combine(tempRoot, "sequence.meta");
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(metaPath, "-10");

        var provider = new CoreEventSequenceProvider(options, spoolStore, NullLogger<CoreEventSequenceProvider>.Instance);
        provider.Current.Should().Be(0);
        provider.Next().Should().Be(1);
    }

    /// <summary>
    /// 多次连续调用 Next 时，序号应严格单调递增，无间隙、无重复。
    /// </summary>
    [Fact]
    public void Next_produces_strictly_monotonic_sequence()
    {
        var tempRoot = CreateTempRoot();
        var (options, spoolStore) = CreateSpoolInfrastructure(tempRoot);
        var provider = new CoreEventSequenceProvider(options, spoolStore, NullLogger<CoreEventSequenceProvider>.Instance);

        var ids = new List<long>();
        for (var i = 0; i < 100; i++)
        {
            ids.Add(provider.Next());
        }

        // 应为 1, 2, 3, ..., 100
        ids.Should().BeInAscendingOrder();
        ids.Should().HaveCount(100);
        ids[0].Should().Be(1);
        ids[99].Should().Be(100);
        // 无重复
        ids.Distinct().Should().HaveCount(100);
    }

    /// <summary>
    /// 创建独立的临时 spool 根目录。
    /// </summary>
    private static string CreateTempRoot()
    {
        return Path.Combine(Path.GetTempPath(), $"aitool-test-seq-persist-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// 使用指定根目录创建 spool 基础设施（选项 + 存储）。
    /// </summary>
    private static (CoreEventSpoolOptions options, CoreEventSpoolStore spoolStore) CreateSpoolInfrastructure(string root)
    {
        var options = new CoreEventSpoolOptions { RootPath = root };
        var spoolStore = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);
        return (options, spoolStore);
    }
}
