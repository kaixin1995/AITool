# Core / Admin 拆分通信协议清单

## 文档目的

本文档定义 Core 与 Admin 拆分后的通信协议草案，用于指导后续实现。

设计目标：

- Core 不依赖当前业务数据库
- Admin 是配置权威源
- Core 通过版本化配置快照运行
- Core 把运行事件持续推送给 Admin
- Admin 离线期间，Core 本地 spool
- Admin 恢复后按 sequence 补传
- Admin 重启后如果配置无变化，则不触发 Core 重新切换
- 配置更新必须可追踪、可比较、可回放、可幂等

---

# 一、协议分层

Core 与 Admin 间通信分成两层：

## 控制协议

用于：

- Admin 与 Core 握手
- 查询 Core 当前状态
- 下发全量配置
- 下发增量 patch
- 要求 Core 强制重载
- 获取当前活跃请求数、排空状态等

建议走：

- HTTP JSON API
- 或 gRPC request/response

---

## 事件协议

用于：

- Core 向 Admin 推送 UsageLog、Conversation、Trace、Detection 等事件
- Admin ack
- Core 补传积压事件
- 实时流与 replay 流切换

建议走：

- gRPC Streaming
- 或 WebSocket

---

# 二、统一约定

## 2.1 时间格式

统一使用：

- ISO 8601
- UTC
- 示例：`2026-06-08T10:22:33.456Z`

---

## 2.2 标识符格式

- `Guid` 一律用字符串形式
- `ConfigVersion` 使用 `long`
- `SequenceId` 使用 `long`

---

## 2.3 幂等原则

以下操作必须幂等：

- 全量配置同步
- 增量 patch 应用
- 事件 replay 补传
- ack 重复提交

---

## 2.4 版本比较规则

- `ConfigVersion` 单调递增
- `SequenceId` 单调递增
- 若版本逆行或 sequence 乱序，必须进入异常处理分支

---

# 三、RuntimeConfigSnapshot

这是 Core 运行时真正使用的完整配置快照。

## 3.1 顶层结构

```json
{
  "configVersion": 27,
  "configHash": "sha256:3C4E...A9",
  "generatedAt": "2026-06-08T10:22:33.456Z",
  "payload": {
    "sites": [],
    "models": [],
    "siteModelMappings": [],
    "routeEntries": [],
    "routeRules": [],
    "accessKeys": [],
    "runtimeSettings": {}
  }
}
```

---

## 3.2 sites

```json
{
  "id": "11111111-1111-1111-1111-111111111111",
  "name": "Primary OpenAI",
  "baseUrl": "https://api.example.com",
  "endpointPathMode": "standard-root",
  "apiKey": "sk-xxx",
  "protocolType": "OpenAI",
  "supportsOpenAi": true,
  "supportsAnthropic": false,
  "isEnabled": true
}
```

### 说明

- Core 不需要完整 UI 元信息，只保留运行时必要字段
- `apiKey` 是敏感字段，仅控制通道内传输

---

## 3.3 models

```json
{
  "id": "22222222-2222-2222-2222-222222222222",
  "modelName": "gpt-5.4",
  "displayName": "GPT-5.4",
  "isEnabled": true
}
```

---

## 3.4 siteModelMappings

```json
{
  "id": "33333333-3333-3333-3333-333333333333",
  "siteId": "11111111-1111-1111-1111-111111111111",
  "modelLibraryItemId": "22222222-2222-2222-2222-222222222222",
  "remoteModelName": "gpt-5.4",
  "lastStatus": "success",
  "isEnabled": true,
  "maxConcurrency": 8
}
```

---

## 3.5 routeEntries

```json
{
  "id": "44444444-4444-4444-4444-444444444444",
  "entryName": "chat-prod"
}
```

---

## 3.6 routeRules

```json
{
  "id": "55555555-5555-5555-5555-555555555555",
  "externalModelName": "chat-prod",
  "upstreamModelName": "gpt-5.4",
  "siteId": "11111111-1111-1111-1111-111111111111",
  "siteModelName": "gpt-5.4",
  "priority": 0,
  "modelPriority": 0,
  "instancePriority": 0,
  "isEnabled": true,
  "availabilityMode": "AllDay",
  "timeRangesJson": ""
}
```

---

## 3.7 accessKeys

```json
{
  "id": "66666666-6666-6666-6666-666666666666",
  "keyName": "prod-key",
  "plainKey": "sk-prod-xxx",
  "accessKeyHash": "ABCDEF...",
  "maskedValue": "sk-***",
  "isEnabled": true
}
```

### 说明

Core 运行时校验可以用：

- `accessKeyHash`
- 或 `plainKey`

建议最终只保留运行时必要形式，减少敏感信息暴露面。

---

## 3.8 runtimeSettings

这里只放 Core 真正运行时需要的字段：

```json
{
  "proxyRequestTimeoutSeconds": 60,
  "proxyRetryCount": 1,
  "circuitBreakerFailureThreshold": 5,
  "circuitBreakerRecoveryMinutes": 2,
  "concurrencyMode": 0,
  "concurrencyQueueTimeoutSeconds": 120,
  "conversationLogEnabled": true
}
```

### 注意

像：

- UsageLogs 清理天数
- Analytics 页面相关设置

这些不应放进 Core 快照。

---

# 四、ConfigHash 计算规则

建议：

- Admin 对 `payload` 做稳定序列化
- 再计算 SHA-256
- 最终格式：

```text
sha256:<HEX>
```

例如：

```text
sha256:3C4E7AA2...
```

### 规则要求

- 数组顺序必须固定
- 属性顺序必须稳定
- 序列化不能受环境差异影响

否则同一配置会算出不同 hash。

---

# 五、RuntimeConfigPatch

用于高频小改动场景。

## 5.1 顶层结构

```json
{
  "configVersion": 28,
  "baseVersion": 27,
  "configHash": "sha256:9F21...D1",
  "generatedAt": "2026-06-08T10:30:12.000Z",
  "changes": {
    "sites": {
      "added": [],
      "updated": [],
      "removed": []
    },
    "routeRules": {
      "added": [],
      "updated": [],
      "removed": []
    },
    "runtimeSettings": {
      "updated": {
        "proxyRequestTimeoutSeconds": 75
      }
    }
  }
}
```

---

## 5.2 removed 的表达方式

建议：

- 资源类集合使用 ID 数组
- 不直接传整对象

例如：

```json
"removed": [
  "55555555-5555-5555-5555-555555555555"
]
```

---

## 5.3 patch 应用规则

Core 处理 patch 时：

- 如果 `baseVersion != currentVersion` → 拒绝
- 在当前快照副本上应用
- 应用完成后重算 hash
- 与 `configHash` 比较
- 一致才允许切换

---

# 六、握手协议

Admin 重连 Core 时先握手。

## 6.1 HandshakeRequest

```json
{
  "adminInstanceId": "admin-node-01",
  "adminStartedAt": "2026-06-08T10:40:00.000Z",
  "currentConfigVersion": 28,
  "currentConfigHash": "sha256:9F21...D1",
  "lastAckedSequenceId": 102345
}
```

---

## 6.2 HandshakeResponse

```json
{
  "coreInstanceId": "core-node-01",
  "coreStartedAt": "2026-06-08T09:00:00.000Z",
  "appliedConfigVersion": 27,
  "appliedConfigHash": "sha256:3C4E...A9",
  "latestSequenceId": 102980,
  "activeRequestCount": 4,
  "state": "ready",
  "spoolStatus": {
    "hasBacklog": true,
    "oldestSequenceId": 102346,
    "newestSequenceId": 102980,
    "fileCount": 3,
    "approxBytes": 8234112
  }
}
```

---

## 6.3 握手后的决策规则

### 情况一：版本和 hash 都相同

- 不同步配置
- 直接进入补传阶段

### 情况二：版本不同

- 尝试增量 patch 或全量同步

### 情况三：版本相同但 hash 不同

- 判定为漂移
- 强制全量同步

---

# 七、Core 状态接口响应模型

## 7.1 `/api/core/config/status`

```json
{
  "configVersion": 27,
  "configHash": "sha256:3C4E...A9",
  "generatedAt": "2026-06-08T10:22:33.456Z",
  "hasLastGoodConfig": true
}
```

---

## 7.2 `/api/core/runtime/status`

```json
{
  "state": "ready",
  "activeRequestCount": 4,
  "isDraining": false,
  "adminConnected": true,
  "latestSequenceId": 102980,
  "lastAckedSequenceId": 102345
}
```

---

## 7.3 `/api/core/health`

```json
{
  "status": "ok"
}
```

---

## 7.4 `/api/core/ready`

```json
{
  "ready": true,
  "reason": ""
}
```

如果没有配置：

```json
{
  "ready": false,
  "reason": "No runtime config snapshot loaded"
}
```

---

# 八、事件 Envelope

所有 Core → Admin 的事件统一封装成一个结构。

## 8.1 EventEnvelope

```json
{
  "sequenceId": 102981,
  "eventType": "usage-log",
  "occurredAt": "2026-06-08T10:41:22.123Z",
  "payload": {}
}
```

### 字段说明

- `sequenceId`：全局单调递增
- `eventType`：事件类型
- `occurredAt`：事件发生时间
- `payload`：具体事件内容

---

# 九、事件类型建议

## 9.1 UsageLogEvent

```json
{
  "requestId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "sourceTool": "claude-code",
  "sessionId": "session-123",
  "conversationGroupKey": "claude-code:session-123",
  "protocolType": "OpenAI",
  "forwardingMode": "direct",
  "requestModel": "chat-prod",
  "attemptedModel": "gpt-5.4",
  "targetSiteId": "11111111-1111-1111-1111-111111111111",
  "targetSiteName": "Primary OpenAI",
  "siteModelName": "gpt-5.4",
  "status": "success",
  "source": "claude-code",
  "retryCount": 1,
  "attemptIndex": 1,
  "isFinalResult": true,
  "fallbackTriggered": false,
  "errorMessage": "",
  "inputTokens": 1200,
  "cachedTokens": 800,
  "outputTokens": 500,
  "totalTokens": 2500,
  "isStreaming": true,
  "isStreamInterrupted": false,
  "firstTokenLatencyMs": 320,
  "streamDurationMs": 2800,
  "totalDurationMs": 3200,
  "reasoningEffort": "high",
  "requestedAt": "2026-06-08T10:41:22.123Z"
}
```

---

## 9.2 ConversationTurnEvent

```json
{
  "requestId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "createdAt": "2026-06-08T10:42:11.000Z",
  "userCreatedAt": "2026-06-08T10:42:08.000Z",
  "sourceTool": "claude-code",
  "sessionId": "session-123",
  "conversationGroupKey": "claude-code:session-123",
  "accessKeyId": "66666666-6666-6666-6666-666666666666",
  "requestModel": "claude-sonnet-4-6",
  "protocolType": "OpenAI",
  "requestPath": "/v1/responses",
  "source": "claude-code",
  "userInputText": "gzip:....",
  "assistantOutputMarkdown": "gzip:....",
  "inputTokens": 123,
  "cachedTokens": 0,
  "outputTokens": 456,
  "isStreaming": true,
  "status": "success",
  "metadataJson": "{\"userAgent\":\"...\"}",
  "conversationTitle": ""
}
```

### 注意

这里可以直接沿用你现有 `ConversationTurnLog` 的字段形态，减少 Admin 入库改造成本。

---

## 9.3 DeveloperTraceEvent

建议至少带：

```json
{
  "traceId": "trace-001",
  "requestId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "protocolType": "OpenAI",
  "requestModel": "chat-prod",
  "attemptedModel": "gpt-5.4",
  "siteId": "11111111-1111-1111-1111-111111111111",
  "siteName": "Primary OpenAI",
  "siteModelName": "gpt-5.4",
  "forwardingMode": "direct",
  "status": "success",
  "startedAt": "2026-06-08T10:43:00.000Z",
  "finishedAt": "2026-06-08T10:43:03.000Z",
  "errorMessage": "",
  "requestPreview": "...",
  "responsePreview": "..."
}
```

---

## 9.4 DetectionResultEvent

```json
{
  "taskId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
  "taskName": "nightly-probe",
  "modelId": "22222222-2222-2222-2222-222222222222",
  "modelName": "gpt-5.4",
  "siteId": "11111111-1111-1111-1111-111111111111",
  "siteName": "Primary OpenAI",
  "remoteModelName": "gpt-5.4",
  "status": "success",
  "durationMs": 1200,
  "errorMessage": "",
  "executedAt": "2026-06-08T10:44:11.000Z"
}
```

---

## 9.5 RouteFallbackEvent

```json
{
  "requestId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
  "requestModel": "chat-prod",
  "fromRouteId": "route-1",
  "fromSiteId": "site-1",
  "fromSiteModelName": "gpt-5.4",
  "toRouteId": "route-2",
  "toSiteId": "site-2",
  "toSiteModelName": "glm-5.1",
  "reason": "upstream timeout",
  "occurredAt": "2026-06-08T10:45:00.000Z"
}
```

---

# 十、Ack 协议

## 10.1 AckEnvelope

```json
{
  "adminInstanceId": "admin-node-01",
  "ackedSequenceId": 102981,
  "ackedAt": "2026-06-08T10:45:10.000Z"
}
```

### 规则

- Core 只认最大的连续 ack
- 小于等于当前已确认序号的重复 ack 直接忽略
- ack 必须在 Admin 成功落库后才发

---

# 十一、Replay 协议

## 11.1 ReplayRequest

```json
{
  "fromSequenceId": 102346,
  "adminInstanceId": "admin-node-01"
}
```

含义：

- Admin 已确认到 `102345`
- 现在要求从 `102346` 开始补

---

## 11.2 ReplayResponse（流式）

返回的每个消息仍是 `EventEnvelope`。

补传完成后，可附加一个标记消息：

```json
{
  "sequenceId": 102980,
  "eventType": "replay-complete",
  "occurredAt": "2026-06-08T10:46:00.000Z",
  "payload": {
    "lastReplayedSequenceId": 102980
  }
}
```

---

# 十二、Admin 入库批处理约定

Admin 接收事件后，不逐条同步写库，建议批量处理。

## 12.1 IngestBatch 逻辑模型

```json
{
  "batchId": "batch-20260608-001",
  "firstSequenceId": 102346,
  "lastSequenceId": 102380,
  "count": 35
}
```

### 行为建议

- 内存缓冲达到 N 条或 T 秒后批量入库
- 入库成功后统一 ack 到最大连续 `sequenceId`
- 入库失败则不 ack

---

# 十三、错误码建议

控制协议建议统一错误结构：

```json
{
  "code": "CONFIG_BASE_VERSION_MISMATCH",
  "message": "Base version 26 does not match current version 27"
}
```

建议错误码：

- `CONFIG_NOT_READY`
- `CONFIG_HASH_MISMATCH`
- `CONFIG_BASE_VERSION_MISMATCH`
- `CONFIG_VALIDATION_FAILED`
- `REPLAY_SEQUENCE_TOO_OLD`
- `REPLAY_SEQUENCE_NOT_AVAILABLE`
- `ADMIN_NOT_AUTHORIZED`
- `CORE_NOT_READY`

---

# 十四、状态迁移约定

## Admin 重启恢复顺序

1. 握手
2. 配置比对
3. 无变化则跳过；有变化则 patch / full sync
4. Replay 补传
5. 切换到实时流

## Core 配置切换原则

- 候选副本构建成功前，不影响当前快照
- hash 校验通过前，不允许切换
- 切换后新请求用新快照，旧请求继续用旧快照引用

---

# 十五、兼容现有数据库结构的建议

为了减少第一阶段改动：

- `ConversationTurnEvent` 可尽量复用现有 `ConversationTurnLog` 字段
- `UsageLogEvent` 可尽量复用现有 `ProxyUsageLog` 字段
- `DetectionResultEvent` 可尽量兼容现有 Detection 执行记录结构
- Admin 收到事件后可以直接映射到当前表结构，减少迁移成本

---

# 十六、第一阶段实现建议

第一版协议实现建议优先级：

## 先做

- Handshake
- FullConfigSnapshot
- EventEnvelope
- AckEnvelope
- ReplayRequest
- UsageLogEvent
- ConversationTurnEvent

## 后做

- ConfigPatch
- DeveloperTraceEvent
- DetectionResultEvent
- RouteFallbackEvent
- replay-complete 标记事件
