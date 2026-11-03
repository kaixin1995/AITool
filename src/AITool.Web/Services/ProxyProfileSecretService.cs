using System.Diagnostics;
using System.Net;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace AITool.Web.Services;

/// <summary>
/// Protects proxy credentials independently from the public endpoint. The host persists its
/// DataProtection key ring and migrates legacy URLs before accepting requests. A lost or
/// replaced key ring is not allowed to keep the host from starting: startup migration
/// quarantines profiles whose stored secrets became unreadable instead of failing.
/// </summary>
public sealed class ProxyProfileSecretService
{
    /// <summary>ConfigurationError marker for profiles quarantined by an unreadable key ring.</summary>
    public const string UndecryptableReason = "proxy_secrets_undecryptable";
    private readonly IDataProtector _protector;
    private readonly ILogger<ProxyProfileSecretService>? _logger;

    public ProxyProfileSecretService(IDataProtectionProvider provider, ILogger<ProxyProfileSecretService>? logger = null)
    {
        _protector = provider.CreateProtector("AITool.ProxyProfile.Credentials.v1");
        _logger = logger;
    }

    public static bool HasCredentials(ProxyProfile profile) =>
        !string.IsNullOrEmpty(profile.ProtectedUsername) || !string.IsNullOrEmpty(profile.ProtectedPassword);

    public NetworkCredential? ReadCredentials(ProxyProfile profile)
    {
        if (!HasCredentials(profile))
        {
            return null;
        }

        // Fail closed on partial/corrupt data; never fall back to unauthenticated egress.
        if (string.IsNullOrEmpty(profile.ProtectedUsername) || string.IsNullOrEmpty(profile.ProtectedPassword))
        {
            throw new CryptographicException("Proxy credentials are incomplete.");
        }

        try
        {
            var protector = ForProfile(profile);
            return new NetworkCredential(
                protector.CreateProtector("username").Unprotect(profile.ProtectedUsername),
                protector.CreateProtector("password").Unprotect(profile.ProtectedPassword));
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("Proxy credentials could not be decrypted.");
        }
    }

    private IDataProtector ForProfile(ProxyProfile profile) => _protector.CreateProtector(profile.Id.ToString("N"));

    /// <summary>
    /// Accepts old URL userinfo only on input. Splits before percent decoding, so encoded
    /// colons and at signs remain part of the username/password; '+' is not a space.
    /// All errors are constant strings and contain no submitted URL or credentials.
    /// </summary>
    public static bool TryNormalizeProxyUrl(string? value, out string endpoint,
        out NetworkCredential? credentials, out string? error)
    {
        endpoint = string.Empty;
        credentials = null;
        error = "代理地址必须是有效的 HTTP、HTTPS 或 SOCKS 端点，不能包含路径、查询或片段";
        if (string.IsNullOrWhiteSpace(value)) return false;
        var input = value.Trim();
        if (input.Any(char.IsControl) || input.Contains('\\') ||
            !Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            !input.Contains("://", StringComparison.Ordinal) ||
            !EgressProxyValidator.TryValidate(input, out _) ||
            string.IsNullOrEmpty(uri.Host) || uri.Port is <= 0 or > 65535 ||
            (uri.AbsolutePath != string.Empty && uri.AbsolutePath != "/") ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            return false;
        }

        var authority = input[(input.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/', '?', '#')[0];
        var at = authority.LastIndexOf('@');
        var userInfo = at < 0 ? string.Empty : authority[..at];
        if (userInfo.Contains('@')) return false;
        if (userInfo.Length != 0)
        {
            for (var i = 0; i < userInfo.Length; i++)
            {
                if (userInfo[i] != '%') continue;
                if (i + 2 >= userInfo.Length || !Uri.IsHexDigit(userInfo[i + 1]) || !Uri.IsHexDigit(userInfo[i + 2]))
                    return false;
                i += 2;
            }
            var separator = userInfo.IndexOf(':');
            var username = Uri.UnescapeDataString(separator < 0 ? userInfo : userInfo[..separator]);
            var password = separator < 0 ? string.Empty : Uri.UnescapeDataString(userInfo[(separator + 1)..]);
            if (username.Length == 0 || username.Any(char.IsControl) || password.Any(char.IsControl)) return false;
            credentials = new NetworkCredential(username, password);
        }
        else if (input[(input.IndexOf("://", StringComparison.Ordinal) + 3)..].Contains('@'))
        {
            return false;
        }

        endpoint = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        if (endpoint.Length > 512) return false;
        error = null;
        return true;
    }

    /// <summary>
    /// Omitted action preserves existing credentials unless a legacy URL explicitly supplies
    /// userinfo. Explicit Keep/Replace/Clear always wins; replacement requires both fields.
    /// </summary>
    public bool TryApplyForSave(ProxyProfile profile, string? proxyUrl, string? credentialAction,
        string? username, string? password, out string? error)
    {
        if (!TryNormalizeProxyUrl(proxyUrl, out var endpoint, out var legacyCredentials, out error)) return false;
        var action = credentialAction?.Trim();
        if (string.IsNullOrEmpty(action))
        {
            if (username is not null || password is not null)
            {
                error = "设置凭据必须显式指定 Replace 操作";
                return false;
            }
            action = legacyCredentials is null ? "Keep" : "Replace";
            username = legacyCredentials?.UserName;
            password = legacyCredentials?.Password;
        }

        if (string.Equals(action, "Replace", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(username) || password is null || username.Any(char.IsControl) || password.Any(char.IsControl))
            {
                error = "替换凭据必须提供用户名和密码（密码允许为空）";
                return false;
            }
            var protector = ForProfile(profile);
            var protectedUsername = protector.CreateProtector("username").Protect(username);
            var protectedPassword = protector.CreateProtector("password").Protect(password);
            profile.ProtectedUsername = protectedUsername;
            profile.ProtectedPassword = protectedPassword;
        }
        else if (string.Equals(action, "Clear", StringComparison.OrdinalIgnoreCase))
        {
            profile.ProtectedUsername = null;
            profile.ProtectedPassword = null;
        }
        else if (!string.Equals(action, "Keep", StringComparison.OrdinalIgnoreCase))
        {
            error = "凭据操作仅支持 Keep、Replace 或 Clear";
            return false;
        }

        profile.ProxyUrl = endpoint;
        profile.ConfigurationError = null;
        error = null;
        return true;
    }

    private static NetworkCredential? RecoverLegacyCredentials(string value)
    {
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return null;
        var authority = value[(scheme + 3)..].Split('/', '?', '#')[0];
        var at = authority.LastIndexOf('@');
        if (at <= 0) return null;
        var userInfo = authority[..at];
        if (userInfo.Contains('@')) return null;
        for (var i = 0; i < userInfo.Length; i++)
        {
            if (userInfo[i] != '%') continue;
            if (i + 2 >= userInfo.Length || !Uri.IsHexDigit(userInfo[i + 1]) || !Uri.IsHexDigit(userInfo[i + 2])) return null;
            i += 2;
        }
        var colon = userInfo.IndexOf(':');
        var username = Uri.UnescapeDataString(colon < 0 ? userInfo : userInfo[..colon]);
        var password = colon < 0 ? string.Empty : Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        return username.Length == 0 || username.Any(char.IsControl) || password.Any(char.IsControl)
            ? null : new NetworkCredential(username, password);
    }

    private void ProtectCredentials(ProxyProfile profile, NetworkCredential credentials)
    {
        var protector = ForProfile(profile);
        profile.ProtectedUsername = protector.CreateProtector("username").Protect(credentials.UserName);
        profile.ProtectedPassword = protector.CreateProtector("password").Protect(credentials.Password);
    }

    // Validate each optional field independently: historical quarantine rows may have only
    // a repair payload (or partial credentials). Do not require ReadCredentials' complete
    // username/password pair here. Returns false when any existing field is unreadable;
    // callers must then quarantine the profile — never protect remaining plaintext with a
    // ring that cannot read the row's old payloads. Never include payloads or inner errors.
    private bool TryValidateProtectedFields(ProxyProfile profile)
    {
        var protector = ForProfile(profile);
        try
        {
            if (!string.IsNullOrEmpty(profile.ProtectedUsername))
                protector.CreateProtector("username").Unprotect(profile.ProtectedUsername);
            if (!string.IsNullOrEmpty(profile.ProtectedPassword))
                protector.CreateProtector("password").Unprotect(profile.ProtectedPassword);
            if (!string.IsNullOrEmpty(profile.ProtectedLegacyConfiguration))
                protector.CreateProtector("legacy-configuration").Unprotect(profile.ProtectedLegacyConfiguration);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static async Task ExecuteSqliteTransactionCommandAsync(SQLitePCL.sqlite3 handle, string command,
        string error, CancellationToken ct)
    {
        var timeout = Stopwatch.StartNew();
        int? lastLockResult = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (lastLockResult.HasValue && timeout.Elapsed >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException($"{error} SQLite result code: {lastLockResult.Value}.");
            var result = SQLitePCL.raw.sqlite3_exec(handle, command);
            if (result == SQLitePCL.raw.SQLITE_OK) return;
            // SQLite can return extended codes, including LOCKED_SHAREDCACHE. Only lock
            // conflicts are retryable here; malformed SQL and all other failures fail fast.
            var primaryCode = result & 0xff;
            if (primaryCode != SQLitePCL.raw.SQLITE_BUSY && primaryCode != SQLitePCL.raw.SQLITE_LOCKED)
                throw new InvalidOperationException($"{error} SQLite result code: {result}.");
            lastLockResult = result;
            var remaining = TimeSpan.FromSeconds(5) - timeout.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new InvalidOperationException($"{error} SQLite result code: {result}.");
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25), ct);
        }
    }

    /// <summary>Idempotent startup migration; must run after schema initialization, before requests.</summary>
    public async Task<int> MigrateLegacyCredentialsAsync(AppDbContext db, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var client = db.Client.CopyNew();
        // SqliteConnection.BeginTransaction and SqlSugar's command retry synchronously on
        // BUSY for seconds, ignoring cancellation. Use native nonblocking BEGIN IMMEDIATE
        // on a pinned independent connection instead, and retry asynchronously ourselves.
        client.CurrentConnectionConfig.IsAutoCloseConnection = false;
        client.Ado.Open();
        var sqlite = client.Ado.Connection as SqliteConnection
            ?? throw new InvalidOperationException("Proxy profile migration requires SQLite.");
        var handle = sqlite.Handle ?? throw new InvalidOperationException("SQLite connection is not open.");
        if (SQLitePCL.raw.sqlite3_busy_timeout(handle, 0) != SQLitePCL.raw.SQLITE_OK)
            throw new InvalidOperationException("Could not configure the SQLite writer for proxy profile migration.");
        await ExecuteSqliteTransactionCommandAsync(handle, "BEGIN IMMEDIATE;",
            "Could not acquire the SQLite writer for proxy profile migration.", ct);
        try
        {
            // BEGIN IMMEDIATE owns the writer throughout the authoritative read, preflight,
            // updates and commit; no deferred transaction upgrade or stale snapshot.
            ct.ThrowIfCancellationRequested();
            var profiles = await client.Queryable<ProxyProfile>().ToListAsync(ct);
            var quarantined = 0;
            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();
                if (TryValidateProtectedFields(profile)) continue;
                // A lost/replaced key ring must not block host startup (the session isolation
                // key self-heals the same way), and it must never mix freshly protected
                // payloads into a row whose existing ones are unreadable. Quarantine: disable
                // with an explicit error, payloads untouched; credentials are re-entered to
                // reactivate. The marker makes repeated startups idempotent no-ops.
                // Even an already-quarantined mixed legacy row can still contain userinfo.
                // Preserve its original URL in a distinct repair field when available; never
                // overwrite any unreadable payload or expose it in the public endpoint.
                var originalUrl = profile.ProxyUrl ?? string.Empty;
                var sanitizedUrl = string.Empty;
                if (Uri.TryCreate(originalUrl.Trim(), UriKind.Absolute, out var legacyUri) && !string.IsNullOrEmpty(legacyUri.Host))
                    TryNormalizeProxyUrl(legacyUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped), out sanitizedUrl, out _, out _);
                var needsSanitizing = !string.Equals(originalUrl, sanitizedUrl, StringComparison.Ordinal);
                if (!profile.IsEnabled && string.Equals(profile.ConfigurationError, UndecryptableReason, StringComparison.Ordinal) && !needsSanitizing)
                    continue;
                if (needsSanitizing)
                {
                    if (string.IsNullOrEmpty(profile.ProtectedLegacyConfiguration))
                        profile.ProtectedLegacyConfiguration = ForProfile(profile).CreateProtector("legacy-configuration").Protect(originalUrl);
                    profile.ProxyUrl = sanitizedUrl;
                }                var oldRevision = profile.Revision;
                profile.IsEnabled = false;
                profile.ConfigurationError = UndecryptableReason;
                profile.UpdatedAt = DateTimeOffset.UtcNow;
                profile.Revision = Math.Max(1, oldRevision + 1);
                var updated = await client.Updateable(profile)
                    .Where(x => x.Id == profile.Id && x.Revision == oldRevision)
                    .ExecuteCommandAsync(ct);
                if (updated != 1)
                    throw new InvalidOperationException("Proxy profile migration encountered a concurrent change.");
                quarantined++;
            }
            if (quarantined > 0)
                _logger?.LogWarning(
                    "{Count} proxy profile(s) hold secrets that cannot be decrypted with the current Data Protection key ring; "
                    + "they were disabled instead of blocking startup. Re-enter their credentials to reactivate them.",
                    quarantined);

            var changed = 0;
            foreach (var profile in profiles)
            {
                ct.ThrowIfCancellationRequested();
                if (!profile.IsEnabled && string.Equals(profile.ConfigurationError, UndecryptableReason, StringComparison.Ordinal))
                    continue; // Quarantined above (or by an earlier startup): never re-protect into an unreadable row.
                var original = profile.ProxyUrl ?? string.Empty;
                var valid = TryNormalizeProxyUrl(original, out var endpoint, out var credentials, out _);
                if (!valid)
                {
                    if (profile.ConfigurationError == "invalid_legacy_proxy_endpoint" && !profile.IsEnabled &&
                        !string.IsNullOrEmpty(profile.ProtectedLegacyConfiguration) && original.Length == 0 && profile.Revision >= 1)
                        continue;
                    // Keep the exact original only in an encrypted repair payload. A bad historical
                    // row must not prevent unrelated profiles/accounts or the entire host from starting.
                    if (string.IsNullOrEmpty(profile.ProtectedLegacyConfiguration))
                        profile.ProtectedLegacyConfiguration = ForProfile(profile).CreateProtector("legacy-configuration").Protect(original);
                    credentials = RecoverLegacyCredentials(original.Trim());
                    endpoint = string.Empty;
                    if (Uri.TryCreate(original.Trim(), UriKind.Absolute, out var legacyUri) && !string.IsNullOrEmpty(legacyUri.Host))
                    {
                        // Strip all non-endpoint components, not just userinfo: path/query/fragment
                        // can contain secrets as well. Only retain an authority accepted by the parser.
                        var authority = legacyUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
                        TryNormalizeProxyUrl(authority, out endpoint, out _, out _);
                    }
                    profile.IsEnabled = false;
                    profile.ConfigurationError = "invalid_legacy_proxy_endpoint";
                }
                else if (credentials is null && profile.Revision >= 1 && string.Equals(endpoint, original, StringComparison.Ordinal))
                {
                    continue;
                }
                var oldRevision = profile.Revision;
                if (!HasCredentials(profile) && credentials is not null)
                {
                    ProtectCredentials(profile, credentials);
                }
                profile.ProxyUrl = endpoint;
                profile.Revision = Math.Max(1, oldRevision + 1);
                profile.UpdatedAt = DateTimeOffset.UtcNow;
                var updated = await client.Updateable(profile)
                    .Where(x => x.Id == profile.Id && x.Revision == oldRevision)
                    .ExecuteCommandAsync(ct);
                if (updated != 1)
                    throw new InvalidOperationException("Proxy profile migration encountered a concurrent change.");
                changed += updated;
            }
            ct.ThrowIfCancellationRequested();
            await ExecuteSqliteTransactionCommandAsync(handle, "COMMIT;",
                "Could not commit proxy profile migration.", ct);
            return changed;
        }
        catch
        {
            // Preserve the original exception, including cancellation; disposal also closes
            // the connection if rollback itself fails.
            SQLitePCL.raw.sqlite3_exec(handle, "ROLLBACK;");
            throw;
        }
    }

    /// <summary>Probe-only transport helper. Credentials never travel in the proxy address.</summary>
    public static HttpClientHandler CreateProxyHandler(string endpoint, NetworkCredential? credentials = null)
    {
        if (!TryNormalizeProxyUrl(endpoint, out var sanitized, out var embedded, out _) || embedded is not null)
            throw new ArgumentException("A sanitized proxy endpoint is required.", nameof(endpoint));
        return new HttpClientHandler
        {
            Proxy = new WebProxy(new Uri(sanitized)) { Credentials = credentials },
            UseProxy = true,
            UseDefaultCredentials = false,
            AllowAutoRedirect = false
            // Leave ServerCertificateCustomValidationCallback unset: normal TLS verification.
        };
    }
}
