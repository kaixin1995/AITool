global using AppVersionInfo = AITool.Infrastructure.Hosting.AppVersionInfo;
global using HttpExceptionLoggingFilter = AITool.Infrastructure.Hosting.HttpExceptionLoggingFilter;
global using HttpLogFormatter = AITool.Infrastructure.Hosting.HttpLogFormatter;

// 从 Infrastructure 共享层引入的代理协议转换、日志格式化和查询模型类型。
// 这些类型原先是 Web/Services 下的独立副本，现已统一到 Infrastructure 层。
global using ConsoleProxyLogFormatter = AITool.Infrastructure.Proxy.ConsoleProxyLogFormatter;
global using ProxyProtocolBridge = AITool.Infrastructure.ProxyProtocol.ProxyProtocolBridge;
global using ChatToResponsesStreamState = AITool.Infrastructure.ProxyProtocol.ChatToResponsesStreamState;
global using RouteModelItem = AITool.Infrastructure.Proxy.RouteModelItem;
global using RouteEntryListItem = AITool.Infrastructure.Proxy.RouteEntryListItem;
global using SiteInstanceItem = AITool.Infrastructure.Proxy.SiteInstanceItem;
global using DiscoveredSiteItem = AITool.Infrastructure.Proxy.DiscoveredSiteItem;
global using RouteRuleListItem = AITool.Infrastructure.Proxy.RouteRuleListItem;
global using ClientSimulatorModelItemViewModel = AITool.Infrastructure.Proxy.ClientSimulatorModelItemViewModel;

// 模型并发控制相关类型，原先是 Web/Services 下的副本，现已统一到 Infrastructure/Proxy 层。
global using ModelConcurrencyLimiter = AITool.Infrastructure.Proxy.ModelConcurrencyLimiter;
global using ConcurrencyAcquireMode = AITool.Infrastructure.Proxy.ConcurrencyAcquireMode;
global using ConcurrencyAcquireResult = AITool.Infrastructure.Proxy.ConcurrencyAcquireResult;
global using ActiveModelConcurrencyEntry = AITool.Infrastructure.Proxy.ActiveModelConcurrencyEntry;

// 开发者调用追踪相关类型，原先是 Web/Services 下的副本，现已统一到 Infrastructure/Proxy 层。
global using DeveloperInvocationTraceStore = AITool.Infrastructure.Proxy.DeveloperInvocationTraceStore;
global using DeveloperInvocationTraceRequest = AITool.Infrastructure.Proxy.DeveloperInvocationTraceRequest;
global using DeveloperInvocationAttempt = AITool.Infrastructure.Proxy.DeveloperInvocationAttempt;
global using DeveloperInvocationResult = AITool.Infrastructure.Proxy.DeveloperInvocationResult;
global using DeveloperInvocationTraceEntry = AITool.Infrastructure.Proxy.DeveloperInvocationTraceEntry;
global using DeveloperInvocationTraceAttempt = AITool.Infrastructure.Proxy.DeveloperInvocationTraceAttempt;
