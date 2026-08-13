using System.Net.Http.Headers;
using System.Text.Json;
using AITool.Application.Codex;
using Microsoft.Extensions.Options;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// 动态拉取 Codex 上游模型目录。请求格式移植自 CPA
/// （reference-projects/CLIProxyAPI/cmd/fetch_codex_models/main.go:231-295）。
/// </summary>
public sealed class CodexModelFetcher : ICodexModelFetcher
{
    private const string ModelsBaseUrl = "https://chatgpt.com/backend-api/codex/models";
    private const string UserAgentSuffix = " (Mac OS 26.3.1; arm64) iTerm.app/3.6.9";

    private readonly HttpClient _httpClient;
    private readonly string _clientVersion;
    private readonly string _userAgent;

    public CodexModelFetcher(HttpClient httpClient, IOptions<CodexUpstreamOptions> options)
    {
        _httpClient = httpClient;
        _clientVersion = options?.Value?.ClientVersion ?? "0.133.0";
        _userAgent = $"codex_cli_rs/{_clientVersion}{UserAgentSuffix}";
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodexRemoteModel>> FetchAsync(string accessToken, string accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ModelsBaseUrl}?client_version={_clientVersion}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Originator", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        if (!string.IsNullOrEmpty(accountId))
        {
            request.Headers.TryAddWithoutValidation("Chatgpt-Account-Id", accountId);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        // 上游响应顶层可能是数组，也可能是 { models: [...] } 或 { data: [...] }，逐种尝试
        JsonElement arrayEl;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            arrayEl = doc.RootElement;
        }
        else if (doc.RootElement.TryGetProperty("models", out var m1) && m1.ValueKind == JsonValueKind.Array)
        {
            arrayEl = m1;
        }
        else if (doc.RootElement.TryGetProperty("data", out var m2) && m2.ValueKind == JsonValueKind.Array)
        {
            arrayEl = m2;
        }
        else
        {
            return [];
        }

        var list = new List<CodexRemoteModel>();
        foreach (var item in arrayEl.EnumerateArray())
        {
            // Codex 当前通常返回 slug/display_name；兼容部分版本返回的 id/name 字段，
            // 避免上游模型目录字段变更后整批模型被静默过滤。
            var slug = ReadString(item, "slug")
                ?? ReadString(item, "id")
                ?? ReadString(item, "model")
                ?? ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(slug)) continue;

            var display = ReadString(item, "display_name")
                ?? ReadString(item, "displayName")
                ?? ReadString(item, "name")
                ?? slug;

            list.Add(new CodexRemoteModel { Slug = slug, DisplayName = display });
        }
        return list;
    }

    private static string? ReadString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
