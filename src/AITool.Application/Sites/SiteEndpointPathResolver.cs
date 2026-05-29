namespace AITool.Application.Sites;

/// <summary>
/// 根据站点接口路径模式生成上游接口路径。
/// </summary>
public static class SiteEndpointPathResolver
{
    public const string StandardRoot = "standard-root";
    public const string VersionedBase = "versioned-base";

    public static string NormalizeMode(string? mode)
    {
        return string.Equals(mode, VersionedBase, StringComparison.OrdinalIgnoreCase)
            ? VersionedBase
            : StandardRoot;
    }

    public static string ResolvePath(string? mode, string endpoint)
    {
        var normalizedEndpoint = endpoint.Trim('/');
        if (NormalizeMode(mode) == VersionedBase)
        {
            return $"/{normalizedEndpoint}";
        }

        return $"/v1/{normalizedEndpoint}";
    }

    public static string BuildUrl(string baseUrl, string? mode, string endpoint)
    {
        return $"{baseUrl.TrimEnd('/')}/{ResolvePath(mode, endpoint).TrimStart('/')}";
    }
}
