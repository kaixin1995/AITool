using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AITool.Application.Pricing;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Pricing;

/// <summary>
/// 模型价格服务实现：价格表存放在软件运行目录的 model-pricing.json（非数据库），
/// 首次运行从源码模板生成；读取时按文件改动时间自动刷新缓存，编辑保存后立即生效。
/// 计价基准恒为 USD；价格表中的 UsdToCny 仅用于前端展示换算。
/// </summary>
public sealed partial class ModelPricingService : IModelPricingService
{
    private const string CatalogFileName = "model-pricing.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>模型名的日期后缀（如 claude-opus-4-6-20260206 → claude-opus-4-6）。</summary>
    [GeneratedRegex(@"-\d{8}$", RegexOptions.IgnoreCase)]
    private static partial Regex DateSuffixRegex();

    /// <summary>模型名的思考等级后缀（如 gpt-5.6-high → gpt-5.6）。</summary>
    [GeneratedRegex(@"-(low|medium|high|xhigh|minimal)$", RegexOptions.IgnoreCase)]
    private static partial Regex EffortSuffixRegex();

    private readonly string _catalogPath;
    private readonly string _templateCatalogPath;
    private readonly ILogger<ModelPricingService> _logger;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    /// <summary>价格快照：条目按小写 ID 建索引；文件未变时读路径完全无锁。</summary>
    private volatile PricingSnapshot _snapshot = new(new ModelPricingCatalog(), DateTime.MinValue);

    internal sealed record PricingSnapshot(ModelPricingCatalog Catalog, DateTime LastWriteUtc)
    {
        public IReadOnlyDictionary<string, ModelPriceEntry> Index { get; } =
            Catalog.Models.GroupBy(m => m.Id.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 测试钩子：直接注入价格快照，绕过文件 IO。
    /// </summary>
    internal void SetSnapshotForTesting(ModelPricingCatalog catalog)
    {
        _snapshot = new PricingSnapshot(catalog, DateTime.MinValue);
    }

    public ModelPricingService(Microsoft.Extensions.Hosting.IHostEnvironment environment, ILogger<ModelPricingService> logger)
    {
        // 与 ModelVendorCatalogService 相同的约定：运行目录文件为准，源码模板仅首次生成。
        _catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CatalogFileName);
        _templateCatalogPath = Path.Combine(environment.ContentRootPath, CatalogFileName);
        _logger = logger;
    }

    public async Task<ModelPricingCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshot;
        DateTime lastWrite;
        try
        {
            lastWrite = File.GetLastWriteTimeUtc(_catalogPath);
        }
        catch
        {
            lastWrite = DateTime.MinValue;
        }

        // 缓存命中：文件不存在或未变动。
        if (lastWrite != DateTime.MinValue && lastWrite == snapshot.LastWriteUtc)
        {
            return snapshot.Catalog;
        }

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshot;
            try
            {
                lastWrite = File.GetLastWriteTimeUtc(_catalogPath);
            }
            catch
            {
                lastWrite = DateTime.MinValue;
            }

            if (lastWrite != DateTime.MinValue && lastWrite == snapshot.LastWriteUtc)
            {
                return snapshot.Catalog;
            }

            var catalog = await LoadOrCreateAsync(cancellationToken);
            _snapshot = new PricingSnapshot(catalog, lastWrite == DateTime.MinValue ? DateTime.MinValue : lastWrite);
            return catalog;
        }
        catch (Exception ex)
        {
            // 价格表读取/解析失败不能拖垮统计接口：退回当前快照（冷启动时为空表，仅表示全部未定价）。
            _logger.LogWarning(ex, "模型价格表加载失败，退回缓存快照：{Path}", _catalogPath);
            return snapshot.Catalog;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task<ModelPricingCatalog> SaveCatalogAsync(ModelPricingCatalog catalog, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCatalog(catalog);

        await _ioLock.WaitAsync(cancellationToken);
        try
        {
            // 原子写：先写临时文件再替换，进程中途崩溃不会留下半截 JSON。
            var tempPath = _catalogPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(normalized, JsonOptions), cancellationToken);
            File.Move(tempPath, _catalogPath, overwrite: true);
            var lastWrite = File.GetLastWriteTimeUtc(_catalogPath);
            _snapshot = new PricingSnapshot(normalized, lastWrite);
            return normalized;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public ModelPriceEntry? ResolveEntry(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var index = _snapshot.Index;
        if (index.Count == 0)
        {
            return null;
        }

        // 顺序归一化链：精确 → 去 namespace 前缀 → 去日期后缀 → 去 effort 后缀。
        // 每步成功剥离后从当前形态继续（覆盖 z-ai/glm-5.2-high 这类组合形态）。
        var work = modelName.Trim();
        foreach (var step in (Func<string, string?>[])[StripNamespace, StripDateSuffix, StripEffortSuffix])
        {
            if (index.TryGetValue(work.ToLowerInvariant(), out var entry))
            {
                return entry;
            }

            var stripped = step(work);
            if (stripped is not null)
            {
                work = stripped;
            }
        }

        return index.TryGetValue(work.ToLowerInvariant(), out var last) ? last : null;
    }

    public ModelUsageCost CalculateCostUsd(string? modelName, DateTimeOffset requestedAt, int inputTokens, int cachedTokens, int outputTokens)
    {
        var entry = ResolveEntry(modelName);
        if (entry is null)
        {
            return default;
        }

        // 峰谷条目：高峰窗口内用基准价，窗口外用低峰价。
        var (inputPrice, outputPrice, cacheReadPrice) = SelectTier(entry, requestedAt);

        // 日志的 CachedTokens 是"读+写"合并桶：统一按缓存读单价计价。
        // 绝大多数模型缓存写价为 0；Claude 系列的写价（2.5× 读价）会因此被低估，属已知近似。
        var inputCost = inputTokens * inputPrice / 1_000_000m;
        var cachedCost = cachedTokens * cacheReadPrice / 1_000_000m;
        var outputCost = outputTokens * outputPrice / 1_000_000m;
        return new ModelUsageCost
        {
            CostUsd = inputCost + cachedCost + outputCost,
            InputCostUsd = inputCost,
            CachedCostUsd = cachedCost,
            OutputCostUsd = outputCost,
            MatchedPriceId = entry.Id,
            Entry = entry
        };
    }

    /// <summary>
    /// 依据请求时间选择峰/谷档价格。
    /// </summary>
    private static (decimal Input, decimal Output, decimal CacheRead) SelectTier(ModelPriceEntry entry, DateTimeOffset requestedAt)
    {
        if (entry.OffPeak is null || entry.PeakWindows is not { Count: > 0 })
        {
            return (entry.Input, entry.Output, entry.CacheRead);
        }

        var isPeak = IsInPeakWindow(entry.PeakWindows, entry.PeakTimeZoneOffsetMinutes, requestedAt);
        if (isPeak)
        {
            return (entry.Input, entry.Output, entry.CacheRead);
        }

        var offPeak = entry.OffPeak;
        return (offPeak.Input, offPeak.Output, offPeak.CacheRead);
    }

    /// <summary>
    /// 判断请求时间（换算到高峰时区）是否落在任一高峰窗口内。窗口支持跨午夜（如 "22:00-06:00"）。
    /// </summary>
    internal static bool IsInPeakWindow(List<string> peakWindows, int timeZoneOffsetMinutes, DateTimeOffset requestedAt)
    {
        var localMinutes = (int)(requestedAt.ToUnixTimeSeconds() / 60 + timeZoneOffsetMinutes) % (24 * 60);
        if (localMinutes < 0)
        {
            localMinutes += 24 * 60;
        }

        foreach (var window in peakWindows)
        {
            if (!TryParseTimeWindow(window, out var startMinutes, out var endMinutes))
            {
                continue;
            }

            if (startMinutes <= endMinutes)
            {
                if (localMinutes >= startMinutes && localMinutes < endMinutes)
                {
                    return true;
                }
            }
            else if (localMinutes >= startMinutes || localMinutes < endMinutes)
            {
                // 跨午夜窗口。
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 解析 "HH:mm-HH:mm" 时间窗口。
    /// </summary>
    internal static bool TryParseTimeWindow(string? window, out int startMinutes, out int endMinutes)
    {
        startMinutes = 0;
        endMinutes = 0;
        if (string.IsNullOrWhiteSpace(window))
        {
            return false;
        }

        var parts = window.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TryParseTimeOfDay(parts[0], out startMinutes) || !TryParseTimeOfDay(parts[1], out endMinutes))
        {
            return false;
        }

        return true;
    }

    private static bool TryParseTimeOfDay(string text, out int minutes)
    {
        minutes = 0;
        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
        {
            return false;
        }

        if (hour is < 0 or > 24 || minute is < 0 or > 59)
        {
            return false;
        }

        minutes = hour * 60 + minute;
        return true;
    }

    private async Task<ModelPricingCatalog> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var exists = false;
        try
        {
            exists = File.Exists(_catalogPath);
        }
        catch
        {
            exists = false;
        }

        if (exists)
        {
            var json = await File.ReadAllTextAsync(_catalogPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var catalog = JsonSerializer.Deserialize<ModelPricingCatalog>(json, JsonOptions);
                if (catalog is not null)
                {
                    return NormalizeCatalog(catalog);
                }
            }
        }

        // 首次运行：从源码模板生成运行时文件。
        var initialized = await InitializeFromTemplateAsync(cancellationToken);
        try
        {
            await File.WriteAllTextAsync(_catalogPath, JsonSerializer.Serialize(initialized, JsonOptions), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入模型价格表初始文件失败：{Path}", _catalogPath);
        }

        return initialized;
    }

    private async Task<ModelPricingCatalog> InitializeFromTemplateAsync(CancellationToken cancellationToken)
    {
        var runtimePath = Path.GetFullPath(_catalogPath);
        var templatePath = Path.GetFullPath(_templateCatalogPath);
        if (!string.Equals(runtimePath, templatePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(_templateCatalogPath))
        {
            var templateJson = await File.ReadAllTextAsync(_templateCatalogPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(templateJson))
            {
                var template = JsonSerializer.Deserialize<ModelPricingCatalog>(templateJson, JsonOptions);
                if (template is not null)
                {
                    return NormalizeCatalog(template);
                }
            }
        }

        return new ModelPricingCatalog();
    }

    /// <summary>
    /// 规范化价格表：ID 去重、负价归零、峰谷字段校验、汇率范围钳制。
    /// </summary>
    private static ModelPricingCatalog NormalizeCatalog(ModelPricingCatalog catalog)
    {
        var normalized = new ModelPricingCatalog
        {
            UsdToCny = catalog.UsdToCny <= 0 ? 6.74m : Math.Clamp(catalog.UsdToCny, 0.01m, 100m)
        };

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in catalog.Models)
        {
            var id = model.Id?.Trim() ?? string.Empty;
            if (id.Length == 0 || !seenIds.Add(id))
            {
                continue;
            }

            var entry = new ModelPriceEntry
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? id : model.DisplayName.Trim(),
                Input = Math.Max(0, model.Input),
                Output = Math.Max(0, model.Output),
                CacheRead = Math.Max(0, model.CacheRead),
                CacheWrite = Math.Max(0, model.CacheWrite),
                PeakTimeZoneOffsetMinutes = model.PeakTimeZoneOffsetMinutes
            };

            // 峰谷配置只有同时具备低峰价和至少一个合法窗口时才生效。
            var windows = model.PeakWindows?
                .Where(w => TryParseTimeWindow(w, out _, out _))
                .Select(w => w.Trim())
                .ToList();
            if (model.OffPeak is not null && windows is { Count: > 0 })
            {
                entry.OffPeak = new ModelOffPeakPricing
                {
                    Input = Math.Max(0, model.OffPeak.Input),
                    Output = Math.Max(0, model.OffPeak.Output),
                    CacheRead = Math.Max(0, model.OffPeak.CacheRead),
                    CacheWrite = model.OffPeak.CacheWrite is null ? null : Math.Max(0, model.OffPeak.CacheWrite.Value)
                };
                entry.PeakWindows = windows;
                if (entry.PeakTimeZoneOffsetMinutes is < (-14 * 60) or > (14 * 60))
                {
                    entry.PeakTimeZoneOffsetMinutes = 480;
                }
            }

            normalized.Models.Add(entry);
        }

        normalized.Models.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return normalized;
    }

    /// <summary>去掉 namespace 前缀（z-ai/glm-5.2 → glm-5.2；含多个 / 取最后段）。</summary>
    private static string? StripNamespace(string modelName)
    {
        var slashIndex = modelName.LastIndexOf('/');
        return slashIndex >= 0 && slashIndex < modelName.Length - 1 ? modelName[(slashIndex + 1)..] : null;
    }

    /// <summary>去掉日期后缀（-20260206）。</summary>
    private static string? StripDateSuffix(string modelName)
    {
        var stripped = DateSuffixRegex().Replace(modelName, string.Empty);
        return stripped.Length > 0 && stripped.Length != modelName.Length ? stripped : null;
    }

    /// <summary>去掉思考等级后缀（-high 等）。</summary>
    private static string? StripEffortSuffix(string modelName)
    {
        var stripped = EffortSuffixRegex().Replace(modelName, string.Empty);
        return stripped.Length > 0 && stripped.Length != modelName.Length ? stripped : null;
    }
}
