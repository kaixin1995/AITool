using System.Net.Http.Json;
using System.Text.Json;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using Microsoft.Extensions.Logging;

namespace AITool.Web.Services;

/// <summary>
/// Codex 手动重置额度 credits 服务实现。
/// 调用 ChatGPT wham API：rate-limit-reset-credits / consume。
/// </summary>
public sealed class CodexResetCreditsService : ICodexResetCreditsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CodexResetCreditsService> _logger;

    public CodexResetCreditsService(HttpClient httpClient, ILogger<CodexResetCreditsService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<CodexResetCreditsInfo> QueryResetCreditsAsync(CodexAccount account, CancellationToken ct)
    {
        var info = new CodexResetCreditsInfo();

        if (string.IsNullOrEmpty(account.AccessToken))
        {
            info.Error = "账号缺少 access token";
            return info;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {account.AccessToken}");
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/0.76.0 (Debian 13.0.0; x86_64) WindowsTerminal");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
            request.Headers.TryAddWithoutValidation("Originator", "Codex Desktop");
            if (!string.IsNullOrEmpty(account.AccountId))
            {
                request.Headers.TryAddWithoutValidation("Chatgpt-Account-Id", account.AccountId);
            }

            var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            info.RawJson = body;

            if (!response.IsSuccessStatusCode)
            {
                info.Error = $"HTTP {(int)response.StatusCode}: {body}";
                _logger.LogWarning("Query Codex reset credits failed: {Status} {Body}", response.StatusCode, body);
                return info;
            }

            var json = JsonDocument.Parse(body);
            var root = json.RootElement;

            // 解析 available_count
            if (root.TryGetProperty("available_count", out var availableCountEl))
            {
                info.AvailableCount = availableCountEl.ValueKind == JsonValueKind.Number
                    ? availableCountEl.GetInt32()
                    : int.TryParse(availableCountEl.GetString(), out var c) ? c : 0;
            }

            // 解析 credits 数组（仅保留 available 且 reset_type=codex_rate_limits 且有 expires_at 的）
            if (root.TryGetProperty("credits", out var creditsEl) && creditsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var creditEl in creditsEl.EnumerateArray())
                {
                    var status = creditEl.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;
                    var resetType = creditEl.TryGetProperty("reset_type", out var rtEl) ? rtEl.GetString() : null;
                    if (status != "available" || resetType != "codex_rate_limits") continue;

                    var id = creditEl.TryGetProperty("id", out var idEl) ? idEl.ToString() : Guid.NewGuid().ToString();
                    var expiresAt = ParseTimestamp(creditEl, "expires_at");
                    if (!expiresAt.HasValue) continue;

                    var grantedAt = ParseTimestamp(creditEl, "granted_at");

                    info.Credits.Add(new CodexResetCredit
                    {
                        Id = id,
                        Status = status ?? "available",
                        GrantedAt = grantedAt,
                        ExpiresAt = expiresAt,
                    });
                }
            }

            info.Success = true;
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
            _logger.LogError(ex, "Query Codex reset credits exception");
        }

        return info;
    }

    /// <inheritdoc />
    public async Task<(bool Success, string? Error)> ConsumeResetCreditAsync(CodexAccount account, string redeemRequestId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(account.AccessToken))
        {
            return (false, "账号缺少 access token");
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {account.AccessToken}");
            request.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/0.76.0 (Debian 13.0.0; x86_64) WindowsTerminal");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
            request.Headers.TryAddWithoutValidation("Originator", "Codex Desktop");
            if (!string.IsNullOrEmpty(account.AccountId))
            {
                request.Headers.TryAddWithoutValidation("Chatgpt-Account-Id", account.AccountId);
            }

            var payload = new { redeem_request_id = redeemRequestId };
            request.Content = JsonContent.Create(payload);

            var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"HTTP {(int)response.StatusCode}: {body}";
                _logger.LogWarning("Consume Codex reset credit failed: {Status} {Body}", response.StatusCode, body);
                return (false, error);
            }

            _logger.LogInformation("Codex reset credit consumed successfully for account {AccountId}", account.Id);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Consume Codex reset credit exception");
            return (false, ex.Message);
        }
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement element, string key)
    {
        if (!element.TryGetProperty(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number)
        {
            var ts = el.GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(ts);
        }
        if (el.ValueKind == JsonValueKind.String)
        {
            var str = el.GetString();
            if (long.TryParse(str, out var ts))
            {
                return DateTimeOffset.FromUnixTimeSeconds(ts);
            }
            if (DateTimeOffset.TryParse(str, out var dt))
            {
                return dt;
            }
        }
        return null;
    }
}
