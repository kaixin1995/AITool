using AITool.Admin.Services;
using FluentAssertions;

namespace AITool.ApplicationTests.Hosting;

/// <summary>
/// 验证模型厂商目录服务在迁入共享宿主层后仍保持既有解析与兜底行为。
/// </summary>
public sealed class ModelVendorCatalogServiceTests
{
    /// <summary>
    /// 当规则命中时，应返回对应厂商定义。
    /// </summary>
    [Fact]
    public void Resolve_vendor_returns_matched_vendor_definition()
    {
        var catalog = new ModelVendorCatalog
        {
            Vendors =
            [
                new ModelVendorDefinition
                {
                    VendorName = "OpenAI",
                    HeaderBackground = "#eef6ff",
                    SortOrder = 0
                },
                new ModelVendorDefinition
                {
                    VendorName = "未分类",
                    HeaderBackground = "#f8fafc",
                    SortOrder = int.MaxValue
                }
            ],
            Rules =
            [
                new ModelVendorRuleDefinition
                {
                    VendorName = "OpenAI",
                    MatchType = "wildcard",
                    Pattern = "gpt-*",
                    Priority = 0
                }
            ]
        };

        var vendor = ModelVendorCatalogService.ResolveVendor(catalog, "gpt-5.4");

        vendor.VendorName.Should().Be("OpenAI");
        vendor.HeaderBackground.Should().Be("#eef6ff");
    }

    /// <summary>
    /// 当没有规则命中时，应回退到未分类厂商定义。
    /// </summary>
    [Fact]
    public void Resolve_vendor_falls_back_to_uncategorized_when_no_rule_matches()
    {
        var catalog = new ModelVendorCatalog
        {
            Vendors =
            [
                new ModelVendorDefinition
                {
                    VendorName = "OpenAI",
                    HeaderBackground = "#eef6ff",
                    SortOrder = 0
                },
                new ModelVendorDefinition
                {
                    VendorName = "未分类",
                    HeaderBackground = "#f8fafc",
                    SortOrder = int.MaxValue
                }
            ]
        };

        var vendor = ModelVendorCatalogService.ResolveVendor(catalog, "custom-model-x");

        vendor.VendorName.Should().Be("未分类");
        vendor.HeaderBackground.Should().Be("#f8fafc");
    }
}
