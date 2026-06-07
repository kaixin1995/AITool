# Responses-only 站点桥接实现说明

## 文档目的

本文档只描述**当前代码已经采用的真实实现逻辑**，不再保留早期方案推演、备选架构或过渡性讨论。

目标是说明：

- Responses-only 站点现在如何配置
- 三条客户端入口分别怎么走
- 哪些链路是原生透传，哪些链路是兼容桥接
- 相关核心文件分别负责什么

## Responses-only 站点如何配置

当前后台没有单独新增“Responses-only 协议类型”下拉选项。

当前站点页面仍然使用两个能力勾选项：

- 支持 OpenAI
- 支持 Anthropic

当前真实规则是：

- 勾选 OpenAI，未勾选 Anthropic：按 OpenAI 站点处理
- 未勾选 OpenAI，勾选 Anthropic：按 Anthropic 站点处理
- 两个都勾选：按同时支持 OpenAI / Anthropic 的站点处理
- **两个都不勾选：按 Responses-only 站点处理**

当前页面也已经补充提示文案：

> 如果两个都不勾选，则按仅支持 Responses 的站点处理。

## 当前三条调用链路

## OpenAI Chat 调用 Responses-only 站点

当客户端请求：

- `POST /v1/chat/completions`

且命中的站点是 Responses-only 时，当前真实链路是：

- OpenAI Chat 请求
- -> 转换为 Responses 请求
- -> 转发到上游 `/v1/responses`
- -> 上游返回 Responses 响应 / Responses SSE
- -> 再转换回 OpenAI Chat 响应 / Chat SSE
- -> 返回给客户端

也就是：

- `Chat -> Responses -> Chat`

这条链路是当前 Responses-only 兼容能力的基础。

## Anthropic `/v1/messages` 调用 Responses-only 站点

当客户端请求：

- `POST /v1/messages`

且命中的站点是 Responses-only 时，当前真实链路是：

- Anthropic `/v1/messages` 请求
- -> 先复用现有 `Anthropic -> OpenAI Chat` 请求转换
- -> 再复用 `OpenAI Chat -> Responses` 请求转换
- -> 转发到上游 `/v1/responses`
- -> 上游返回 Responses 响应 / Responses SSE
- -> 先转换回 OpenAI Chat 响应 / Chat SSE
- -> 再复用现有 `OpenAI Chat -> Anthropic` 响应转换
- -> 返回给客户端

也就是：

- `Anthropic -> Chat -> Responses -> Chat -> Anthropic`

这里的关键点是：

- **没有单独再造一条 Anthropic -> Responses 的独立主链**
- 仍然是先复用原有 `Anthropic <-> Chat` 能力
- Responses-only 只是接在中间：
  - `Anthropic -> Chat`
  - `Chat -> Responses`
  - `Responses -> Chat`
  - `Chat -> Anthropic`

### Anthropic 非流式链路

非流式时，真实处理顺序是：

- `Anthropic body`
- -> `OpenAI Chat body`
- -> `Responses body`
- -> 上游非流式 Responses 响应
- -> `OpenAI Chat response`
- -> `Anthropic message response`

### Anthropic 流式链路

流式时，真实处理顺序是：

- `Anthropic SSE request`
- -> `OpenAI Chat request`
- -> `Responses request`
- -> 上游 `Responses SSE`
- -> 转成 `OpenAI Chat SSE`
- -> 再转成 `Anthropic SSE`
- -> 返回给客户端

也就是流式同样遵循：

- `Responses -> Chat -> Anthropic`

## Responses 原生调用 Responses-only 站点

当客户端请求：

- `POST /v1/responses`
- `GET /v1/responses` WebSocket

且命中的站点本身就是 Responses-only 时，当前逻辑是：

- 直接透传到上游 Responses 接口
- 不额外插入 Chat / Anthropic 兼容转换主链

也就是：

- `Responses -> Responses`

这是当前最直接的一条链路。

## 当前实现原则

当前代码遵循的实现原则是：

- **最小侵入**
- **优先复用现有桥接链**
- **控制器层只做必要接线**
- **真正的协议转换尽量放在桥接层**

具体表现为：

- OpenAI Chat 侧复用现有 OpenAI 主链
- Anthropic 侧复用现有 Anthropic -> Chat 和 Chat -> Anthropic 能力
- Responses-only 只补：
  - `Chat -> Responses`
  - `Responses -> Chat`
- `/v1/responses` 原生入口继续走原有链路，不额外改造成兼容链

## 当前关键实现文件

## 路由与站点能力识别

- [src/AITool.Web/Services/ProxyRequestMetadataCache.cs](../src/AITool.Web/Services/ProxyRequestMetadataCache.cs)
- [src/AITool.Web/Pages/Admin/Sites/Index.cshtml.cs](../src/AITool.Web/Pages/Admin/Sites/Index.cshtml.cs)
- [src/AITool.Web/Pages/Admin/Sites/Edit.cshtml.cs](../src/AITool.Web/Pages/Admin/Sites/Edit.cshtml.cs)
- [src/AITool.Web/Pages/Admin/Sites/Import.cshtml.cs](../src/AITool.Web/Pages/Admin/Sites/Import.cshtml.cs)
- [src/AITool.Web/Pages/Admin/Sites/Create.cshtml](../src/AITool.Web/Pages/Admin/Sites/Create.cshtml)
- [src/AITool.Web/Pages/Admin/Sites/Edit.cshtml](../src/AITool.Web/Pages/Admin/Sites/Edit.cshtml)

职责：

- 把“两个协议都不勾选”的站点识别为 `Responses`
- 让 OpenAI / Anthropic 客户端在选路时能够命中 Responses-only 站点

## OpenAI 主链接入 Responses-only

- [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs](../src/AITool.Web/Controllers/Proxy/OpenAiProxyController.cs)
- [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs](../src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs)
- [src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Core.cs](../src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Core.cs)
- [src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Responses.cs](../src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Responses.cs)

职责：

- 让 `POST /v1/chat/completions` 能转发到 `/v1/responses`
- 让 Responses 非流式 / 流式结果再回转为 OpenAI Chat

## Anthropic 主链复用 Chat -> Responses

- [src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs](../src/AITool.Web/Controllers/Proxy/AnthropicProxyController.cs)
- [src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Core.cs](../src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Core.cs)
- [src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.RequestConvert.cs](../src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.RequestConvert.cs)
- [src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.ResponseConvert.cs](../src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.ResponseConvert.cs)
- [src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Responses.cs](../src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Responses.cs)
- [src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.StreamToAnthropic.cs](../src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.StreamToAnthropic.cs)

职责：

- `Anthropic -> Chat -> Responses`
- `Responses -> Chat -> Anthropic`
- 保持 Anthropic 侧只做必要接线，不再单独扩成 Responses 专用主链

## Responses 原生链路

- [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs](../src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Responses.cs)
- [src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs](../src/AITool.Web/Controllers/Proxy/OpenAiProxyController.Streaming.cs)
- [src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Responses.cs](../src/AITool.Web/Services/ProxyProtocol/ProxyProtocolBridge.Responses.cs)

职责：

- 继续承接原生 Responses HTTP / SSE / WebSocket 链路
- 保持原生透传和 WebSocket 行为不退化

## 当前实现边界

本文档只说明当前已经采用并保留的真实实现逻辑，不再保留以下内容：

- 早期的多种备选架构讨论
- 已放弃的“Anthropic 控制器内单独长出 Responses 主链”的做法
- 与当前代码不一致的中间过渡设计
- 与本次 Responses-only 接入无直接关系的扩展范围（如 Images / Videos / Count Tokens 等）

## 当前应如何理解这次改造

如果用最简洁的话概括当前实现，可以理解为：

- **Responses 原生入口继续走原生链路。**
- **OpenAI Chat 入口通过 `Chat -> Responses -> Chat` 接入 Responses-only 站点。**
- **Anthropic `/v1/messages` 入口通过 `Anthropic -> Chat -> Responses -> Chat -> Anthropic` 接入 Responses-only 站点。**

这就是当前代码的真实调用链和维护基线。
