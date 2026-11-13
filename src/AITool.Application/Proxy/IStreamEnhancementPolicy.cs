namespace AITool.Application.Proxy;

public interface IStreamEnhancementPolicy
{
    bool LoopDetectionEnabled { get; }
    int KeepAliveSeconds { get; }
}
