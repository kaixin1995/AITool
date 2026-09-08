using System.Collections.Concurrent;
using System.Text.Json;
using AITool.Application.Pricing;

namespace AITool.Web.Services;

/// <summary>
/// 公开模型价格源查询服务：从 models.dev 与 LiteLLM 的公开 JSON 拉取全量价格表，
/// 本地匹配模型 ID。一次查询零 AI 调用、秒级返回。
/// <para>
/// 两个源的单位不同（models.dev 已是 USD/百万 tokens；LiteLLM 是 USD/单 token），解析时统一换算。
/// 原始响应进程级缓存 6 小时，解析后的索引在缓存体未变化时复用。
/// </para>
/// </summary>
public sealed class ModelPriceSourceService
{
    private const string ModelsDevUrl = "https://models.dev/api.json";
    private const string LiteLlmUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    /// <summary>进程级原始响应缓存。static：服务为单例，但与集成测试多宿主共享目录时也保持一致语义。</summary>
    private static readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, string Body)> BodyCache = new();

    private static readonly object IndexLock = new();
    private static IReadOnlyDictionary<string, List<ModelPriceSourceEntry>>? _index;
    private static DateTimeOffset _indexBuiltAt;
    private static string _indexSourceNames = string.Empty;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelPriceSourceService> _logger;

    public ModelPriceSourceService(IHttpClientFactory httpClientFactory, ILogger<ModelPriceSourceService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>一次查询的结果。Success=false 表示两个源均不可达。</summary>
    public sealed record SourceLookupResult(
        bool Success,
        string? Error,
        IReadOnlyList<string> Sources,
        IReadOnlyDictionary<string, ModelPriceSourceEntry> Matched,
        IReadOnlyList<string> Unmatched);

    /// <summary>按模型 ID 清单匹配公开价格源。单源失败不影响另一源；双源均失败才返回失败。</summary>
    public async Task<SourceLookupResult> LookupAsync(IReadOnlyCollection<string> modelIds, CancellationToken cancellationToken)
    {
        var index = await GetIndexAsync(cancellationToken);
        if (index.Count == 0)
        {
            return new SourceLookupResult(false, "公开价格源不可达（models.dev 与 LiteLLM 均拉取失败），请稍后重试或改用 AI 查询", [], new Dictionary<string, ModelPriceSourceEntry>(), []);
        }

        var matched = new Dictionary<string, ModelPriceSourceEntry>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<string>();
        foreach (var id in modelIds)
        {
            if (ModelPriceSourceMatcher.TryMatch(id, index, out var entry, out _)
                && entry is not null)
            {
                matched[id] = entry;
            }
            else
            {
                unmatched.Add(id);
            }
        }

        return new SourceLookupResult(true, null, LoadedSourceNames(), matched, unmatched);
    }

    private async Task<IReadOnlyDictionary<string, List<ModelPriceSourceEntry>>> GetIndexAsync(CancellationToken cancellationToken)
    {
        var bodies = new List<(string Name, string Body)>();
        var refreshed = false;

        foreach (var (name, url) in new[] { ("models.dev", ModelsDevUrl), ("LiteLLM", LiteLlmUrl) })
        {
            if (BodyCache.TryGetValue(url, out var cached)
                && DateTimeOffset.UtcNow - cached.FetchedAt < CacheTtl)
            {
                bodies.Add((name, cached.Body));
                continue;
            }

            try
            {
                var client = _httpClientFactory.CreateClient("ModelPriceSource");
                using var response = await client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                BodyCache[url] = (DateTimeOffset.UtcNow, body);
                bodies.Add((name, body));
                refreshed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 单源失败容忍：models.dev 不可达时 LiteLLM 仍可用，反之亦然。
                _logger.LogWarning(ex, "Fetch model price source failed: {Source}", name);
            }
        }

        lock (IndexLock)
        {
            if (_index is not null && !refreshed && _indexSourceNames == SourceKey(bodies))
            {
                return _index;
            }

            var index = new Dictionary<string, List<ModelPriceSourceEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, body) in bodies)
            {
                try
                {
                    var entries = name == "models.dev"
                        ? ParseModelsDev(body)
                        : ParseLiteLlm(body);
                    foreach (var (key, entry) in entries)
                    {
                        if (!index.TryGetValue(key, out var list))
                        {
                            list = [];
                            index[key] = list;
                        }
                        list.Add(entry);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Parse model price source failed: {Source}", name);
                }
            }

            _index = index;
            _indexBuiltAt = DateTimeOffset.UtcNow;
            _indexSourceNames = SourceKey(bodies);
            return index;
        }
    }

    private static string SourceKey(List<(string Name, string Body)> bodies)
        => string.Join("|", bodies.Select(b => b.Name));

    private static IReadOnlyList<string> LoadedSourceNames()
    {
        lock (IndexLock)
        {
            return _indexSourceNames.Split('|', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    /// <summary>解析 models.dev：按厂商分组，cost 单位已是 USD/百万 tokens，直接取用。</summary>
    private static IEnumerable<KeyValuePair<string, ModelPriceSourceEntry>> ParseModelsDev(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;

        foreach (var providerProp in root.EnumerateObject())
        {
            if (providerProp.Value.ValueKind != JsonValueKind.Object
                || !providerProp.Value.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var provider = providerProp.Name.ToLowerInvariant();
            foreach (var modelProp in models.EnumerateObject())
            {
                if (modelProp.Value.ValueKind != JsonValueKind.Object
                    || !modelProp.Value.TryGetProperty("cost", out var cost)
                    || cost.ValueKind != JsonValueKind.Object
                    || !TryGetDecimal(cost, "input", out var input)
                    || !TryGetDecimal(cost, "output", out var output))
                {
                    continue;
                }

                TryGetDecimal(cost, "cache_read", out var cacheRead);
                TryGetDecimal(cost, "cache_write", out var cacheWrite);
                var key = modelProp.Name.ToLowerInvariant();
                yield return KeyValuePair.Create(
                    $"{provider}/{key}",
                    new ModelPriceSourceEntry(key, provider, input, output, cacheRead, cacheWrite));
                // 裸 ID 也入索引；撞名由匹配器的厂商优先级裁决。
                yield return KeyValuePair.Create(
                    key,
                    new ModelPriceSourceEntry(key, provider, input, output, cacheRead, cacheWrite));
            }
        }
    }

    /// <summary>解析 LiteLLM：input_cost_per_token 等单位是 USD/单 token，统一乘 100 万换算为 USD/百万 tokens。</summary>
    private static IEnumerable<KeyValuePair<string, ModelPriceSourceEntry>> ParseLiteLlm(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;

        const decimal PerMillion = 1_000_000m;
        foreach (var modelProp in root.EnumerateObject())
        {
            if (modelProp.Value.ValueKind != JsonValueKind.Object
                || !TryGetDecimal(modelProp.Value, "input_cost_per_token", out var input)
                || !TryGetDecimal(modelProp.Value, "output_cost_per_token", out var output))
            {
                continue;
            }

            TryGetDecimal(modelProp.Value, "cache_read_input_token_cost", out var cacheRead);
            TryGetDecimal(modelProp.Value, "cache_creation_input_token_cost", out var cacheWrite);

            var provider = modelProp.Value.TryGetProperty("litellm_provider", out var provEl)
                && provEl.ValueKind == JsonValueKind.String
                ? provEl.GetString()?.ToLowerInvariant() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrEmpty(provider) && modelProp.Name.Contains('/'))
            {
                provider = modelProp.Name[..modelProp.Name.IndexOf('/')].ToLowerInvariant();
            }

            var key = modelProp.Name.ToLowerInvariant();
            yield return KeyValuePair.Create(
                key,
                new ModelPriceSourceEntry(key, provider, input * PerMillion, output * PerMillion, cacheRead * PerMillion, cacheWrite * PerMillion));
        }
    }

    private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out value))
            {
                return true;
            }
            // 字符串数字（LiteLLM 部分条目用字符串表达价格）。
            if (el.ValueKind == JsonValueKind.String
                && decimal.TryParse(el.GetString(), out value))
            {
                return true;
            }
        }
        return false;
    }
}
