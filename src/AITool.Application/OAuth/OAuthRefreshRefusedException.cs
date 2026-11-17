using System.Net;
using System.Text.Json;

namespace AITool.Application.OAuth;

/// <summary>A refresh endpoint explicitly rejected the refresh credential.</summary>
public sealed class OAuthRefreshRefusedException(string message) : InvalidOperationException(message)
{
    public static bool IsCredentialRejection(HttpStatusCode status, string body)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)) return false;
            if (error.ValueKind == JsonValueKind.Object && !error.TryGetProperty("code", out error)) return false;
            if (error.ValueKind != JsonValueKind.String) return false;
            var code = error.GetString();
            return string.Equals(code, "invalid_grant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "invalid_token", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "refresh_token_expired", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "refresh_token_revoked", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "refresh_token_reused", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }
}
