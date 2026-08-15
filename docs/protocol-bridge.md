# 协议转换层 AITool.Protocol（函数级）

> 本文是 [README.md](../README.md) 的协议转换细节篇，描述 `src/AITool.Protocol` 项目：OpenAI Chat Completions / Anthropic Messages / OpenAI Responses（外加 legacy Completions）四类协议间的双向转换引擎。
> 转换在代理链路中的位置见 [proxy-pipeline.md](proxy-pipeline.md)；与参考实现 CLIProxyAPI 的字段级同步核查见 `docs/protocol-sync-report.md`（由 [tools/ProtocolSyncCheck](tools.md) 生成）。

---

## 1. 项目定位与文件结构

- 独立类库项目，**零 NuGet 依赖**（只用 BCL `System.Text.Json`），唯一项目引用是 `AITool.Domain`（为了 `CompatibilityRule` DTO）
- 被消费方：`Infrastructure/Proxy/ProxyForwardService`（仅 `ExtractUsageFromElement`）、`Infrastructure/Health/ModelHealthRequestService`、`Web/Controllers/Proxy/*`（全部转换入口）、`Web/Controllers/Admin/ChatApiController` 与 `DeveloperInvocationsApiController`（离线诊断）

整个项目 = 一个巨型 `static partial class ProxyProtocolBridge`（6 个文件，约 6,970 行）+ 5 个流式状态类：

| 文件 | 行数 | 内容 |
|------|------|------|
| `ProxyProtocolBridge.Core.cs` | 597 | 入口分发：`PrepareRequestBody` / `AdaptResponseBodyForClient` / 规则引擎 / Codex 规范化；嵌套 `AnthropicOpenAiStreamState`、`AnthropicToolCallBlockState` |
| `ProxyProtocolBridge.RequestConvert.cs` | 516 | 请求体转换：`BuildOpenAiRequestFromAnthropic`、`BuildAnthropicRequestFromOpenAi` |
| `ProxyProtocolBridge.ResponseConvert.cs` | 1177 | 非流式响应互转 + 整段流式互转 + legacy Completions 四件套；嵌套 `StreamingToolCall`、`StreamingUsageInfo` |
| `ProxyProtocolBridge.StreamToAnthropic.cs` | 522 | OpenAI 流式分片 → Anthropic 事件流（增量状态机） |
| `ProxyProtocolBridge.Responses.cs` | 2676 | Responses 协议全家桶（请求/响应/双向流式）；顶层 `ResponsesToChatStreamState`、`ResponsesToolCallState`、`ChatToResponsesStreamState` |
| `ProxyProtocolBridge.Helpers.cs` | 1482 | 全部共享私有辅助（内容块解析、usage 提取、SSE 写入、映射表）+ 唯一公开辅助 `ExtractUsageFromElement` |

### 流式状态类（均 `public sealed`，由调用方持有）

| 类 | 文件:行 | 用途 |
|----|---------|------|
| `ProxyProtocolBridge.AnthropicOpenAiStreamState` | Core.cs:18 | OpenAI/Responses 流 → Anthropic 事件流会话状态：块索引、thinking/text 块开闭、tool_use 字典、usage 四元组（Input/Cached/CacheCreation/Output）、`StopReason`、`ConversionFailed` |
| `ProxyProtocolBridge.AnthropicToolCallBlockState` | Core.cs:85 | 单个 tool_use 块状态（ContentIndex/ToolUseId/Name/Started/Closed） |
| `ResponsesToChatStreamState` | Responses.cs:10 | Responses 流 → Chat 流：`ToolCallChatIndices`（Responses output_index → Chat 连续索引映射）、reasoning/text StringBuilder、token 三元组 |
| `ResponsesToolCallState` | Responses.cs:61 | 工具调用参数累积（Id/Name/Arguments） |
| `ChatToResponsesStreamState` | Responses.cs:68 | Chat 流或 Anthropic 流 → Responses 事件流：`SawMeaningfulEvent`（防空响应启动）、`OutputText.AppendOutputText()`（惰性求值防 O(n²)）、三组 tool call 索引映射字典、Usage 元组 |

**失败约定**：非流式转换失败返回 `string.Empty`（调用层据此保留路由 fallback）；流式状态机解析失败置 `state.ConversionFailed = true`，尚未写出首字节时仍可 fallback。

---

## 2. 两个统一入口与方向矩阵

协议标识字符串（大小写不敏感）：`"OpenAI"`、`"Anthropic"`、`"Responses"`。

### 2.1 请求方向 — `PrepareRequestBody`（Core.cs:112）

```csharp
public static string PrepareRequestBody(
    string clientProtocol, string targetProtocol, string requestBody,
    string targetModelName, bool enableStreaming,
    string? overrideReasoningEffort = null, string? targetBaseUrl = null,
    IReadOnlyList<CompatibilityRule>? compatibilityRules = null, bool isPassthrough = true)
```

| client \ target | OpenAI | Anthropic | Responses |
|---|---|---|---|
| **OpenAI** | 同协议直通：`ReplaceOpenAiModelAndEnsureStreamUsage` | `BuildAnthropicRequestFromOpenAi` | `ConvertChatRequestToResponses` |
| **Anthropic** | `BuildOpenAiRequestFromAnthropic`（keepReasoning 视规则） | 直通：`ReplaceModelName` | **两段式**：`BuildOpenAiRequestFromAnthropic` → `ConvertChatRequestToResponses` |
| **Responses** | `ConvertResponsesRequestToChat` | 两段式：`ConvertResponsesRequestToChat` → `BuildAnthropicRequestFromOpenAi` | 直通（模型名替换 + stream_usage 保证） |

统一后处理链（顺序固定）：`ApplyReasoningEffort`（按目标协议写 `output_config.effort`+`thinking` / `reasoning.effort` / `reasoning_effort`）→ 目标为 Responses 时 `NormalizeResponsesBody`（`store=false` 兜底；`IsCodexTarget` 为真时剔除不支持字段 + 强制 `stream=true`）→ `ApplyCompatibilityProfile`（兼容规则，最后一步）。

### 2.2 响应方向 — `AdaptResponseBodyForClient`（Core.cs:546）

```csharp
public static string AdaptResponseBodyForClient(
    string clientProtocol, string upstreamProtocol, string responseBody,
    bool isStreaming, string modelName,
    int inputTokens, int cachedTokens, int outputTokens)
```

| client \ upstream | OpenAI | Anthropic | Responses |
|---|---|---|---|
| **OpenAI** | 原样 | 非流式 `BuildOpenAiResponseFromAnthropic` / 流式 `BuildOpenAiStreamingResponseFromAnthropic`（整段聚合） | 非流式 `ConvertResponsesResponseToChat` / 流式 `ConvertResponsesStreamingToChat` |
| **Anthropic** | 非流式 `BuildAnthropicResponseFromOpenAi` / 流式 `BuildAnthropicStreamingResponseFromOpenAi` | 原样 | 两段式：先 → Chat，再 → Anthropic（流式在控制器逐块做） |
| **Responses** | `ConvertChatResponseToResponses`（非流式）/ 控制器逐块 `ConvertChatStreamChunkToResponses` | 非流式 `ConvertAnthropicResponseToResponses`；流式走控制器 `ConvertAnthropicStreamChunkToResponses` | 原样 |

> 注意：Anthropic→OpenAI 的**增量**流式转换在控制器 `ForwardAnthropicStreamAsOpenAiAsync` 内联实现（事件级直转），Protocol 项目提供的是整段聚合版本；Responses 相关的部分流式方向也在控制器层分发。**代码中不存在** `ConvertAnthropicStreamChunkToOpenAi` / `ConvertResponsesSseToResponse` 这两个旧名（老版 README 笔误）。

---

## 3. 流式转换的三种模式

1. **增量状态机**（边收边转，真实时）：`ConvertOpenAiStreamChunkToAnthropic`（每 data 行调一次）、`ConvertChatStreamChunkToResponses`、`ConvertAnthropicStreamChunkToResponses`、`ConvertResponsesStreamingToChat(state)`（按 SSE 事件块 `FlushEvent` 分发）
2. **整段聚合**：先读完上游整个 SSE 文本再重建（`BuildAnthropicStreamingResponseFromOpenAi`、`BuildOpenAiStreamingResponseFromAnthropic`），内部用 `ExtractOpenAiStreamingText/Metadata`、`ExtractAnthropicStreamingText/Metadata` 重组
3. **非流式 → 流式重放**：`BuildAnthropicStreamFromOpenAiResponse` 把完整 JSON 响应一次性重放为 Anthropic 事件序列（兼容 stream=true 却返回完整对象的伪流式上游）

**惰性启动**（防空响应阻断 fallback）：所有 →Anthropic / →Responses 的流式转换都延迟到首个非空事件才发 `message_start` / `response.created`（`BuildAnthropicStreamStart`、`EnsureResponseStarted`/`EnsureMessageStarted`）。

**收尾补齐**：`CompleteAnthropicStream`（补 content_block_stop、message_delta(usage)、message_stop）；`EnsureAnthropicStreamClosed`（上游缺 message_stop 时补终止事件）。

**防御性设计**：`content:null`、`usage:null`、空 choices/output、role-only/usage-only 分片不构造空响应；工具调用 index/id/arguments 跨分片稳定累积；完成事件去重；转换失败时不混入原协议 SSE。

---

## 4. <a id="usage"></a>usage / token 统计语义（核心设计点）

**内部记账不变式：`InputTokens`（新输入，不含缓存）+ `CachedTokens`（缓存）+ `OutputTokens` = 总 token，绝不重复统计。** 落库 `ProxyUsageLog` 与使用日志页均用此口径。

**基准提取函数 `ExtractUsageFromElement(JsonElement usage, string protocolType)`**（Helpers.cs:1320，本项目唯一公开辅助，被 Infrastructure 复用以保证口径统一）：
- Anthropic：`input_tokens` 天然**包含**缓存子集（`cache_read_input_tokens` + `cache_creation_input_tokens`），返回 `新输入 = max(0, input − (cache_read + cache_creation))`，缓存 = 两者之和
- OpenAI/Responses：`input_tokens` 优先、缺失回退 `prompt_tokens`；缓存字段兼容 `input_tokens_details.cached_tokens` 与 `prompt_tokens_details.cached_tokens`（newapi 等中间层 details 为 null 时回退）；`output_tokens` 为 0 时回退 `completion_tokens`；同样返回**不含缓存的新输入**
- 缓存大于输入的异常数据下限为 0，不出负数

**跨协议还原语义**（转出方向）：
- 转成 **Anthropic** 输出：统一 `input_tokens = 新输入`（**不含**缓存），`cache_read_input_tokens` / `cache_creation_input_tokens` 独立成桶，三桶相加 = 总输入——与官方语义一致（cookbook 实例 input=4、cache_read=2051），流式与非流式同口径——见 `BuildAnthropicResponseFromOpenAi`(L396)、`CompleteAnthropicStream`(L234)、`BuildAnthropicStreamFromOpenAiResponse`、`BuildAnthropicStreamingResponseFromOpenAi`。按官方加法公式计费的客户端（claude-code/ccusage 等）总输入精确；`message_start` 与 `message_delta` 使用同一数值，回退入参（已是新输入）不再二次扣减缓存。
- 转成 **OpenAI** 输出：上游缺 `input_tokens` 时 `prompt_tokens = 新输入 + cached`（OpenAI 语义 prompt 含缓存命中），并带 `prompt_tokens_details.cached_tokens`——见 `ConvertResponsesResponseToChat`(L1504)、`ConvertResponsesStreamingToChat`(L2140)
- Anthropic → OpenAI：`BuildOpenAiResponseFromAnthropic`(L552)：Anthropic input 已含缓存，直接映射 prompt_tokens
- ⚠️ 入口侧已知歧义：`ExtractUsageFromElement` 的 Anthropic 分支按"`input_tokens` 含缓存"口径做减法（`新输入 = input − cache`，见上文）。该口径对 newapi 类中间层（把 OpenAI prompt_tokens 语义带进 Anthropic 格式）成立；对官方 Anthropic 上游（官方为三桶加法、input 不含缓存）则会低估。是否需要按上游类型区分口径，待实际流量验证后再定。

**流式累计覆盖语义**（防 newapi 类中间层重复累计）：`message_start` 与 `message_delta` 都带 usage 时按**覆盖**而非累加处理（`ConvertOpenAiStreamChunkToAnthropic` L53-80 只在 `usage` 为 JSON 对象时提取，`cached` 用 `>0` 守卫防 0 覆盖外部值；Anthropic 透传的 `UpdateAnthropicUsageFromPayload` 同口径）。

**思维链字段兼容**：Anthropic→Responses 的 `thinking_delta` 正文读 `delta.thinking`（个别把 thinking 放 `text` 的中间层兜底）；Chat→Responses 的 reasoning 增量用 `ExtractReasoningFromElement` 统一兼容 `reasoning_content`/`reasoning`/`thinking` 三种字段。

**内容块重开**：OpenAI→Anthropic 流式状态机中，工具调用关闭 thinking/text 块后若上游再出现同类增量（GLM/DeepSeek 分段思考、工具后尾文本），会分配 `NextContentIndex++` 新索引重开块，而不是对已 `content_block_stop` 的索引继续发 delta（违反 Anthropic 事件序会导致客户端报 unknown block index）。

**usage:null 防御**：所有从分片提取 usage 的位置都判 `ValueKind == Object`（OpenAI 开 `include_usage` 后常规分片带 `"usage": null`，对 null 调 TryGetProperty 会抛异常导致该分片后续字段全部丢失）；`cached_creation_tokens` 等数值字段同时判 `Number`。

历史数据修正 SQL 见 `docs/usage-token语义修复SQL.md`。

---

## 5. keep_reasoning 规则（思维链保留）

**场景**：deepseek 等上游在 thinking 模式 + 工具调用时要求把上一轮 assistant 思维链以 `reasoning_content` 回传（否则 400）；标准 OpenAI 不认该字段，所以**只在绑定 `op=keep_reasoning` 规则时保留**。

**生效链路**：
1. `PrepareRequestBody`（Core.cs L163-169）：仅在**跨协议转换分支**（Anthropic→OpenAI/Responses）检查规则；`scope` 仅 `all`/`bridge` 生效（与规则引擎口径一致）；**在转换前检查**，避免 thinking 已丢失
2. 传入 `BuildOpenAiRequestFromAnthropic(keepReasoning: true)`（RequestConvert.cs L15）
3. `ParseAnthropicContentBlocks`（Helpers.cs L46-69）提取 assistant `thinking` block 文本（兼容 `thinking`/`text`/`content` 三种字段写法，多块拼接），keepReasoning 时写入 OpenAI 消息的 `reasoning_content`；默认丢弃

**反向兼容**：`BuildAnthropicRequestFromOpenAi`(L351-359) 把 `reasoning_content` 转回 `thinking` block；`BuildOpenAiResponseFromAnthropic`(L567)、`BuildOpenAiStreamingResponseFromAnthropic`(L782) 把 thinking 重组为 `reasoning_content` 输出。

**thinking 字段类型防御**：`keep_reasoning` 规则应用时对 `thinking` 字段做 scope 过滤与类型防御（非字符串形态不注入）。

---

## 6. 兼容规则引擎（ApplyCompatibilityProfile，Core.cs:340）

规则来自模型绑定的 `CompatibilityProfile`（`RulesJson` 反序列化为 `List<CompatibilityRule>`）：

| Op | 字段 | 语义 |
|----|------|------|
| `strip` | `Target` | 剔除字段。顶层字段直接写名字（如 `metadata`）；裸字段名自动当作 `messages[].字段名`（如 `reasoning_content`）；也可写精确路径 `a.b` / `a[].b` |
| `rename` | `From`/`To` | 重命名顶层字段 |
| `default` | `Key`/`Value` | 为缺失的顶层字段补默认值（按 true/false/数字/字符串推断类型） |
| `keep_reasoning` | — | 见第 5 节（转换前检查，不走通用规则引擎路径） |

`Scope`（`passthrough`/`bridge`/`all`）按当前转发路径筛选：透传路径应用 `passthrough`+`all`，桥接路径应用 `bridge`+`all`。规则应用是 `PrepareRequestBody` 的**最后一步**（跨协议分支在转换后应用；keep_reasoning 例外在转换前）。

---

## 7. Codex 上游规范化

- `IsCodexTarget(targetBaseUrl)`（Core.cs:281）：判定是否 `chatgpt.com/backend-api` 上游
- `NormalizeResponsesBody(requestBody, isCodex)`（Core.cs:299）：`store=false` 兜底；Codex 目标剔除 **`CodexUnsupportedParameters`**（Core.cs:527，公开常量，12 个字段：metadata/temperature/top_p/max_output_tokens/max_completion_tokens/truncation/user/previous_response_id/prompt_cache_retention/safety_identifier/stream_options/context_management，与参考实现 CLIProxyAPI 的 `codex_openai-responses_request.go` DeleteBytes 列表人工同步）+ 强制 `stream=true`
- 非流式 Codex 请求由 `ProxyForwardService.ForwardAsync` 透明聚合（`TryExtractResponsesCompletion`，见 [proxy-pipeline.md](proxy-pipeline.md#6-阶段三b非流式转发)）——**代码中不存在 `CodexNonStreamingBridge` 类**（老版 README 笔误，该职责已内联进 ForwardAsync）

---

## 8. SSE 解析器与写入器

Protocol 项目不做网络 IO，网络层逐行读取在 `ProxyForwardService.ProcessStreamingResponseAsync`（`StreamReader.ReadLineAsync` + `onSseDataAsync` 回调），控制器攒行成事件块再喂给转换器。

**写入器**（生成 SSE 文本）：
- `AppendSseEvent`（Helpers.cs:651）：`event: xxx\ndata: {...}\n\n`（Anthropic 流）
- `BuildResponsesEvent` 两重载（Responses.cs:2605/2650）：按事件类型把 payload 包进 `response`/`item`/`part` 字段或裸 `delta` 字符串
- `BuildChatCompletionChunk`（Responses.cs:2287）：`data: {chat.completion.chunk}\n\n`
- `AppendOpenAiChunk` / `AppendOpenAiChunkWithToolCalls`（ResponseConvert.cs:847/872）

**解析器**（消费 SSE 文本）：
- `ExtractOpenAiStreamingText/Metadata`：按行切 + `data: ` 前缀 + `[DONE]` 跳过
- `ExtractAnthropicStreamingText/Metadata` + `ProcessAnthropicSseBlock`/`ProcessAnthropicMetadataBlock`：按空行分块的 `event:`/`data:` 状态机
- `ConvertResponsesStreamingToChat(state)` 内嵌 `StringReader` + `FlushEvent()` 闭包（event:/data:/空行三态）
- `ConvertChatCompletionSseToCompletionsSse`：归一 `\r\n`→`\n` 后按 `\n\n` 切块

---

## 9. 完整调用路径示例（最复杂路径）

**Anthropic 客户端（claude-code）流式请求 → Responses 协议上游（Codex）**：

1. `AnthropicProxyController.Messages`（L140）校验 AccessKey、选路由
2. `PrepareRequestBody("Anthropic","Responses",...)`：`BuildOpenAiRequestFromAnthropic` → `ConvertChatRequestToResponses` → `ApplyReasoningEffort` → `NormalizeResponsesBody`(Codex 剔 12 字段) → `ApplyCompatibilityProfile`
3. `ForwardOpenAiStreamAsAnthropicAsync`（L624）创建双状态 `AnthropicOpenAiStreamState` + `ResponsesToChatStreamState`
4. `ProxyForwardService.ForwardStreamingAsync` 逐行回调 → `FlushOpenAiSseBlockAsync`（L661）：
   - **第一重**：`ConvertResponsesStreamingToChat(event块, responsesToChatState)` — Responses 事件 → OpenAI chunk（`response.output_text.delta`→content、`response.reasoning_summary_text.delta`→reasoning_content、`response.output_item.added(function_call)`→tool_calls、`response.completed`→finish+usage+`[DONE]`）
   - **第二重**：每个 OpenAI JSON 分片 → `ConvertOpenAiStreamChunkToAnthropic(json, state)` — content/reasoning/tool_call → Anthropic `content_block_*` 事件
5. 惰性首写 `BuildAnthropicStreamStart` → `WriteChunkAsync` 写客户端并 Flush（累积 ≤64KB 诊断副本）
6. 流结束 `CompleteAnthropicStream(state)` 补齐块与 usage 还原；`ConversionFailed && !startedWriting` → 置失败保留 fallback；成功记录 UsageLog / DeveloperTrace

---

## 10. 公开 API 速查

| 类别 | 方法（均 static） |
|------|-------------------|
| 入口 | `PrepareRequestBody`、`AdaptResponseBodyForClient`、`ApplyCompatibilityProfile`、`NormalizeResponsesBody`、`IsCodexTarget`、`OverrideReasoningEffort`（兼容壳） |
| 请求转换 | `BuildOpenAiRequestFromAnthropic`*、`BuildAnthropicRequestFromOpenAi`*、`ConvertResponsesRequestToChat`、`ConvertChatRequestToResponses` |
| 非流式响应 | `BuildAnthropicResponseFromOpenAi`*、`BuildOpenAiResponseFromAnthropic`*、`ConvertChatResponseToResponses`、`ConvertAnthropicResponseToResponses`、`ConvertResponsesResponseToChat` |
| 流式 → Anthropic | `BuildAnthropicStreamStart`、`ConvertOpenAiStreamChunkToAnthropic`、`CompleteAnthropicStream`、`EnsureAnthropicStreamClosed`、`BuildAnthropicStreamFromOpenAiResponse`、`BuildAnthropicStreamingResponseFromOpenAi`* |
| 流式 → OpenAI | `BuildOpenAiStreamingResponseFromAnthropic`*、`ConvertResponsesStreamingToChat`（两重载） |
| 流式 → Responses | `ConvertChatStreamChunkToResponses`、`ConvertAnthropicStreamChunkToResponses` |
| legacy Completions | `ConvertCompletionsRequestToChat`、`ConvertChatResponseToCompletions`、`ConvertChatCompletionSseToCompletionsSse`、`ConvertChatStreamChunkToCompletions` |
| 请求探测 | `ExtractResponsesModel`、`ExtractResponsesStream`、`ExtractResponsesReasoningEffort` |
| usage | `ExtractUsageFromElement` |
| 常量 | `CodexUnsupportedParameters` |

（带 `*` 的为 private/内部实现，列入表中便于按名索骥；其余 public 可直接调用。）
