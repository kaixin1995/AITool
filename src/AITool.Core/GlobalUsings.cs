// Core 宿主的全局类型别名。
// 从 Infrastructure 共享层引入的代理协议转换、日志格式化和查询模型类型。
// 这些类型原先是 Core/Services 下的独立副本，现已统一到 Infrastructure 层。
global using ConsoleProxyLogFormatter = AITool.Infrastructure.Proxy.ConsoleProxyLogFormatter;
global using ProxyProtocolBridge = AITool.Infrastructure.ProxyProtocol.ProxyProtocolBridge;
global using ChatToResponsesStreamState = AITool.Infrastructure.ProxyProtocol.ChatToResponsesStreamState;
global using RouteModelItem = AITool.Infrastructure.Proxy.RouteModelItem;
global using RouteEntryListItem = AITool.Infrastructure.Proxy.RouteEntryListItem;
global using SiteInstanceItem = AITool.Infrastructure.Proxy.SiteInstanceItem;
global using DiscoveredSiteItem = AITool.Infrastructure.Proxy.DiscoveredSiteItem;
global using RouteRuleListItem = AITool.Infrastructure.Proxy.RouteRuleListItem;
global using ClientSimulatorModelItemViewModel = AITool.Infrastructure.Proxy.ClientSimulatorModelItemViewModel;
