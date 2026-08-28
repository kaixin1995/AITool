using AITool.Application.Pricing;
using AITool.Infrastructure.Pricing;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Pricing;

/// <summary>
/// 模型价格服务测试：本地 JSON 加载/保存、模型名归一化匹配、峰谷窗口判断、成本计算。
/// </summary>
public sealed class ModelPricingServiceTests : IDisposable
{
    private readonly string _rootDir;

    public ModelPricingServiceTests()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"aitool-pricing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, true); } catch { /* 临时目录清理失败不影响测试 */ }
    }

    /// <summary>
    /// 在临时目录构造服务：runtimeDir 放运行时文件，templateDir 放源码模板。
    /// </summary>
    private ModelPricingService CreateService(string? templateJson = null)
    {
        var runtimeDir = Path.Combine(_rootDir, "runtime");
        var templateDir = Path.Combine(_rootDir, "template");
        Directory.CreateDirectory(runtimeDir);
        Directory.CreateDirectory(templateDir);
        if (templateJson is not null)
        {
            File.WriteAllText(Path.Combine(templateDir, "model-pricing.json"), templateJson);
        }

        var environment = new FakeHostEnvironment(Path.Combine(templateDir, ".."));
        // 服务按 BaseDirectory 找运行时文件：用环境变量无法改 BaseDirectory，
        // 这里通过反射替换路径不便，改为直接服务构造后用保存接口写入运行时文件的替代方案——
        // 实际上服务构造函数固定用 AppDomain.BaseDirectory，测试进程的运行时文件与其他测试共享。
        // 因此单测聚焦纯计算逻辑（快照构造 + 解析），文件 IO 用公共快照目录隔离。
        return new ModelPricingService(environment, NullLogger<ModelPricingService>.Instance);
    }

    [Fact]
    public void ResolveEntry_matches_exact_namespace_date_and_effort_variants()
    {
        var service = CreateService();
        // 直接注入快照（绕过文件 IO）：验证归一化匹配链。
        SetSnapshot(service, new ModelPricingCatalog
        {
            UsdToCny = 6.74m,
            Models =
            [
                new ModelPriceEntry { Id = "glm-5.2", DisplayName = "GLM-5.2", Input = 1.4m, Output = 4.4m, CacheRead = 0.26m },
                new ModelPriceEntry { Id = "claude-opus-4-6", DisplayName = "Claude Opus 4.6", Input = 5, Output = 25, CacheRead = 0.5m, CacheWrite = 6.25m },
                new ModelPriceEntry { Id = "gpt-5.6", DisplayName = "GPT-5.6", Input = 5, Output = 30, CacheRead = 0.5m }
            ]
        });

        service.ResolveEntry("glm-5.2").Should().NotBeNull("精确命中");
        service.ResolveEntry("z-ai/glm-5.2").Should().NotBeNull("去 namespace 前缀后命中");
        service.ResolveEntry("qwen/qwen3.6-27b").Should().BeNull("去前缀后仍未命中 → null");
        service.ResolveEntry("claude-opus-4-6-20260206").Should().NotBeNull("去日期后缀后命中");
        service.ResolveEntry("gpt-5.6-high").Should().NotBeNull("去 effort 后缀后命中");
        service.ResolveEntry("z-ai/gpt-5.6-xhigh").Should().NotBeNull("组合形态：去前缀 + 去 effort");
        service.ResolveEntry("unknown-model").Should().BeNull("未定价模型返回 null");
        service.ResolveEntry(null).Should().BeNull();
        service.ResolveEntry("  ").Should().BeNull();
    }

    [Fact]
    public void CalculateCostUsd_uses_three_segment_pricing()
    {
        var service = CreateService();
        SetSnapshot(service, new ModelPricingCatalog
        {
            Models = [new ModelPriceEntry { Id = "claude-opus-5", Input = 5, Output = 25, CacheRead = 0.5m, CacheWrite = 6.25m }]
        });

        // input=1000_000（新输入）、cached=200_000、output=100_000：
        // 1000_000*5/1M + 200_000*0.5/1M + 100_000*25/1M = 5 + 0.1 + 2.5 = 7.6
        var cost = service.CalculateCostUsd("claude-opus-5", DateTimeOffset.UtcNow, 1_000_000, 200_000, 100_000);
        cost.CostUsd.Should().Be(7.6m);
        cost.MatchedPriceId.Should().Be("claude-opus-5");

        // 未定价模型 → null（不计 0 成本，便于前端区分）。
        service.CalculateCostUsd("no-such-model", DateTimeOffset.UtcNow, 100, 0, 10).CostUsd.Should().BeNull();
    }

    [Fact]
    public void CalculateCostUsd_applies_off_peak_tier_outside_peak_windows()
    {
        var service = CreateService();
        SetSnapshot(service, new ModelPricingCatalog
        {
            Models =
            [
                new ModelPriceEntry
                {
                    Id = "deepseek-v4-flash",
                    Input = 0.445m, Output = 1.335m, CacheRead = 0.0148m,
                    OffPeak = new ModelOffPeakPricing { Input = 0.2226m, Output = 0.6677m, CacheRead = 0.0074m },
                    PeakWindows = ["09:00-12:00", "14:00-18:00"],
                    PeakTimeZoneOffsetMinutes = 480
                }
            ]
        });

        // 北京时间 2026-08-17 10:00 = UTC 02:00 → 高峰窗口内（09:00-12:00）→ 基准价。
        var peakTime = new DateTimeOffset(2026, 8, 17, 2, 0, 0, TimeSpan.Zero);
        var peakCost = service.CalculateCostUsd("deepseek-v4-flash", peakTime, 1_000_000, 0, 1_000_000);
        peakCost.CostUsd.Should().BeApproximately(0.445m + 1.335m, 0.0001m);

        // 北京时间 22:00 = UTC 14:00 → 窗口外 → 低峰价。
        var offPeakTime = new DateTimeOffset(2026, 8, 17, 14, 0, 0, TimeSpan.Zero);
        var offPeakCost = service.CalculateCostUsd("deepseek-v4-flash", offPeakTime, 1_000_000, 0, 1_000_000);
        offPeakCost.CostUsd.Should().BeApproximately(0.2226m + 0.6677m, 0.0001m);

        // 北京时间 13:00（午休）= UTC 05:00 → 窗口外 → 低峰价。
        var noonBreak = new DateTimeOffset(2026, 8, 17, 5, 0, 0, TimeSpan.Zero);
        service.CalculateCostUsd("deepseek-v4-flash", noonBreak, 1_000_000, 0, 1_000_000).CostUsd
            .Should().BeApproximately(0.2226m + 0.6677m, 0.0001m);
    }

    [Fact]
    public void IsInPeakWindow_supports_overnight_windows_and_boundaries()
    {
        var windows = new List<string> { "22:00-06:00" };
        var utc = (int hour) => new DateTimeOffset(2026, 8, 17, hour, 30, 0, TimeSpan.Zero);

        // UTC+8：UTC 15:00 = 北京 23:00 → 窗口内。
        ModelPricingService.IsInPeakWindow(windows, 480, utc(15)).Should().BeTrue();
        // UTC 10:00 = 北京 18:00 → 窗口外。
        ModelPricingService.IsInPeakWindow(windows, 480, utc(10)).Should().BeFalse();
        // UTC 21:00 = 北京 05:00 → 跨午夜窗口的下半段。
        ModelPricingService.IsInPeakWindow(windows, 480, utc(21)).Should().BeTrue();

        // 边界：窗口含起点、不含终点（北京 09:00 在 09:00-12:00 内；12:00 不在）。
        var dayWindows = new List<string> { "09:00-12:00" };
        var at = (int hour, int minute) => new DateTimeOffset(2026, 8, 17, hour, minute, 0, TimeSpan.Zero);
        ModelPricingService.IsInPeakWindow(dayWindows, 480, at(1, 0)).Should().BeTrue("北京 09:00 含起点");
        ModelPricingService.IsInPeakWindow(dayWindows, 480, at(3, 59)).Should().BeTrue("北京 11:59");
        ModelPricingService.IsInPeakWindow(dayWindows, 480, at(4, 0)).Should().BeFalse("北京 12:00 不含终点");
    }

    [Fact]
    public async Task SaveCatalogAsync_normalizes_and_persists()
    {
        var service = CreateService();
        var catalog = new ModelPricingCatalog
        {
            UsdToCny = -1,
            Models =
            [
                new ModelPriceEntry { Id = "  model-a  ", DisplayName = "", Input = -5, Output = 3 },
                new ModelPriceEntry { Id = "model-a", Input = 1, Output = 2 },
                new ModelPriceEntry
                {
                    Id = "model-b",
                    Input = 1, Output = 2,
                    OffPeak = new ModelOffPeakPricing { Input = 0.5m },
                    PeakWindows = ["09:00-12:00", "bad-window"]
                }
            ]
        };

        var saved = await service.SaveCatalogAsync(catalog);

        // 汇率非法回退默认；重复 ID 去重（保留首条）；负价归零；非法窗口被剔除后峰谷仍生效。
        saved.UsdToCny.Should().Be(6.74m);
        saved.Models.Should().HaveCount(2);
        saved.Models[0].Id.Should().Be("model-a");
        saved.Models[0].Input.Should().Be(0);
        saved.Models.First(m => m.Id == "model-b").PeakWindows.Should().BeEquivalentTo(["09:00-12:00"]);

        // 保存后立即读取（新快照）能命中。
        service.ResolveEntry("model-b").Should().NotBeNull();

        // 无峰谷配置的条目：offPeak 被丢弃。
        saved.Models[0].OffPeak.Should().BeNull();
    }

    [Fact]
    public async Task GetCatalogAsync_falls_back_gracefully_when_file_corrupted()
    {
        var runtimePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model-pricing.json");
        var original = File.Exists(runtimePath) ? await File.ReadAllTextAsync(runtimePath) : null;
        try
        {
            File.WriteAllText(runtimePath, "{ this is not valid json !!!");
            var service = CreateService();

            // 损坏的价格表不能把统计接口打挂：退回空目录（全部未定价）而不是抛异常。
            var catalog = await service.GetCatalogAsync();
            catalog.Should().NotBeNull();
            catalog.Models.Should().BeEmpty();
            catalog.UsdToCny.Should().BeGreaterThan(0);
        }
        finally
        {
            if (original is null)
            {
                try { File.Delete(runtimePath); } catch { /* 忽略 */ }
            }
            else
            {
                await File.WriteAllTextAsync(runtimePath, original);
            }
        }
    }

    /// <summary>
    /// 注入测试快照（服务提供了 internal 测试钩子，绕过文件 IO）。
    /// </summary>
    private static void SetSnapshot(ModelPricingService service, ModelPricingCatalog catalog)
    {
        service.SetSnapshotForTesting(catalog);
    }

    private sealed class FakeHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
