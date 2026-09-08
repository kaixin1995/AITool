using System.Text.Json;
using AITool.Application.Pricing;

namespace AITool.Web.Services;

/// <summary>
/// 公开模型价格源查询服务：从 models.dev 与 LiteLLM 的公开 JSON 拉取全量价格表，
/// 本地匹配模型 ID。一次查询零 AI 调用。
/// <para>
/// 两个源的单位不同（models.dev 已是 USD/百万 tokens；LiteLLM 是 USD/单 token），解析时统一换算。
/// 内存策略（手动低频按钮，内存优先）：<strong>完全无状态、零常驻</strong>——每次查询现拉现解析，
/// 原始响应体与解析后的索引都是请求内局部对象，请求结束即整条引用链交给 GC 回收，
/// 进程内不留任何缓存（单源失败容忍另一源）。
/// </para>
/// </summary>
public sealed class ModelPriceSourceService
{
    private const string ModelsDevUrl = "https://models.dev/api.json";
    private const string LiteLlmUrl = "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

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

    /// <summary>按模型 ID 清单匹配公开价格源。每次现拉现解析，无缓存；单源失败不影响另一源。</summary>
    public async Task<SourceLookupResult> LookupAsync(IReadOnlyCollection<string> modelIds, CancellationToken cancellationToken)
    {
        var (index, sourceNames) = await BuildIndexAsync(cancellationToken);
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

        return new SourceLookupResult(true, null, sourceNames, matched, unmatched);
    }

    /// <summary>拉取并解析两个价格源为本次请求专用的匹配索引（局部对象，不落任何静态字段）。</summary>
    private async Task<(IReadOnlyDictionary<string, List<ModelPriceSourceEntry>> Index, IReadOnlyList<string> SourceNames)> BuildIndexAsync(CancellationToken cancellationToken)
    {
        var sourceNames = new List<string>();
        var index = new Dictionary<string, List<ModelPriceSourceEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, url) in new[] { ("models.dev", ModelsDevUrl), ("LiteLLM", LiteLlmUrl) })
        {
            try
            {
                var client = _httpClientFactory.CreateClient("ModelPriceSource");
                using var response = await client.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                // 响应体只作局部变量：解析完成后出作用域即可被 GC 回收，不进程级常驻。
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
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
                if (entries.Any())
                {
                    sourceNames.Add(name);
                }
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

        return (index, sourceNames);
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
