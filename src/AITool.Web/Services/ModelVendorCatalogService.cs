using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AITool.Web.Services;

public sealed class ModelVendorCatalog
{
    public List<ModelVendorDefinition> Vendors { get; set; } = [];
    public List<ModelVendorRuleDefinition> Rules { get; set; } = [];
}

public sealed class ModelVendorDefinition
{
    public string VendorName { get; set; } = string.Empty;
    public string IconSvgBody { get; set; } = string.Empty;
    public string HeaderBackground { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public sealed class ModelVendorRuleDefinition
{
    public string VendorName { get; set; } = string.Empty;
    public string MatchType { get; set; } = "wildcard";
    public string Pattern { get; set; } = string.Empty;
    public int Priority { get; set; }
}

// 模型厂商分组配置服务，统一从独立 JSON 文件读取并保存厂商定义与匹配规则。
public sealed class ModelVendorCatalogService
{
    private const string CatalogFileName = "model-vendor-catalog.json";
    private const string UncategorizedVendorName = "未分类";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _catalogPath;
    private readonly string _templateCatalogPath;

    public ModelVendorCatalogService(IWebHostEnvironment environment)
    {
        // 厂商配置以软件运行目录中的文件为准，源码目录中的文件只作为首次生成模板。
        _catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CatalogFileName);
        _templateCatalogPath = Path.Combine(environment.ContentRootPath, CatalogFileName);
    }

    public async Task<ModelVendorCatalog> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_catalogPath))
        {
            var initializedCatalog = await InitializeCatalogAsync(cancellationToken);
            await WriteCatalogAsync(initializedCatalog, cancellationToken);
            return initializedCatalog;
        }

        var json = await File.ReadAllTextAsync(_catalogPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            var initializedCatalog = await InitializeCatalogAsync(cancellationToken);
            await WriteCatalogAsync(initializedCatalog, cancellationToken);
            return initializedCatalog;
        }

        var catalog = JsonSerializer.Deserialize<ModelVendorCatalog>(json, JsonOptions) ?? new ModelVendorCatalog();
        return NormalizeCatalog(catalog);
    }

    // 运行目录缺少配置时，优先用源码目录模板初始化；模板不存在时再退回空配置。
    private async Task<ModelVendorCatalog> InitializeCatalogAsync(CancellationToken cancellationToken)
    {
        var runtimePath = Path.GetFullPath(_catalogPath);
        var templatePath = Path.GetFullPath(_templateCatalogPath);
        if (!string.Equals(runtimePath, templatePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(_templateCatalogPath))
        {
            var templateJson = await File.ReadAllTextAsync(_templateCatalogPath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(templateJson))
            {
                var templateCatalog = JsonSerializer.Deserialize<ModelVendorCatalog>(templateJson, JsonOptions) ?? new ModelVendorCatalog();
                return NormalizeCatalog(templateCatalog);
            }
        }

        return NormalizeCatalog(new ModelVendorCatalog());
    }

    public async Task<ModelVendorCatalog> SaveAsync(ModelVendorCatalog catalog, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCatalog(catalog, validateRuleReferences: true);
        await WriteCatalogAsync(normalized, cancellationToken);
        return normalized;
    }

    // 规则按优先级顺序匹配，命中后返回对应厂商；未命中则归到未分类。
    public static ModelVendorDefinition ResolveVendor(ModelVendorCatalog catalog, string modelName)
    {
        var normalizedName = modelName?.Trim() ?? string.Empty;
        var vendors = catalog.Vendors
            .ToDictionary(x => x.VendorName, x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var rule in catalog.Rules.OrderBy(x => x.Priority).ThenBy(x => x.Pattern, StringComparer.OrdinalIgnoreCase))
        {
            if (!vendors.ContainsKey(rule.VendorName))
            {
                continue;
            }

            if (IsMatch(normalizedName, rule))
            {
                return vendors[rule.VendorName];
            }
        }

        return vendors.TryGetValue(UncategorizedVendorName, out var uncategorized)
            ? uncategorized
            : CreateFallbackVendor();
    }

    private static ModelVendorCatalog NormalizeCatalog(ModelVendorCatalog catalog, bool validateRuleReferences = false)
    {
        catalog ??= new ModelVendorCatalog();
        catalog.Vendors ??= [];
        catalog.Rules ??= [];

        var normalizedVendors = catalog.Vendors
            .Where(x => !string.IsNullOrWhiteSpace(x.VendorName))
            .GroupBy(x => x.VendorName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new ModelVendorDefinition
                {
                    VendorName = g.Key,
                    IconSvgBody = first.IconSvgBody?.Trim() ?? string.Empty,
                    HeaderBackground = string.IsNullOrWhiteSpace(first.HeaderBackground) ? "#f8fafc" : first.HeaderBackground.Trim(),
                    SortOrder = first.SortOrder
                };
            })
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.VendorName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedVendors.All(x => !string.Equals(x.VendorName, UncategorizedVendorName, StringComparison.OrdinalIgnoreCase)))
        {
            normalizedVendors.Add(CreateFallbackVendor());
        }

        var vendorNames = normalizedVendors
            .Select(x => x.VendorName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalizedRules = catalog.Rules
            .Where(x => !string.IsNullOrWhiteSpace(x.Pattern))
            .Select(x => new ModelVendorRuleDefinition
            {
                VendorName = x.VendorName?.Trim() ?? string.Empty,
                MatchType = NormalizeMatchType(x.MatchType),
                Pattern = x.Pattern.Trim(),
                Priority = x.Priority
            })
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Pattern, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validateRuleReferences)
        {
            var invalidVendor = normalizedRules.FirstOrDefault(x => !vendorNames.Contains(x.VendorName));
            if (invalidVendor is not null)
            {
                throw new InvalidOperationException($"匹配规则引用了不存在的厂商：{invalidVendor.VendorName}");
            }

            foreach (var rule in normalizedRules.Where(x => string.Equals(x.MatchType, "regex", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var pattern in SplitPatterns(rule.Pattern))
                {
                    try
                    {
                        _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"正则规则无效：{pattern}，原因：{ex.Message}");
                    }
                }
            }
        }

        return new ModelVendorCatalog
        {
            Vendors = normalizedVendors,
            Rules = normalizedRules
        };
    }

    private static ModelVendorDefinition CreateFallbackVendor()
    {
        return new ModelVendorDefinition
        {
            VendorName = UncategorizedVendorName,
            HeaderBackground = "#f8fafc",
            SortOrder = 999
        };
    }

    private static bool IsMatch(string modelName, ModelVendorRuleDefinition rule)
    {
        if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return false;
        }

        foreach (var pattern in SplitPatterns(rule.Pattern))
        {
            var matched = NormalizeMatchType(rule.MatchType) switch
            {
                "exact" => string.Equals(modelName, pattern, StringComparison.OrdinalIgnoreCase),
                "regex" => Regex.IsMatch(modelName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                _ => Regex.IsMatch(modelName, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            };

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    // 单条规则支持用半角逗号或中文逗号同时维护多个匹配表达式。
    private static IEnumerable<string> SplitPatterns(string pattern)
    {
        return pattern
            .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    private static string NormalizeMatchType(string? matchType)
    {
        return matchType?.Trim().ToLowerInvariant() switch
        {
            "exact" => "exact",
            "regex" => "regex",
            _ => "wildcard"
        };
    }

    private async Task WriteCatalogAsync(ModelVendorCatalog catalog, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(catalog, JsonOptions);
        await File.WriteAllTextAsync(_catalogPath, json, Encoding.UTF8, cancellationToken);
    }
}
