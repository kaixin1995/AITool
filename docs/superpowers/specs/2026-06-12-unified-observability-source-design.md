# 2026-06-12 统一拉取观测源设计

## 目标
将 `Admin/Developer/Invocations`、`Admin/UsageLogs`、`Admin/Chat#conversationLogPane` 三处页面的数据获取，收敛为 **Admin 侧一份统一拉取源**。

## 方案
- `Core` 继续作为运行时事实来源
- `Admin` 新增统一观测拉取服务，按一次拉取/一次缓存的方式获取统一观测快照
- 三个页面从统一观测快照中各取字段，不再分别命中三套独立远端获取路径

## 边界
- 不强行把现有 `ProxyUsageLogs`、`ConversationLogStore`、`DeveloperInvocationTrace` 立即物理合表
- 当前阶段统一的是 **获取源** 和 **Admin 侧读取入口**，不是一次性重写全部持久化结构
- 必须依赖运行时的事实仍由 `Core` 提供，Admin 只做统一拉取和页面裁剪

## 统一观测模型
新增一份 Admin 侧统一 DTO / snapshot，至少包含：
- 请求级字段：`RequestId`、`RequestedAt`、`Source`、`ProtocolType`、`RequestModel`
- 尝试级字段：`AttemptIndex`、`AttemptedModel`、`SiteName`、`SiteModelName`、`Status`、`ErrorMessage`
- 指标字段：`InputTokens`、`CachedTokens`、`OutputTokens`、`TotalTokens`、`FirstTokenLatencyMs`、`TotalDurationMs`
- 对话字段：`ConversationGroupKey`、`ConversationTitle`、`UserInputText`、`AssistantOutputText`
- 运行时字段：`IsStreaming`、`FallbackTriggered`、`IsFinalResult`

## 页面映射
- `Developer/Invocations`
  - 主要消费请求级 + 尝试级 + 运行时字段
- `UsageLogs`
  - 主要消费请求级 + 指标字段 + 尝试摘要字段
- `Chat#conversationLogPane`
  - 主要消费对话字段 + 请求时间 + 请求模型

## 实现步骤
1. 定义 Admin 侧统一观测快照模型
2. 定义统一观测读取服务接口
3. 第一阶段适配：由现有本地数据源拼装统一快照
   - `ProxyUsageLogs`
   - `ConversationLogStore`
   - `CoreAdminClient` developer endpoints（可用时）
4. 三个页面改为走统一读取服务
5. 保留旧底层存储，不影响现有写入链路

## 风险控制
- 若 `Core` developer endpoints 不可用，统一读取服务允许部分降级
- `UsageLogs` 与 `ConversationLogPane` 仍可从本地数据正常工作
- `Developer/Invocations` 在 `Core` 不可用时展示降级提示，但不再破坏其它页面

## 验证
- `Admin` 编译通过
- 三个页面路由正常加载
- `Core` 不可用时，`UsageLogs` 和 `ConversationLogPane` 仍可工作
- `Core` 可用时，`Developer/Invocations` 正常展示
