using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AITool.Desktop.Models;

namespace AITool.Desktop.Services;

public sealed class ApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _httpClient;
    private readonly TokenStore _tokenStore;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Uri? _baseAddress;

    public ApiService(HttpClient httpClient, TokenStore tokenStore)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        ConfigureBaseAddress();
    }

    public void ConfigureBaseAddress()
    {
        var serverUrl = _tokenStore.Settings.ServerUrl.Trim().TrimEnd('/') + "/";
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var baseAddress))
        {
            throw new ApiException("服务端地址无效", string.Empty, 0);
        }

        // HttpClient 发出请求后不能再修改 BaseAddress，因此只保存当前服务端地址，逐个请求使用绝对 URI。
        _baseAddress = baseAddress;
    }

    public Task<AuthStatus> GetAuthStatusAsync(CancellationToken cancellationToken = default)
    {
        // /api/auth/status 是公开端点，不需要 token，也不应该触发 401 刷新逻辑。
        return SendAsync<AuthStatus>(HttpMethod.Get, "/api/auth/status", null, false, cancellationToken);
    }

    public Task<TokenPair> LoginAsync(string password, CancellationToken cancellationToken = default)
    {
        return SendAsync<TokenPair>(HttpMethod.Post, "/api/auth/login", new { password }, false, cancellationToken);
    }

    public Task<TokenPair> SetupAsync(string password, string confirmPassword, CancellationToken cancellationToken = default)
    {
        return SendAsync<TokenPair>(HttpMethod.Post, "/api/auth/setup", new { password, confirmPassword }, false, cancellationToken);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var refreshToken = _tokenStore.Settings.RefreshToken;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            await SendAsync<object>(HttpMethod.Post, "/api/auth/logout", new { refreshToken }, false, cancellationToken);
        }

        _tokenStore.ClearTokens();
    }

    public async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        bool retryOnUnauthorized = true,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, CreateRequestUri(path));
        var settings = _tokenStore.Settings;
        if (!string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        }

        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && retryOnUnauthorized)
        {
            if (await RefreshAccessTokenAsync(cancellationToken))
            {
                return await SendAsync<T>(method, path, body, false, cancellationToken);
            }

            _tokenStore.ClearTokens();
            throw new ApiException("登录已过期，请重新登录", "unauthenticated", 401);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response.StatusCode, responseBody);
        }

        return DeserializeResponse<T>(responseBody);
    }

    internal async Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        var refreshToken = _tokenStore.Settings.RefreshToken;
        if (string.IsNullOrWhiteSpace(refreshToken)) return false;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var latestToken = _tokenStore.Settings.RefreshToken;
            if (latestToken != refreshToken)
            {
                return !string.IsNullOrWhiteSpace(latestToken);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, CreateRequestUri("/api/auth/refresh"))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { refreshToken }, JsonOptions),
                    Encoding.UTF8,
                    "application/json")
            };
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode) return false;

            var tokens = DeserializeResponse<TokenPair>(body);
            _tokenStore.SaveTokens(tokens);
            return !string.IsNullOrWhiteSpace(tokens.AccessToken);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ApiException)
        {
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<DeveloperRawResponse> SendRawAsync(
        HttpMethod method,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri);
        foreach (var header in headers)
        {
            if (!string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (headers.TryGetValue("Content-Type", out var contentType)
                && !string.IsNullOrWhiteSpace(contentType))
            {
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return new DeveloperRawResponse
        {
            StatusCode = (int)response.StatusCode,
            Body = await response.Content.ReadAsStringAsync(cancellationToken)
        };
    }

    public Uri CreateRequestUri(string path)
    {
        if (_baseAddress is null)
        {
            throw new ApiException("服务端地址未配置", string.Empty, 0);
        }

        return new Uri(_baseAddress, path.TrimStart('/'));
    }

    private static T DeserializeResponse<T>(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return default!;

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("success", out var successElement)
            && successElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && IsApiResponseShape(root))
        {
            var envelope = JsonSerializer.Deserialize<ApiResponse<T>>(responseBody, JsonOptions)
                ?? throw new ApiException("服务端响应无效", string.Empty, 0);
            if (!envelope.Success)
            {
                throw new ApiException(envelope.Message ?? "操作失败", envelope.ErrorCode ?? string.Empty, 200);
            }

            return envelope.Data!;
        }

        return JsonSerializer.Deserialize<T>(responseBody, JsonOptions)!;
    }

    /// <summary>
    /// 判断 JSON 对象是否符合 ApiResponse 信封结构（只含 success/data/message/errorCode 四个 key），
    /// 避免误判恰好含 success 布尔字段的领域对象。
    /// </summary>
    private static bool IsApiResponseShape(JsonElement root)
    {
        var allowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "success", "data", "message", "errorCode" };
        foreach (var property in root.EnumerateObject())
        {
            if (!allowedKeys.Contains(property.Name))
                return false;
        }
        return true;
    }

    private static ApiException CreateApiException(HttpStatusCode statusCode, string responseBody)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<ApiResponse<JsonElement>>(responseBody, JsonOptions);
            if (envelope is not null && !string.IsNullOrWhiteSpace(envelope.Message))
            {
                return new ApiException(envelope.Message, envelope.ErrorCode ?? string.Empty, (int)statusCode);
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误响应继续使用状态码文本。
        }

        return new ApiException($"请求失败（HTTP {(int)statusCode}）", string.Empty, (int)statusCode);
    }
}

public sealed class ApiException : Exception
{
    public ApiException(string message, string errorCode, int statusCode)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string ErrorCode { get; }
    public int StatusCode { get; }
}
