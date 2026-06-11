using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 spool 文件轮转/清理行为。
/// 覆盖 ExtractDateFromFileName 解析、PruneExpiredFilesAsync 年龄清理和数量清理等场景。
/// </summary>
public sealed class CoreEventSpoolRotationTests
{
    /// <summary>
    /// 标准文件名格式应正确提取日期。
    /// </summary>
    [Theory]
    [InlineData("events-20260610.jsonl", 2026, 6, 10)]
    [InlineData("events-20250101.jsonl", 2025, 1, 1)]
    [InlineData("events-20241231.jsonl", 2024, 12, 31)]
    public void ExtractDateFromFileName_parses_valid_filenames(string fileName, int expectedYear, int expectedMonth, int expectedDay)
    {
        var result = CoreEventSpoolStore.ExtractDateFromFileName(fileName);

        result.Should().NotBeNull();
        result!.Value.Year.Should().Be(expectedYear);
        result.Value.Month.Should().Be(expectedMonth);
        result.Value.Day.Should().Be(expectedDay);
    }

    /// <summary>
    /// 非标准文件名格式应返回 null，不会抛异常。
    /// </summary>
    [Theory]
    [InlineData("events-.jsonl")]
    [InlineData("events-2026.jsonl")]
    [InlineData("events-2026061.jsonl")]
    [InlineData("events-202606101.jsonl")]
    [InlineData("events-abcdefgh.jsonl")]
    [InlineData("other-file.jsonl")]
    [InlineData("noext")]
    [InlineData("")]
    public void ExtractDateFromFileName_returns_null_for_invalid_filenames(string fileName)
    {
        var result = CoreEventSpoolStore.ExtractDateFromFileName(fileName);
        result.Should().BeNull();
    }

    /// <summary>
    /// 文件名中日期部分不是合法日期（如月份13）时应返回 null。
    /// </summary>
    [Fact]
    public void ExtractDateFromFileName_returns_null_for_invalid_date()
    {
        var result = CoreEventSpoolStore.ExtractDateFromFileName("events-20261301.jsonl");
        result.Should().BeNull();
    }

    /// <summary>
    /// 包含完整路径的文件名应只取文件名部分进行解析。
    /// </summary>
    [Fact]
    public void ExtractDateFromFileName_works_with_full_path()
    {
        var fullPath = Path.Combine("/some/deep/path", "events-20260610.jsonl");
        var result = CoreEventSpoolStore.ExtractDateFromFileName(fullPath);

        result.Should().NotBeNull();
        result!.Value.Year.Should().Be(2026);
        result.Value.Month.Should().Be(6);
        result.Value.Day.Should().Be(10);
    }

    /// <summary>
    /// 超过 MaxAgeDays 的旧文件应被删除，不超过的应保留。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_deletes_files_older_than_MaxAgeDays()
    {
        var rootPath = CreateTempRoot();
        try
        {
            // 创建一个 MaxAgeDays=5 的配置
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 5, MaxFileCount = 0 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            // 创建一个 10 天前的旧文件
            var oldDate = DateTimeOffset.Now.AddDays(-10);
            var oldFile = Path.Combine(rootPath, $"events-{oldDate:yyyyMMdd}.jsonl");
            await File.WriteAllTextAsync(oldFile, "");

            // 创建一个 2 天前的新文件
            var recentDate = DateTimeOffset.Now.AddDays(-2);
            var recentFile = Path.Combine(rootPath, $"events-{recentDate:yyyyMMdd}.jsonl");
            await File.WriteAllTextAsync(recentFile, "");

            var deletedCount = await store.PruneExpiredFilesAsync();

            deletedCount.Should().Be(1);
            File.Exists(oldFile).Should().BeFalse("旧文件应被删除");
            File.Exists(recentFile).Should().BeTrue("新文件应保留");
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// MaxAgeDays 设为 0 时不应按天数删除任何文件。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_skips_age_cleanup_when_MaxAgeDays_is_zero()
    {
        var rootPath = CreateTempRoot();
        try
        {
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 0, MaxFileCount = 0 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            // 创建一个很旧的文件
            var oldDate = DateTimeOffset.Now.AddDays(-365);
            var oldFile = Path.Combine(rootPath, $"events-{oldDate:yyyyMMdd}.jsonl");
            await File.WriteAllTextAsync(oldFile, "");

            var deletedCount = await store.PruneExpiredFilesAsync();

            deletedCount.Should().Be(0);
            File.Exists(oldFile).Should().BeTrue("MaxAgeDays=0 不应删除任何文件");
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// 文件总数超过 MaxFileCount 时，应从最旧文件开始删除多余部分。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_deletes_oldest_files_over_MaxFileCount()
    {
        var rootPath = CreateTempRoot();
        try
        {
            // 最多保留 3 个文件
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 0, MaxFileCount = 3 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            // 创建 5 个不同日期的文件，从旧到新
            var files = new List<string>();
            for (var i = 4; i >= 0; i--)
            {
                var date = DateTimeOffset.Now.AddDays(-i);
                var filePath = Path.Combine(rootPath, $"events-{date:yyyyMMdd}.jsonl");
                await File.WriteAllTextAsync(filePath, "");
                files.Add(filePath);
            }

            var deletedCount = await store.PruneExpiredFilesAsync();

            // 应删除最旧的 2 个文件（5 - 3 = 2）
            deletedCount.Should().Be(2);
            // 最旧的 2 个文件应被删除
            File.Exists(files[0]).Should().BeFalse("最旧文件应被删除");
            File.Exists(files[1]).Should().BeFalse("次旧文件应被删除");
            // 最新的 3 个文件应保留
            File.Exists(files[2]).Should().BeTrue("应保留");
            File.Exists(files[3]).Should().BeTrue("应保留");
            File.Exists(files[4]).Should().BeTrue("应保留");
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// MaxFileCount 设为 0 时不应按数量删除任何文件。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_skips_count_cleanup_when_MaxFileCount_is_zero()
    {
        var rootPath = CreateTempRoot();
        try
        {
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 0, MaxFileCount = 0 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            // 创建 10 个文件
            for (var i = 0; i < 10; i++)
            {
                var date = DateTimeOffset.Now.AddDays(-i);
                var filePath = Path.Combine(rootPath, $"events-{date:yyyyMMdd}.jsonl");
                await File.WriteAllTextAsync(filePath, "");
            }

            var deletedCount = await store.PruneExpiredFilesAsync();

            deletedCount.Should().Be(0);
            Directory.GetFiles(rootPath).Length.Should().Be(10);
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// 年龄清理和数量清理联合工作时，先删超龄的，再删超数的。
    /// 如果年龄清理已将文件数量降到 MaxFileCount 以下，则数量清理阶段无需再删。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_applies_age_then_count_cleanup()
    {
        var rootPath = CreateTempRoot();
        try
        {
            // 最多保留 5 天、最多 2 个文件
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 5, MaxFileCount = 2 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            // 创建文件：2 个超龄（10天、8天前），2 个在龄内（3天、1天前）
            var day10 = DateTimeOffset.Now.AddDays(-10);
            var day8 = DateTimeOffset.Now.AddDays(-8);
            var day3 = DateTimeOffset.Now.AddDays(-3);
            var day1 = DateTimeOffset.Now.AddDays(-1);

            var file10 = Path.Combine(rootPath, $"events-{day10:yyyyMMdd}.jsonl");
            var file8 = Path.Combine(rootPath, $"events-{day8:yyyyMMdd}.jsonl");
            var file3 = Path.Combine(rootPath, $"events-{day3:yyyyMMdd}.jsonl");
            var file1 = Path.Combine(rootPath, $"events-{day1:yyyyMMdd}.jsonl");

            await File.WriteAllTextAsync(file10, "");
            await File.WriteAllTextAsync(file8, "");
            await File.WriteAllTextAsync(file3, "");
            await File.WriteAllTextAsync(file1, "");

            var deletedCount = await store.PruneExpiredFilesAsync();

            // 年龄阶段：删掉 10天和8天的（2个）
            // 数量阶段：剩余 2 个，等于 MaxFileCount=2，无需再删
            deletedCount.Should().Be(2);
            File.Exists(file10).Should().BeFalse();
            File.Exists(file8).Should().BeFalse();
            File.Exists(file3).Should().BeTrue();
            File.Exists(file1).Should().BeTrue();
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// 年龄清理后文件数仍超过 MaxFileCount 时，数量阶段应进一步删除最旧的。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_count_stage_cleans_up_after_age_stage()
    {
        var rootPath = CreateTempRoot();
        try
        {
            // 最多保留 30 天、最多 2 个文件
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 30, MaxFileCount = 2 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            // 创建 4 个在龄内的文件
            var files = new List<string>();
            for (var i = 3; i >= 0; i--)
            {
                var date = DateTimeOffset.Now.AddDays(-i);
                var filePath = Path.Combine(rootPath, $"events-{date:yyyyMMdd}.jsonl");
                await File.WriteAllTextAsync(filePath, "");
                files.Add(filePath);
            }

            var deletedCount = await store.PruneExpiredFilesAsync();

            // 年龄阶段：4个都在30天内，不删
            // 数量阶段：4 > MaxFileCount=2，删最旧的2个
            deletedCount.Should().Be(2);
            File.Exists(files[0]).Should().BeFalse("最旧应被数量阶段删除");
            File.Exists(files[1]).Should().BeFalse("次旧应被数量阶段删除");
            File.Exists(files[2]).Should().BeTrue("应保留");
            File.Exists(files[3]).Should().BeTrue("应保留");
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// 空目录不应报错，返回 0。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_returns_zero_on_empty_directory()
    {
        var rootPath = CreateTempRoot();
        try
        {
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 30, MaxFileCount = 10 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            var deletedCount = await store.PruneExpiredFilesAsync();

            deletedCount.Should().Be(0);
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// 文件名不符合 spool 命名规范的文件不应被清理（ExtractDateFromFileName 返回 null 时跳过年龄清理，但可参与数量清理）。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_handles_non_spool_files_gracefully()
    {
        var rootPath = CreateTempRoot();
        try
        {
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 5, MaxFileCount = 0 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            // 创建一个不符合命名规范的文件
            var weirdFile = Path.Combine(rootPath, "other-file.jsonl");
            await File.WriteAllTextAsync(weirdFile, "");

            var deletedCount = await store.PruneExpiredFilesAsync();

            // 不符合 events-*.jsonl 模式的文件不会被 EnumerateSpoolFiles 枚举到
            deletedCount.Should().Be(0);
            File.Exists(weirdFile).Should().BeTrue("非 spool 文件不应被清理");
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// 验证年龄清理的边界行为：使用距离足够远的日期来确保不出现跨日边界问题。
    /// 超龄文件被删除，未超龄文件保留。
    /// </summary>
    [Fact]
    public async Task PruneExpiredFilesAsync_respects_age_boundary()
    {
        var rootPath = CreateTempRoot();
        try
        {
            var options = new CoreEventSpoolOptions { RootPath = rootPath, MaxAgeDays = 5, MaxFileCount = 0 };
            var store = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);

            // 创建一个 10 天前的文件（确定超龄）
            var oldDate = DateTimeOffset.Now.AddDays(-10);
            var oldFile = Path.Combine(rootPath, $"events-{oldDate:yyyyMMdd}.jsonl");
            await File.WriteAllTextAsync(oldFile, "");

            // 创建一个 1 天前的文件（确定在龄内）
            var recentDate = DateTimeOffset.Now.AddDays(-1);
            var recentFile = Path.Combine(rootPath, $"events-{recentDate:yyyyMMdd}.jsonl");
            await File.WriteAllTextAsync(recentFile, "");

            var deletedCount = await store.PruneExpiredFilesAsync();

            deletedCount.Should().Be(1);
            File.Exists(oldFile).Should().BeFalse("超龄文件应被删除");
            File.Exists(recentFile).Should().BeTrue("在龄文件应保留");
        }
        finally
        {
            CleanupDirectory(rootPath);
        }
    }

    /// <summary>
    /// 创建独立的临时 spool 根目录。
    /// </summary>
    private static string CreateTempRoot()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"aitool-spool-rotation-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        return rootPath;
    }

    /// <summary>
    /// 清理临时目录。
    /// </summary>
    private static void CleanupDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
