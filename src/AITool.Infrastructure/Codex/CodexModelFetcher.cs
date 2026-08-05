using System.Net.Http.Headers;
using System.Text.Json;
using AITool.Application.Codex;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// 动态拉取 Codex 上游模型目录。请求格式移植自 CPA
/// （reference-projects/CLIProxyAPI/cmd/fetch_codex_models/main.go:231-295）。
/// </summary>
public sealed class CodexModelFetcher : ICodexModelFetcher
{
    private const string ModelsUrl = "https://chatgpt.com/backend-api/codex/models?client_version=0.133.0";
    private const string UserAgent = "codex_cli_rs/0.133.0 (Mac OS 26.3.1; arm64) iTerm.app/3.6.9";

    private readonly HttpClient _httpClient;

    public CodexModelFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CodexRemoteModel>> FetchAsync(string accessToken, string accountId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Originator", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
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
            var slug = item.TryGetProperty("slug", out var slugEl) && slugEl.ValueKind == JsonValueKind.String
                ? slugEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(slug)) continue;

            var display = item.TryGetProperty("display_name", out var dnEl) && dnEl.ValueKind == JsonValueKind.String
                ? dnEl.GetString() : slug;

            list.Add(new CodexRemoteModel { Slug = slug!, DisplayName = display ?? slug! });
        }
        return list;
    }
}
