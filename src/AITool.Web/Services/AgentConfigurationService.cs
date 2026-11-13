using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AITool.Web.Services;

public sealed class AgentConfigurationRequest
{
    public string Agent { get; set; } = "codex";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ExpectedHash { get; set; } = "";
}

public sealed class AgentConfigurationService
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string _userRoot;
    public AgentConfigurationService() : this(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { }
    internal AgentConfigurationService(string userRoot) => _userRoot = userRoot;
    private string PathFor(string agent) => Path.Combine(_userRoot,
        agent == "codex" ? ".codex" : agent == "claude" ? ".claude" : throw new ArgumentException("仅支持 codex 和 claude。"), agent == "codex" ? "config.toml" : "settings.json");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Quote(string value) => JsonSerializer.Serialize(value);
    private static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
    public object Status(string agent)
    {
        var path = PathFor(agent); var current = Read(path);
        var hash = Read(path + ".aitool.hash");
        return new { path, exists = File.Exists(path), hash = Hash(current), connected = hash.Length > 0,
            drifted = hash.Length > 0 && hash != Hash(current), canRestore = File.Exists(path + ".aitool.backup") };
    }
    public string Preview(AgentConfigurationRequest request)
    {
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new ArgumentException("请输入有效的网关地址。");
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0) throw new ArgumentException("网关地址不能包含查询参数或片段。");
        if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Contains('\n')) throw new ArgumentException("模型名称无效。");
        var existing = Read(PathFor(request.Agent));
        if (request.BaseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            request.BaseUrl = request.BaseUrl.TrimEnd('/')[..^3];
        if (request.Agent == "claude")
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey)) throw new ArgumentException("请输入 Claude 使用的访问密钥。");
            var root = string.IsNullOrWhiteSpace(existing) ? new JsonObject() : JsonNode.Parse(existing)!.AsObject();
            root["env"] ??= new JsonObject();
            root["env"]!["ANTHROPIC_BASE_URL"] = request.BaseUrl.TrimEnd('/');
            root["env"]!["ANTHROPIC_AUTH_TOKEN"] = request.ApiKey;
            root["env"]!["ANTHROPIC_MODEL"] = request.Model;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        var sectionMatch = Regex.Match(existing, @"(?m)^\s*\[");
        var section = sectionMatch.Success ? sectionMatch.Index : -1;
        if (existing.Contains("\"\"\"", StringComparison.Ordinal) || existing.Contains("'''", StringComparison.Ordinal))
            throw new ArgumentException("配置包含多行字符串，请使用预览内容手动合并。");
        if (Regex.IsMatch(existing, @"(?m)^[ \t]*(model_providers[ \t]*=|model_providers\.|\[\s*['""].*model_providers|\[\[)"))
            throw new ArgumentException("配置使用了复杂 TOML 表语法，请手动合并网关配置。");
        var head = section >= 0 ? existing[..section] : existing;
        var tail = section >= 0 ? existing[section..] : "";
        head = Regex.Replace(head, @"(?m)^[ \t]*(model|model_provider)[ \t]*=.*(?:\r?\n|$)", "");
        if (Regex.IsMatch(tail, @"(?m)^\s*\[model_providers\.aitool\]"))
            tail = Regex.Replace(tail, @"(?ms)^[ \t]*\[model_providers\.aitool\][ \t]*(?:#[^\r\n]*)?\r?\n.*?(?=^[ \t]*\[|\z)", "");
        return $"model = {Quote(request.Model)}\nmodel_provider = \"aitool\"\n" + head + "\n" + tail +
            $"\n[model_providers.aitool]\nname = \"AI Tool\"\nbase_url = {Quote(request.BaseUrl.TrimEnd('/') + "/v1")}\nwire_api = \"responses\"\nenv_key = \"AITOOL_API_KEY\"\n";
    }
    public async Task<object> ApplyAsync(AgentConfigurationRequest request, bool restore, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = PathFor(request.Agent); var current = Read(path);
            if (request.ExpectedHash != Hash(current)) throw new InvalidOperationException("配置已变化，请刷新预览后重试。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (restore)
            {
                if (!File.Exists(path + ".aitool.backup") || Read(path + ".aitool.hash") != Hash(current)) throw new InvalidOperationException("配置已有外部改动，不能自动恢复。");
                await File.WriteAllTextAsync(path + ".tmp", Read(path + ".aitool.backup"), ct);
                if (Hash(Read(path)) != request.ExpectedHash) throw new InvalidOperationException("配置在恢复期间发生变化，已停止写入。");
                File.Move(path + ".tmp", path, true);
                File.Delete(path + ".aitool.hash"); File.Delete(path + ".aitool.backup");
            }
            else
            {
                var managed = Read(path + ".aitool.hash");
                if (managed.Length > 0 && managed != Hash(current)) throw new InvalidOperationException("检测到外部配置改动，请先合并配置。");
                var prepared = Preview(request);
                if (!File.Exists(path + ".aitool.backup")) await File.WriteAllTextAsync(path + ".aitool.backup", current, ct);
                await File.WriteAllTextAsync(path + ".tmp", prepared, ct);
                if (Hash(Read(path)) != request.ExpectedHash) throw new InvalidOperationException("配置在保存期间发生变化，已停止写入。");
                File.Move(path + ".tmp", path, true);
                await File.WriteAllTextAsync(path + ".aitool.hash", Hash(prepared), ct);
            }
            return Status(request.Agent);
        }
        finally { _gate.Release(); }
    }
}
