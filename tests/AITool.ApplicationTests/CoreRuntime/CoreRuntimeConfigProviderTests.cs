using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Core 运行时配置提供器的基础行为。
/// </summary>
public sealed class CoreRuntimeConfigProviderTests
{
    /// <summary>
    /// 未设置配置时应返回未就绪。
    /// </summary>
    [Fact]
    public void GetCurrent_returns_null_before_any_snapshot_loaded()
    {
        var provider = CreateProvider();

        provider.IsReady.Should().BeFalse();
        provider.GetCurrent().Should().BeNull();
    }

    /// <summary>
    /// 设置配置后应可读取，并进入就绪状态。
    /// </summary>
    [Fact]
    public void SetCurrent_marks_provider_ready_and_replaces_snapshot_atomically()
    {
        var provider = CreateProvider();
        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = 9,
            ConfigHash = "sha256:test",
            GeneratedAt = new DateTimeOffset(2026, 6, 9, 12, 0, 0, TimeSpan.Zero)
        };

        provider.SetCurrent(snapshot);

        provider.IsReady.Should().BeTrue();
        provider.GetCurrent().Should().BeSameAs(snapshot);
    }

    /// <summary>
    /// 从本地文件恢复成功配置后，应重新进入就绪状态。
    /// </summary>
    [Fact]
    public async Task TryLoadFromFileAsync_restores_snapshot_from_last_good_config_file()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"aitool-core-runtime-test-{Guid.NewGuid():N}.json");
        try
        {
            var writer = CreateProvider(filePath);
            var snapshot = new CoreRuntimeConfigSnapshot
            {
                ConfigVersion = 15,
                ConfigHash = "sha256:file-test",
                GeneratedAt = new DateTimeOffset(2026, 6, 9, 13, 0, 0, TimeSpan.Zero)
            };
            writer.SetCurrent(snapshot);
            await Task.Delay(150);

            var reader = CreateProvider(filePath);
            var loaded = await reader.TryLoadFromFileAsync();

            loaded.Should().BeTrue();
            reader.IsReady.Should().BeTrue();
            reader.GetCurrent()!.ConfigVersion.Should().Be(15);
            reader.GetCurrent()!.ConfigHash.Should().Be("sha256:file-test");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    /// <summary>
    /// 构造带本地配置文件选项的 Provider，便于验证 last-good-config 行为。
    /// </summary>
    private static CoreRuntimeConfigProvider CreateProvider(string? filePath = null)
    {
        return new CoreRuntimeConfigProvider(
            new CoreRuntimeConfigFileOptions
            {
                FilePath = filePath ?? Path.Combine(Path.GetTempPath(), $"aitool-core-runtime-provider-{Guid.NewGuid():N}.json")
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CoreRuntimeConfigProvider>.Instance);
    }
}