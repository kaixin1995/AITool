using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AITool.Web.Services;

/// <summary>
/// 模型厂商目录。
/// </summary>
public sealed class ModelVendorCatalog
{
    /// <summary>
    /// Vendors。
    /// </summary>
    public List<ModelVendorDefinition> Vendors { get; set; } = [];
    /// <summary>
    /// Rules。
    /// </summary>
    public List<ModelVendorRuleDefinition> Rules { get; set; } = [];
}

/// <summary>
/// 模型厂商定义。
/// </summary>
public sealed class ModelVendorDefinition
{
    /// <summary>
    /// 厂商名称。
    /// </summary>
    public string VendorName { get; set; } = string.Empty;
    /// <summary>
    /// 图标 SVG 内容。
    /// </summary>
    public string IconSvgBody { get; set; } = string.Empty;
    /// <summary>
    /// 头部背景色。
    /// </summary>
    public string HeaderBackground { get; set; } = string.Empty;
    /// <summary>
    /// 排序顺序。
    /// </summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// 模型厂商匹配规则。
/// </summary>
public sealed class ModelVendorRuleDefinition
{
    /// <summary>
    /// 厂商名称。
    /// </summary>
    public string VendorName { get; set; } = string.Empty;
    /// <summary>
    /// 匹配类型。
    /// </summary>
    public string MatchType { get; set; } = "wildcard";
    /// <summary>
    /// 匹配模式。
    /// </summary>
    public string Pattern { get; set; } = string.Empty;
    /// <summary>
    /// 优先级。
    /// </summary>
    public int Priority { get; set; }
}

/// <summary>
/// 模型厂商目录服务。
/// </summary>
public sealed class ModelVendorCatalogService
{
    /// <summary>
    /// 厂商目录文件名。
    /// </summary>
    private const string CatalogFileName = "model-vendor-catalog.json";
    /// <summary>
    /// 未分类厂商名称。
    /// </summary>
    private const string UncategorizedVendorName = "未分类";
    /// <summary>
    /// new。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 运行时厂商目录文件路径。
    /// </summary>
    private readonly string _catalogPath;
    /// <summary>
    /// 模板厂商目录文件路径。
    /// </summary>
    private readonly string _templateCatalogPath;

    /// <summary>
    /// 初始化模型厂商目录服务。
    /// </summary>
    public ModelVendorCatalogService(IWebHostEnvironment environment)
    {
        // 厂商配置以软件运行目录中的文件为准，源码目录中的文件只作为首次生成模板。
        _catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CatalogFileName);
        _templateCatalogPath = Path.Combine(environment.ContentRootPath, CatalogFileName);
    }

    /// <summary>
    /// 获取厂商目录，不存在时自动初始化。
    /// </summary>
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

    /// <summary>
    /// 初始化厂商目录。
    /// </summary>
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

    /// <summary>
    /// 保存厂商目录。
    /// </summary>
    public async Task<ModelVendorCatalog> SaveAsync(ModelVendorCatalog catalog, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeCatalog(catalog, validateRuleReferences: true);
        await WriteCatalogAsync(normalized, cancellationToken);
        return normalized;
    }

    /// <summary>
    /// 根据模型名称解析厂商。
    /// </summary>
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

    /// <summary>
    /// 规范化厂商目录数据。
    /// </summary>
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
                    IconSvgBody = NormalizeIconSvgBody(first.IconSvgBody),
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

    /// <summary>
    /// 创建未分类厂商。
    /// </summary>
    private static ModelVendorDefinition CreateFallbackVendor()
    {
        return new ModelVendorDefinition
        {
            VendorName = UncategorizedVendorName,
            HeaderBackground = "#f8fafc",
            SortOrder = 999
        };
    }

    /// <summary>
    /// 判断模型名称是否命中规则。
    /// </summary>
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

    /// <summary>
    /// 拆分多条匹配模式。
    /// </summary>
    private static IEnumerable<string> SplitPatterns(string pattern)
    {
        return pattern
            .Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x));
    }

    /// <summary>
    /// 规范化图标 SVG 内容。
    /// </summary>
    private static string NormalizeIconSvgBody(string? iconSvgBody)
    {
        var normalized = iconSvgBody?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var svgMatch = Regex.Match(normalized, "^<svg\\b(?<attrs>[^>]*)>(?<body>[\\s\\S]*)</svg>$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!svgMatch.Success)
        {
            return normalized;
        }

        var body = svgMatch.Groups["body"].Value.Trim();
        var attrs = svgMatch.Groups["attrs"].Value;
        var viewBoxMatch = Regex.Match(attrs, "\\bviewBox\\s*=\\s*(['\"])(?<viewBox>.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!viewBoxMatch.Success)
        {
            return body;
        }

        var viewBox = viewBoxMatch.Groups["viewBox"].Value.Trim();
        return string.IsNullOrWhiteSpace(viewBox)
            ? body
            : $"<svg viewBox=\"{viewBox}\">{body}</svg>";
    }

    /// <summary>
    /// 规范化匹配类型。
    /// </summary>
    private static string NormalizeMatchType(string? matchType)
    {
        return matchType?.Trim().ToLowerInvariant() switch
        {
            "exact" => "exact",
            "regex" => "regex",
            _ => "wildcard"
        };
    }

    /// <summary>
    /// 写入厂商目录文件。
    /// </summary>
    private async Task WriteCatalogAsync(ModelVendorCatalog catalog, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(catalog, JsonOptions);
        await File.WriteAllTextAsync(_catalogPath, json, Encoding.UTF8, cancellationToken);
    }
}
