using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// 配置变更应用事件发布器。
/// <para>
/// 当 Core 成功应用了一份配置（全量或增量）后，通过此发布器向事件总线发送确认通知。
/// Admin 侧消费此事件后可实时感知配置已生效，无需依赖定时握手轮询。
/// </para>
/// </summary>
public sealed class CoreConfigAppliedEventPublisher
{
    private readonly CoreEventSequenceProvider _sequenceProvider;
    private readonly CoreAdminEventBus _eventBus;

    /// <summary>
    /// 初始化配置变更应用事件发布器。
    /// </summary>
    public CoreConfigAppliedEventPublisher(
        CoreEventSequenceProvider sequenceProvider,
        CoreAdminEventBus eventBus)
    {
        _sequenceProvider = sequenceProvider;
        _eventBus = eventBus;
    }

    /// <summary>
    /// 发布一条配置变更应用事件。
    /// </summary>
    /// <param name="syncMode">同步模式（full 或 patch）。</param>
    /// <param name="configVersion">应用后的配置版本号。</param>
    /// <param name="configHash">应用后的配置哈希值。</param>
    /// <param name="previousConfigVersion">应用前的配置版本号。</param>
    /// <param name="previousConfigHash">应用前的配置哈希值。</param>
    /// <param name="changedCategories">增量同步时变更的实体类别列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task PublishAsync(
        string syncMode,
        long configVersion,
        string configHash,
        long previousConfigVersion,
        string previousConfigHash,
        List<string>? changedCategories = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new CoreConfigAppliedEvent
        {
            ConfigVersion = configVersion,
            ConfigHash = configHash,
            SyncMode = syncMode,
            PreviousConfigVersion = previousConfigVersion,
            PreviousConfigHash = previousConfigHash,
            ChangedCategories = changedCategories ?? [],
            OccurredAt = DateTimeOffset.UtcNow
        };

        var envelope = CoreAdminEventEnvelopeBuilder.CreateConfigAppliedEnvelope(
            _sequenceProvider.Next(), payload);
        await _eventBus.PublishAsync(envelope, cancellationToken);
    }
}
