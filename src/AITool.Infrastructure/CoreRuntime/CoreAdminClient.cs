using System.Net.Http.Json;
using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧最小 Core 客户端。
/// 当前阶段先提供握手、全量同步、ack、replay 这些最关键的控制与补传接口调用。
/// 等后续真正拆成独立 Admin 进程后，这个客户端可以直接复用。
/// </summary>
public sealed class CoreAdminClient
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 Core Admin 客户端。
    /// </summary>
    public CoreAdminClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 向 Core 发起握手，获取当前配置状态与同步建议。
    /// </summary>
    public async Task<CoreAdminHandshakeResponse> HandshakeAsync(CoreAdminHandshakeRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/core/config/handshake", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CoreAdminHandshakeResponse>(cancellationToken))!;
    }

    /// <summary>
    /// 向 Core 下发一份完整配置快照。
    /// </summary>
    public async Task<CoreFullSyncResult> FullSyncAsync(CoreRuntimeConfigSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/core/config/full-sync", snapshot, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CoreFullSyncResult>(cancellationToken))!;
    }

    /// <summary>
    /// 提交事件 ack，通知 Core 删除已确认的积压数据。
    /// </summary>
    public async Task<CoreAckResult> AckAsync(CoreAdminAckRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("api/core/events/ack", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CoreAckResult>(cancellationToken))!;
    }

    /// <summary>
    /// 读取某个序号之后的 replay 事件。
    /// </summary>
    public async Task<IReadOnlyList<CoreAdminEventEnvelope>> ReplayAsync(long afterSequenceId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"api/core/events/replay?afterSequenceId={afterSequenceId}", cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<CoreAdminEventEnvelope>>(cancellationToken)) ?? [];
    }
}

/// <summary>
/// Core 全量同步结果。
/// </summary>
public sealed class CoreFullSyncResult
{
    /// <summary>
    /// 是否应用了配置。
    /// </summary>
    public bool Applied { get; set; }

    /// <summary>
    /// 是否因为配置未变化而忽略。
    /// </summary>
    public bool Ignored { get; set; }

    /// <summary>
    /// 当前配置版本。
    /// </summary>
    public long ConfigVersion { get; set; }

    /// <summary>
    /// 当前配置哈希。
    /// </summary>
    public string ConfigHash { get; set; } = string.Empty;
}

/// <summary>
/// Core ack 结果。
/// </summary>
public sealed class CoreAckResult
{
    /// <summary>
    /// 已确认序号。
    /// </summary>
    public long AckedSequenceId { get; set; }

    /// <summary>
    /// 确认时间。
    /// </summary>
    public DateTimeOffset AckedAt { get; set; }
}
