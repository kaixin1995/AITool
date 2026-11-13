using AITool.Application.Proxy;

namespace AITool.Web.Services;

public sealed class GatewayStreamPolicy(GatewayEnhancementSettings settings) : IStreamEnhancementPolicy
{
    public bool LoopDetectionEnabled => settings.Current.LoopDetectionEnabled;
    public int KeepAliveSeconds => settings.Current.KeepAliveEnabled ? settings.Current.KeepAliveSeconds : 0;
}
