using System.Text.Json;
using AITool.Application.Codex;

namespace AITool.Infrastructure.Codex;

/// <summary>
/// 解析 CPA 格式的 Codex 认证文件（扁平 JSON）。纯内存、无状态、可静态调用。
/// 对应 CPA 的 CodexTokenStorage（reference-projects/CLIProxyAPI/internal/auth/codex/token.go:18-39）。
/// </summary>
public static class CodexCredentialParser
{
    /// <summary>
    /// 解析单个 JSON 字符串。失败返回 Success=false（不抛异常）。
    /// </summary>
    public static CodexCredentialParseResult Parse(string json, string? fileName = null)
    {
        var result = new CodexCredentialParseResult { FileName = fileName };

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { result.Error = "JSON 格式非法"; return result; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                result.Error = "JSON 顶层不是对象";
                return result;
            }

            // —— type 判定：必须 codex；缺失时宽松推断（仅本期支持 codex）——
            var typeOk = true;
            if (root.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                var t = typeEl.GetString();
                if (!string.Equals(t, "codex", StringComparison.OrdinalIgnoreCase))
                {
                    result.Error = $"非 Codex 类型凭证（type={t}）";
                    return result;
                }
            }
            else
            {
                typeOk = false; // 缺失 type，下方校验字段后宽松推断
            }

            // —— 必填字段 ——
            var accessToken = GetString(root, "access_token");
            var refreshToken = GetString(root, "refresh_token");
            var idToken = GetString(root, "id_token");
            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(idToken))
            {
                result.Error = "缺少 access_token / refresh_token / id_token 中的必填项";
                return result;
            }
            result.TypeInferred = !typeOk;

            // —— id_token JWT 解析（权威），失败回退顶层字段 ——
            var claims = CodexJwtParser.Parse(idToken);
            result.AccountId = claims?.AccountId ?? GetString(root, "account_id");
            result.Email = claims?.Email ?? GetString(root, "email");
            result.PlanType = claims?.PlanType;

            // —— 过期时间：优先 expired 字段（ISO），其次 JWT exp claim ——
            result.TokenExpiresAt = ParseExpired(GetString(root, "expired")) ?? TryGetJwtExp(idToken);

            result.AccessToken = accessToken;
            result.RefreshToken = refreshToken;
            result.IdToken = idToken;

            // —— 默认显示名 ——
            var fileBase = !string.IsNullOrEmpty(fileName)
                ? System.IO.Path.GetFileNameWithoutExtension(fileName)
                : null;
            result.DisplayName = !string.IsNullOrEmpty(result.Email) ? result.Email
                : !string.IsNullOrEmpty(fileBase) ? fileBase
                : "Codex 账号";

            result.Success = true;
            return result;
        }
    }

    /// <summary>
    /// 批量解析（多文件），返回逐文件结果（含失败项），不因单文件失败中断。
    /// </summary>
    public static List<CodexCredentialParseResult> ParseMany(IEnumerable<(string FileName, string Json)> files)
    {
        var list = new List<CodexCredentialParseResult>();
        foreach (var (name, json) in files)
        {
            // 逐文件解析，单文件异常不影响其它（Parse 内部已捕获 JSON 异常）
            try { list.Add(Parse(json, name)); }
            catch (Exception ex) { list.Add(new CodexCredentialParseResult { FileName = name, Error = ex.Message }); }
        }
        return list;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            var v = el.GetString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        return null;
    }

    private static DateTimeOffset? ParseExpired(string? expired)
    {
        if (string.IsNullOrWhiteSpace(expired)) return null;
        // 优先 ISO 8601
        if (DateTimeOffset.TryParse(expired, out var dto)) return dto;
        // 兼容 unix 秒
        if (long.TryParse(expired, out var unix)) return DateTimeOffset.FromUnixTimeSeconds(unix);
        return null;
    }

    private static DateTimeOffset? TryGetJwtExp(string idToken)
    {
        try
        {
            var segments = idToken.Split('.');
            if (segments.Length < 2) return null;
            var payload = CodexJwtParser.Base64UrlDecode(segments[1]);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var unix))
                return DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        catch { }
        return null;
    }
}
