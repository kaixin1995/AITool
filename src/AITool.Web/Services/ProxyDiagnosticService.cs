using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Common;
using AITool.Application.Proxy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AITool.Web.Services;

/// <summary>
/// 代理请求诊断上下文，聚合请求、路由、协议、转换后正文与响应结果。
/// </summary>
public sealed class ProxyDiagnosticContext
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public Guid? TraceId { get; set; }
    public string ClientProtocol { get; set; } = string.Empty;
    public string RequestSource { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public string RequestPath { get; set; } = string.Empty;
    public string RouteName { get; set; } = string.Empty;
    public Guid? TargetSiteId { get; set; }
    public string TargetSiteName { get; set; } = string.Empty;
    public string TargetBaseUrl { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string UpstreamProtocol { get; set; } = string.Empty;
    public string ForwardingMode { get; set; } = string.Empty; // "bridge" / "direct"
    /// <summary>
    /// 客户端请求头快照（必须经 <see cref="SnapshotHeaders"/> 在请求线程上拷贝，
    /// 不能直接持有 Request.Headers——转储在线程池构建时请求可能已结束并被回收）。
    /// </summary>
    public Dictionary<string, string>? ClientHeaders { get; set; }
    public string RawClientRequestBody { get; set; } = string.Empty;
    public string PreparedRequestBody { get; set; } = string.Empty;
    public ProxyForwardResult Result { get; set; } = null!;

    /// <summary>
    /// 在请求线程上把请求头拷贝为普通字典（避免线程池读取已回收的 IHeaderDictionary）。
    /// </summary>
    public static Dictionary<string, string>? SnapshotHeaders(IHeaderDictionary? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return null;
        }

        var snapshot = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            snapshot[key] = value.ToString();
        }

        return snapshot;
    }
}

/// <summary>
/// 代理诊断转储记录元数据项。
/// </summary>
public sealed class ProxyDiagnosticDumpItem
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty; // "failure" | "sample"
    public DateTimeOffset Timestamp { get; set; }
    public string RouteName { get; set; } = string.Empty;
    public string SiteName { get; set; } = string.Empty;
    public string RequestModel { get; set; } = string.Empty;
    public string AttemptedModel { get; set; } = string.Empty;
    public string ClientProtocol { get; set; } = string.Empty;
    public string UpstreamProtocol { get; set; } = string.Empty;
    public string ForwardingMode { get; set; } = string.Empty;
    public int? StatusCode { get; set; }
    public bool Success { get; set; }
    public int TotalDurationMs { get; set; }
    public string ErrorSummary { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
}

/// <summary>
/// 成功请求诊断采样状态（默认关闭，临时开启最多 10 分钟自动关闭，防硬盘爆满）。
/// </summary>
public sealed class DiagnosticSamplingStatus
{
    public bool Enabled { get; set; }
    public int RemainingSeconds { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public int MaxDurationMinutes { get; set; } = 10;
}

/// <summary>
/// 诊断抓包与自愈试探的动态限制配置。可在前端页面上随时临时调整，即时生效。
/// </summary>
public sealed class DiagnosticConfigDto
{
    /// <summary>
    /// 诊断抓包允许记录的最大请求/响应正文体积（MB，范围 1 ~ 50）。
    /// </summary>
    public int MaxBodyLengthMb { get; set; } = 4;

    /// <summary>
    /// AI 协议自愈单轮调试允许保留的最大响应体积（MB，范围 1 ~ 20）。
    /// </summary>
    public int MaxRoundResponseMb { get; set; } = 2;

    /// <summary>
    /// 历史转储文件保留天数（范围 1 ~ 30）。
    /// </summary>
    public int RetentionDays { get; set; } = 3;

    /// <summary>
    /// 单日单目录失败抓包最大文件保留数量（范围 10 ~ 500）。
    /// </summary>
    public int MaxFailuresPerDay { get; set; } = 50;
}

/// <summary>
/// 代理请求诊断服务接口：负责自动记录失败请求的完整上下文（用于二次复现）以及成功请求的选择性对比采样。
/// </summary>
public interface IProxyDiagnosticService
{
    /// <summary>
    /// 记录一次代理调用的完整诊断信息。
    /// </summary>
    void RecordDiagnostic(ProxyDiagnosticContext context);

    /// <summary>
    /// 获取最近的失败诊断转储与成功样本清单。
    /// </summary>
    IReadOnlyList<ProxyDiagnosticDumpItem> ListRecentDumps(int limit = 50);

    /// <summary>
    /// 读取指定转储文件的完整 JSON 内容。
    /// </summary>
    string? ReadDumpContent(string fileName);

    /// <summary>
    /// 获取当前成功请求采样状态。
    /// </summary>
    DiagnosticSamplingStatus GetSuccessSamplingStatus();

    /// <summary>
    /// 临时开启成功请求采样（最长 10 分钟，到期自动关闭）。
    /// </summary>
    DiagnosticSamplingStatus EnableSuccessSampling(int durationMinutes = 10);

    /// <summary>
    /// 关闭成功请求采样。
    /// </summary>
    DiagnosticSamplingStatus DisableSuccessSampling();

    /// <summary>
    /// 清理超过保留天数的历史抓包与样本文件（默认保留 3 天）。
    /// </summary>
    int PruneOldDumps(int? retentionDays = null);

    /// <summary>
    /// 清空所有诊断抓包和样本文件。
    /// </summary>
    int ClearAllDumps();

    /// <summary>
    /// 获取当前诊断限制参数。
    /// </summary>
    DiagnosticConfigDto GetConfig();

    /// <summary>
    /// 动态修改诊断限制参数（即时生效）。
    /// </summary>
    DiagnosticConfigDto UpdateConfig(DiagnosticConfigDto config);

    int MaxBodyLengthBytes { get; }
    int MaxRoundResponseBytes { get; }
    int RetentionDays { get; }
    int MaxFailuresPerDay { get; }
}

/// <summary>
/// 代理请求诊断服务实现：
/// 1. 失败请求：在开发者功能开启时自动抓包生成包含原始请求体、转换后请求体、路由、站点、协议、模式及重放命令的独立
///    JSON 文件（查看转储的管理端点同样受开发者功能门控，写入与查看权限对齐）；结构化失败日志不受此门控；
/// 2. 成功请求：默认不落盘；仅当用户通过界面临时开启（最长 10 分钟）时才进行受控对比采样，到期自动关闭，防止硬盘爆满；
/// 3. 生命周期与保留：历史抓包默认仅保留 3 天，后台自动循环修剪，支持单日数量上限限制。
/// </summary>
public sealed class ProxyDiagnosticService : IProxyDiagnosticService
{
    private const int MaxSamplesPerDayDirectory = 20;
    private const int MaxRecentDumpsInMemory = 60;
    private const int MaxSamplingMinutes = 10;

    private readonly ILogger<ProxyDiagnosticService> _logger;
    private readonly ProxyRequestMetadataCache? _metadataCache;
    private readonly ConcurrentDictionary<string, int> _successCounterByRoute = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dumpLock = new();
    private readonly LinkedList<ProxyDiagnosticDumpItem> _recentDumps = [];

    private int _maxBodyLengthMb = 4;
    private int _maxRoundResponseMb = 2;
    private int _retentionDays = 3;
    private int _maxFailuresPerDay = 50;

    private DateTimeOffset? _successSamplingExpiresAtUtc;
    private DateTimeOffset _lastGlobalPruneAt = DateTimeOffset.MinValue;

    public int MaxBodyLengthBytes => _maxBodyLengthMb * 1024 * 1024;
    public int MaxRoundResponseBytes => _maxRoundResponseMb * 1024 * 1024;
    public int RetentionDays => _retentionDays;
    public int MaxFailuresPerDay => _maxFailuresPerDay;

    /// <summary>
    /// metadataCache 用于读取开发者功能开关；为 null（单元测试直构）时不做门控，始终落盘。
    /// </summary>
    public ProxyDiagnosticService(ILogger<ProxyDiagnosticService> logger, ProxyRequestMetadataCache? metadataCache = null)
    {
        _logger = logger;
        _metadataCache = metadataCache;
        // 启动时在后台触发一次历史清理
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                PruneOldDumps(_retentionDays);
            }
            catch
            {
            }
        });
    }

    public DiagnosticConfigDto GetConfig()
    {
        return new DiagnosticConfigDto
        {
            MaxBodyLengthMb = _maxBodyLengthMb,
            MaxRoundResponseMb = _maxRoundResponseMb,
            RetentionDays = _retentionDays,
            MaxFailuresPerDay = _maxFailuresPerDay
        };
    }

    public DiagnosticConfigDto UpdateConfig(DiagnosticConfigDto config)
    {
        if (config is null) return GetConfig();

        _maxBodyLengthMb = Math.Clamp(config.MaxBodyLengthMb, 1, 50);
        _maxRoundResponseMb = Math.Clamp(config.MaxRoundResponseMb, 1, 20);
        _retentionDays = Math.Clamp(config.RetentionDays, 1, 30);
        _maxFailuresPerDay = Math.Clamp(config.MaxFailuresPerDay, 10, 500);

        _logger.LogInformation("诊断参数已动态更新: MaxBodyLength={MaxBody}MB, MaxRoundResponse={MaxRound}MB, RetentionDays={Days}, MaxFailuresPerDay={MaxFailures}",
            _maxBodyLengthMb, _maxRoundResponseMb, _retentionDays, _maxFailuresPerDay);

        return GetConfig();
    }

    public void RecordDiagnostic(ProxyDiagnosticContext context)
    {
        if (context.Result is null) return;

        // 周期性检查并清理过期文件（每小时执行一次）
        EnsurePeriodicPrune();

        var isFailure = !context.Result.Success
                        || context.Result.IsStreamInterrupted
                        || context.Result.StatusCode >= 400;

        if (isFailure)
        {
            HandleFailedRequest(context);
        }
        else
        {
            HandleSuccessfulRequest(context);
        }
    }

    public IReadOnlyList<ProxyDiagnosticDumpItem> ListRecentDumps(int limit = 50)
    {
        lock (_dumpLock)
        {
            return _recentDumps
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Max(1, limit))
                .ToList();
        }
    }

    public string? ReadDumpContent(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains('*') ||
            fileName.Contains('?'))
        {
            return null;
        }

        var baseDir = Path.Combine(AppContext.BaseDirectory, "logs");
        if (!Directory.Exists(baseDir)) return null;

        var matchingFiles = Directory.GetFiles(baseDir, fileName, SearchOption.AllDirectories);
        if (matchingFiles.Length == 0) return null;

        try
        {
            return File.ReadAllText(matchingFiles[0], Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取诊断转储文件失败: {FileName}", fileName);
            return null;
        }
    }

    public DiagnosticSamplingStatus GetSuccessSamplingStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var isEnabled = _successSamplingExpiresAtUtc.HasValue && now < _successSamplingExpiresAtUtc.Value;
        var remaining = isEnabled
            ? (int)Math.Max(0, Math.Ceiling((_successSamplingExpiresAtUtc!.Value - now).TotalSeconds))
            : 0;

        return new DiagnosticSamplingStatus
        {
            Enabled = isEnabled,
            RemainingSeconds = remaining,
            ExpiresAtUtc = isEnabled ? _successSamplingExpiresAtUtc : null,
            MaxDurationMinutes = MaxSamplingMinutes
        };
    }

    public DiagnosticSamplingStatus EnableSuccessSampling(int durationMinutes = 10)
    {
        _successCounterByRoute.Clear();
        var clampedMinutes = Math.Clamp(durationMinutes, 1, MaxSamplingMinutes);
        _successSamplingExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(clampedMinutes);
        _logger.LogInformation("成功请求诊断采样已开启，有效时长 {Minutes} 分钟，过期时间: {ExpiresAt}", clampedMinutes, _successSamplingExpiresAtUtc);
        return GetSuccessSamplingStatus();
    }

    public DiagnosticSamplingStatus DisableSuccessSampling()
    {
        _successSamplingExpiresAtUtc = null;
        _successCounterByRoute.Clear();
        _logger.LogInformation("已手动关闭成功请求诊断采样");
        return GetSuccessSamplingStatus();
    }

    public int PruneOldDumps(int? retentionDays = null)
    {
        var days = retentionDays ?? _retentionDays;
        var deletedCount = 0;
        try
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            if (!Directory.Exists(logsDir)) return 0;

            var cutoffDate = DateTime.Today.AddDays(-Math.Max(1, days));

            foreach (var dir in Directory.GetDirectories(logsDir))
            {
                var dirName = Path.GetFileName(dir);
                if (DateTime.TryParseExact(dirName, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var dirDate))
                {
                    if (dirDate < cutoffDate)
                    {
                        // 超过保留天数，删除整天目录下的 failures 和 samples
                        var failuresPath = Path.Combine(dir, "failures");
                        if (Directory.Exists(failuresPath))
                        {
                            var files = Directory.GetFiles(failuresPath, "*.json");
                            deletedCount += files.Length;
                            Directory.Delete(failuresPath, true);
                        }

                        var samplesPath = Path.Combine(dir, "samples");
                        if (Directory.Exists(samplesPath))
                        {
                            var files = Directory.GetFiles(samplesPath, "*.json");
                            deletedCount += files.Length;
                            Directory.Delete(samplesPath, true);
                        }

                        // 若目录已为空，移除该目录
                        if (Directory.GetFileSystemEntries(dir).Length == 0)
                        {
                            Directory.Delete(dir, false);
                        }
                    }
                    else
                    {
                        // 保留期内的文件夹，执行数量上限修剪
                        var failuresPath = Path.Combine(dir, "failures");
                        if (Directory.Exists(failuresPath))
                        {
                            deletedCount += PruneOldFiles(failuresPath, _maxFailuresPerDay);
                        }

                        var samplesPath = Path.Combine(dir, "samples");
                        if (Directory.Exists(samplesPath))
                        {
                            deletedCount += PruneOldFiles(samplesPath, MaxSamplesPerDayDirectory);
                        }
                    }
                }
            }

            if (deletedCount > 0)
            {
                _logger.LogInformation("历史抓包诊断清理完成，已清理 {Count} 个过期转储文件", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理过期诊断转储文件异常");
        }

        return deletedCount;
    }

    public int ClearAllDumps()
    {
        var deletedCount = 0;
        try
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            if (!Directory.Exists(logsDir)) return 0;

            foreach (var dir in Directory.GetDirectories(logsDir))
            {
                var failuresPath = Path.Combine(dir, "failures");
                if (Directory.Exists(failuresPath))
                {
                    var files = Directory.GetFiles(failuresPath, "*.json");
                    deletedCount += files.Length;
                    Directory.Delete(failuresPath, true);
                }

                var samplesPath = Path.Combine(dir, "samples");
                if (Directory.Exists(samplesPath))
                {
                    var files = Directory.GetFiles(samplesPath, "*.json");
                    deletedCount += files.Length;
                    Directory.Delete(samplesPath, true);
                }
            }

            lock (_dumpLock)
            {
                _recentDumps.Clear();
            }

            _logger.LogInformation("已清空所有诊断抓包和样本文件，共 {Count} 个", deletedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清空诊断转储文件异常");
        }

        return deletedCount;
    }

    private void EnsurePeriodicPrune()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastGlobalPruneAt < TimeSpan.FromHours(1))
        {
            return;
        }

        _lastGlobalPruneAt = now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                PruneOldDumps(_retentionDays);
            }
            catch
            {
            }
        });
    }

    private void HandleFailedRequest(ProxyDiagnosticContext context)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
        var baseDir = Path.Combine(AppContext.BaseDirectory, "logs", dateFolder, "failures");
        var safeRoute = SanitizeForFileName(string.IsNullOrWhiteSpace(context.RouteName) ? context.RequestModel : context.RouteName);
        var safeModel = SanitizeForFileName(context.AttemptedModel);
        var fileName = $"fail_{DateTime.Now:yyyyMMdd_HHmmssfff}_{safeRoute}_{safeModel}_{context.RequestId:N}.json";
        var fullPath = Path.Combine(baseDir, fileName);

        var modeLabel = string.Equals(context.ForwardingMode, "direct", StringComparison.OrdinalIgnoreCase)
            ? "直接透传 (direct)"
            : "兼容转换 (bridge)";

        // 转储构建/写盘整体在线程池执行：开发者功能未开启时跳过（与查看端点的门控对齐，生产不落盘）。
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await IsDumpEnabledAsync())
                {
                    return;
                }

                var dumpPayload = BuildDumpPayload(context, "failed", timestamp, modeLabel);
                Directory.CreateDirectory(baseDir);
                var jsonStr = dumpPayload.ToJsonString(JsonSerializerPresets.WriteIndented);
                File.WriteAllText(fullPath, jsonStr, Encoding.UTF8);

                PruneOldFiles(baseDir, _maxFailuresPerDay);

                var dumpItem = new ProxyDiagnosticDumpItem
                {
                    FileName = fileName,
                    FilePath = fullPath,
                    Category = "failure",
                    Timestamp = timestamp,
                    RouteName = context.RouteName,
                    SiteName = context.TargetSiteName,
                    RequestModel = context.RequestModel,
                    AttemptedModel = context.AttemptedModel,
                    ClientProtocol = context.ClientProtocol,
                    UpstreamProtocol = context.UpstreamProtocol,
                    ForwardingMode = context.ForwardingMode,
                    StatusCode = context.Result.StatusCode,
                    Success = false,
                    TotalDurationMs = context.Result.TotalDurationMs,
                    ErrorSummary = context.Result.ErrorMessage ?? string.Empty,
                    FileSizeBytes = jsonStr.Length
                };

                AddRecentDumpInMemory(dumpItem);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入失败诊断转储文件失败: {FilePath}", fullPath);
            }
        });

        _logger.LogError(
            "代理请求失败 [诊断复现文件已生成: {DumpFileName}]\n" +
            "DumpFile={DumpFilePath}\n" +
            "Route={RouteName}\n" +
            "SiteName={SiteName}\n" +
            "SiteId={SiteId}\n" +
            "BaseUrl={BaseUrl}\n" +
            "RequestModel={RequestModel}\n" +
            "AttemptedModel={AttemptedModel}\n" +
            "ClientProtocol={ClientProtocol}\n" +
            "UpstreamProtocol={UpstreamProtocol}\n" +
            "ForwardingMode={ForwardingMode}\n" +
            "StatusCode={StatusCode}\n" +
            "TotalDurationMs={TotalDurationMs}\n" +
            "IsStreaming={IsStreaming}\n" +
            "IsStreamInterrupted={IsStreamInterrupted}\n" +
            "ErrorMessage={ErrorMessage}\n" +
            "Source={Source}\n" +
            "ClientIp={ClientIp}\n" +
            "RequestBodyPreview={RequestBodyPreview}\n" +
            "ResponseBodyPreview={ResponseBodyPreview}",
            fileName,
            fullPath,
            context.RouteName,
            context.TargetSiteName,
            context.TargetSiteId,
            context.TargetBaseUrl,
            context.RequestModel,
            context.AttemptedModel,
            context.ClientProtocol,
            context.UpstreamProtocol,
            modeLabel,
            context.Result.StatusCode,
            context.Result.TotalDurationMs,
            context.Result.IsStreaming,
            context.Result.IsStreamInterrupted,
            context.Result.ErrorMessage ?? string.Empty,
            context.RequestSource,
            context.ClientIp,
            HttpLogFormatter.FormatBody(context.PreparedRequestBody),
            HttpLogFormatter.FormatBody(context.Result.ResponseBody));
    }

    /// <summary>
    /// 转储落盘开关：注入了元数据缓存时跟随开发者功能开关；未注入（单元测试）时恒为开。
    /// </summary>
    private async Task<bool> IsDumpEnabledAsync()
    {
        if (_metadataCache is null)
        {
            return true;
        }

        try
        {
            var runtime = await _metadataCache.GetRuntimeSettingsAsync(CancellationToken.None);
            return runtime.DeveloperFeaturesEnabled;
        }
        catch
        {
            // 开关读取失败时按关闭处理，避免异常放大。
            return false;
        }
    }

    private void HandleSuccessfulRequest(ProxyDiagnosticContext context)
    {
        // 成功请求默认不记录！只有在管理界面临时开启（最长 10 分钟）且未过期时才采样，防止硬盘爆满。
        var samplingStatus = GetSuccessSamplingStatus();
        if (!samplingStatus.Enabled)
        {
            return;
        }

        var routeKey = $"{context.RouteName}_{context.TargetSiteName}_{context.AttemptedModel}";
        var count = _successCounterByRoute.AddOrUpdate(routeKey, 1, (_, current) => current + 1);

        // 临时开启期间采样策略：前 3 次必采，之后每 5 次采样 1 次
        var shouldSample = count <= 3 || (count % 5 == 0);
        if (!shouldSample) return;

        var timestamp = DateTimeOffset.UtcNow;
        var dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
        var baseDir = Path.Combine(AppContext.BaseDirectory, "logs", dateFolder, "samples");
        var safeRoute = SanitizeForFileName(string.IsNullOrWhiteSpace(context.RouteName) ? context.RequestModel : context.RouteName);
        var safeModel = SanitizeForFileName(context.AttemptedModel);
        var fileName = $"sample_success_{DateTime.Now:yyyyMMdd_HHmmssfff}_{safeRoute}_{safeModel}_{context.RequestId:N}.json";
        var fullPath = Path.Combine(baseDir, fileName);

        var modeLabel = string.Equals(context.ForwardingMode, "direct", StringComparison.OrdinalIgnoreCase)
            ? "直接透传 (direct)"
            : "兼容转换 (bridge)";

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var dumpPayload = BuildDumpPayload(context, "success", timestamp, modeLabel);
                Directory.CreateDirectory(baseDir);
                var jsonStr = dumpPayload.ToJsonString(JsonSerializerPresets.WriteIndented);
                File.WriteAllText(fullPath, jsonStr, Encoding.UTF8);

                PruneOldFiles(baseDir, MaxSamplesPerDayDirectory);

                var dumpItem = new ProxyDiagnosticDumpItem
                {
                    FileName = fileName,
                    FilePath = fullPath,
                    Category = "sample",
                    Timestamp = timestamp,
                    RouteName = context.RouteName,
                    SiteName = context.TargetSiteName,
                    RequestModel = context.RequestModel,
                    AttemptedModel = context.AttemptedModel,
                    ClientProtocol = context.ClientProtocol,
                    UpstreamProtocol = context.UpstreamProtocol,
                    ForwardingMode = context.ForwardingMode,
                    StatusCode = context.Result.StatusCode,
                    Success = true,
                    TotalDurationMs = context.Result.TotalDurationMs,
                    ErrorSummary = string.Empty,
                    FileSizeBytes = jsonStr.Length
                };

                AddRecentDumpInMemory(dumpItem);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "写入成功对比样本转储文件失败: {FilePath}", fullPath);
            }
        });
    }

    private JsonObject BuildDumpPayload(
        ProxyDiagnosticContext context,
        string status,
        DateTimeOffset timestamp,
        string modeLabel)
    {
        var root = new JsonObject
        {
            ["status"] = status,
            ["timestamp"] = timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            ["diagnostic"] = new JsonObject
            {
                ["requestId"] = context.RequestId.ToString("N"),
                ["traceId"] = context.TraceId?.ToString("N") ?? string.Empty,
                ["source"] = context.RequestSource,
                ["clientIp"] = context.ClientIp,
                ["userAgent"] = context.UserAgent,
                ["requestPath"] = context.RequestPath,
                ["routeName"] = context.RouteName,
                ["siteId"] = context.TargetSiteId?.ToString() ?? string.Empty,
                ["siteName"] = context.TargetSiteName,
                ["baseUrl"] = context.TargetBaseUrl,
                ["requestModel"] = context.RequestModel,
                ["attemptedModel"] = context.AttemptedModel,
                ["clientProtocol"] = context.ClientProtocol,
                ["upstreamProtocol"] = context.UpstreamProtocol,
                ["forwardingMode"] = modeLabel,
                ["httpStatusCode"] = context.Result.StatusCode,
                ["isStreaming"] = context.Result.IsStreaming,
                ["isStreamInterrupted"] = context.Result.IsStreamInterrupted,
                ["totalDurationMs"] = context.Result.TotalDurationMs,
                ["firstTokenLatencyMs"] = context.Result.FirstTokenLatencyMs,
                ["inputTokens"] = context.Result.InputTokens,
                ["cachedTokens"] = context.Result.CachedTokens,
                ["outputTokens"] = context.Result.OutputTokens,
                ["errorMessage"] = context.Result.ErrorMessage ?? string.Empty
            }
        };

        if (context.ClientHeaders is not null)
        {
            var headersObj = new JsonObject();
            foreach (var h in context.ClientHeaders)
            {
                if (string.IsNullOrWhiteSpace(h.Key)) continue;
                var val = h.Value;
                if (string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(h.Key, "x-api-key", StringComparison.OrdinalIgnoreCase))
                {
                    val = MaskApiKey(val);
                }
                headersObj[h.Key] = val;
            }
            root["clientHeaders"] = headersObj;
        }

        root["clientRequestBody"] = TryParseJsonOrRaw(context.RawClientRequestBody);
        root["preparedRequestBody"] = TryParseJsonOrRaw(context.PreparedRequestBody);
        root["upstreamResponseBody"] = TryParseJsonOrRaw(context.Result.ResponseBody);

        root["reproduction"] = new JsonObject
        {
            ["description"] = "可直接使用此请求正文及配置向上游进行二次复现排查",
            ["targetBaseUrl"] = context.TargetBaseUrl,
            ["upstreamModel"] = context.AttemptedModel,
            ["upstreamProtocol"] = context.UpstreamProtocol
        };

        return root;
    }

    private JsonNode TryParseJsonOrRaw(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return JsonValue.Create(string.Empty)!;
        }

        var limitBytes = MaxBodyLengthBytes;
        if (body.Length > limitBytes)
        {
            var truncated = body[..limitBytes] + $"\n... [TRUNCATED DUE TO SIZE: Original length {body.Length} bytes, limit {limitBytes / (1024 * 1024)}MB]";
            return JsonValue.Create(truncated)!;
        }

        try
        {
            var parsed = JsonNode.Parse(body);
            if (parsed is not null) return parsed;
        }
        catch
        {
        }

        return JsonValue.Create(body)!;
    }

    private static string MaskApiKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = key[7..].Trim();
            return $"Bearer {MaskToken(token)}";
        }
        return MaskToken(key);
    }

    private static string MaskToken(string token)
    {
        if (token.Length <= 8) return "***";
        return $"{token[..Math.Min(6, token.Length)]}...{token[^Math.Min(4, token.Length)..]}";
    }

    private static string SanitizeForFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) || c is ':' or '/' or '\\' ? '_' : c);
        }
        return sb.ToString();
    }

    private void AddRecentDumpInMemory(ProxyDiagnosticDumpItem item)
    {
        lock (_dumpLock)
        {
            _recentDumps.AddFirst(item);
            while (_recentDumps.Count > MaxRecentDumpsInMemory)
            {
                _recentDumps.RemoveLast();
            }
        }
    }

    private static int PruneOldFiles(string directory, int maxFiles)
    {
        var deleted = 0;
        try
        {
            var dirInfo = new DirectoryInfo(directory);
            if (!dirInfo.Exists) return 0;

            var files = dirInfo.GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(maxFiles)
                .ToList();

            foreach (var f in files)
            {
                try
                {
                    f.Delete();
                    deleted++;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
        return deleted;
    }
}
