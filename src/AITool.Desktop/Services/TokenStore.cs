using System.Text.Json;
using AITool.Desktop.Models;

namespace AITool.Desktop.Services;

public sealed class TokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _sync = new();
    private readonly string _settingsPath;
    private AppSettings _settings;

    public TokenStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var directory = Path.Combine(appData, "AITool");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
        _settings = LoadSettings();
    }

    public AppSettings Settings
    {
        get
        {
            lock (_sync)
            {
                return Clone(_settings);
            }
        }
    }

    public void SaveServerUrl(string serverUrl)
    {
        lock (_sync)
        {
            _settings.ServerUrl = serverUrl.Trim().TrimEnd('/');
            SaveSettings();
        }
    }

    public void SaveTokens(TokenPair tokens)
    {
        lock (_sync)
        {
            _settings.AccessToken = tokens.AccessToken;
            _settings.RefreshToken = tokens.RefreshToken;
            _settings.AccessTokenExpiresAt = tokens.AccessTokenExpiresAt;
            _settings.RefreshTokenExpiresAt = tokens.RefreshTokenExpiresAt;
            SaveSettings();
        }
    }

    public void ClearTokens()
    {
        lock (_sync)
        {
            _settings.AccessToken = string.Empty;
            _settings.RefreshToken = string.Empty;
            _settings.AccessTokenExpiresAt = null;
            _settings.RefreshTokenExpiresAt = null;
            SaveSettings();
        }
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // 设置文件损坏或不可读时使用默认配置，后续保存会覆盖为有效格式。
        }

        return new AppSettings();
    }

    private void SaveSettings()
    {
        var json = JsonSerializer.Serialize(_settings, JsonOptions);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _settingsPath, true);
    }

    private static AppSettings Clone(AppSettings source)
    {
        return new AppSettings
        {
            ServerUrl = source.ServerUrl,
            AccessToken = source.AccessToken,
            RefreshToken = source.RefreshToken,
            AccessTokenExpiresAt = source.AccessTokenExpiresAt,
            RefreshTokenExpiresAt = source.RefreshTokenExpiresAt
        };
    }
}
