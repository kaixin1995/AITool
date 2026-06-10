# Admin 门面服务覆盖面扩展设计（方案 B）

## 背景

当前 Admin 页面和控制器中仍有大量对运行时对象（`ProxyRequestMetadataCache`、`ModelConcurrencyLimiter`）的直接依赖。虽然之前已经创建了 `AdminCacheInvalidationService`、`AdminQueryMetadataService`、`ModelConcurrencyQueryService` 等门面，但覆盖面还不够——Models 页面、Sites 页面、RouteRulesApiController 等仍直接握着运行时缓存对象。

## 目标

在不破坏 Core 代理主链路的前提下，把 Admin 侧对运行时对象的直接依赖进一步剥离，使 Admin 页面/控制器只通过门面服务与运行时交互。

## 范围

### 本轮处理（9 个文件中 8 个）

- 5 个 Models/Sites 页面：纯写侧缓存失效替换
- RouteRulesApiController：混合依赖剥离
- ModelsApiController：运行时写操作门面化
- Developer/Invocations 页面：常量引用迁移

### 暂不处理

- ChatApiController：深度运行时代理执行耦合，需要根本性设计决策

## 设计

### 段 1：Models/Sites 页面纯替换

5 个文件统一改动模式：

1. 将 `ProxyRequestMetadataCache? _metadataCache` 字段 + `[ActivatorUtilitiesConstructor]` 注入，替换为 `AdminCacheInvalidationService _cacheInvalidation`（non-null）
2. 所有 `_metadataCache?.InvalidateXxx()` 调用改为 `_cacheInvalidation.InvalidateXxx()`（去掉 `?.`）
3. 保留另一个接收 `AppDbContext` 的构造函数不变（用于测试场景）

受影响文件及调用点：

| 文件 | PageModel | 调用 |
|------|-----------|------|
| `Pages/Admin/Models/Index.cshtml.cs` | IndexModel | OnPostToggleAsync: InvalidateModelMetadata + InvalidateRouteTargets |
| | IndexModel | OnPostDeleteAsync: InvalidateModelMetadata + InvalidateRouteTargets |
| | CreateModelModel | OnPostAsync: InvalidateModelMetadata + InvalidateRouteTargets |
| `Pages/Admin/Models/Edit.cshtml.cs` | EditModel | OnPostAsync: InvalidateModelMetadata + InvalidateRouteTargets |
| | EditModel | OnPostAddMappingAsync: InvalidateModelMetadata + InvalidateRouteTargets |
| | EditModel | OnPostDeleteMappingAsync: InvalidateModelMetadata + InvalidateRouteTargets |
| `Pages/Admin/Sites/Index.cshtml.cs` | IndexModel | OnPostToggleAsync: InvalidateRouteTargets |
| | IndexModel | OnPostBulkDeleteAsync: InvalidateRouteTargets |
| | IndexModel | OnPostDeleteAsync: InvalidateRouteTargets |
| | CreateModel | OnPostAsync: InvalidateRouteTargets |
| `Pages/Admin/Sites/Edit.cshtml.cs` | EditModel | OnPostAsync: InvalidateRouteTargets |
| `Pages/Admin/Sites/Import.cshtml.cs` | ImportModel | OnPostAsync: InvalidateRouteTargets |

共 14 处调用，全部替换为 `AdminCacheInvalidationService` 上对应的方法。

### 段 2：新增 AdminConcurrencyControlService

创建 `Services/AdminConcurrencyControlService.cs`，封装 `ModelConcurrencyLimiter` 上的运行时写操作：

- `TryDeferRuntimeRouteTargetsRefresh(entryName, affectedRouteTargets, previousRouteTargets) → bool`
- `UpdateLimit(siteId, remoteModelName, maxConcurrency) → void`

该门面封装极薄（各 1 行转发），目的是让 Admin 侧不再直接知道 `ModelConcurrencyLimiter` 的内部方法签名。

### 段 3：RouteRulesApiController 混合依赖剥离

改动：

1. `ProxyRequestMetadataCache _metadataCache` → `AdminCacheInvalidationService _cacheInvalidation`
2. `ModelConcurrencyLimiter _concurrencyLimiter` → `AdminConcurrencyControlService _concurrencyControl`
3. 6 处 `_metadataCache.InvalidateXxx()` → `_cacheInvalidation.InvalidateXxx()`
4. `_concurrencyLimiter.TryDeferRuntimeRouteTargetsRefresh(...)` → `_concurrencyControl.TryDeferRuntimeRouteTargetsRefresh(...)`

改造后该控制器不再直接依赖 `ProxyRequestMetadataCache` 和 `ModelConcurrencyLimiter`。

### 段 4：ModelsApiController 运行时写门面化

改动：

1. `ModelConcurrencyLimiter _concurrencyLimiter` → `AdminConcurrencyControlService _concurrencyControl`
2. `_concurrencyLimiter.UpdateLimit(...)` → `_concurrencyControl.UpdateLimit(...)`
3. `AdminCacheInvalidationService` 保持不变（已在用）

改造后该控制器不再直接依赖 `ModelConcurrencyLimiter`。

### 段 5：Developer Invocations 常量引用迁移

改动：

1. `ModelConcurrencyQueryService` 新增属性 `TimeSpan RecentRetention`，返回 `_concurrencyLimiter.RecentRetention`
2. `Developer/Invocations/Index.cshtml.cs` 第 224 行改为从 `_concurrencyQuery.RecentRetention` 读取
3. 断开对 `ModelConcurrencyLimiter` 类型的 using 引用

## DI 注册

`Program.cs` 中新增：

```csharp
builder.Services.AddSingleton<AdminConcurrencyControlService>();
```

## 验证策略

- 每段完成后运行最相关的集成测试
- 全部完成后运行完整构建和更广泛的测试集
- 重点验证 Models/Sites/Routes 相关页面操作后缓存失效仍正常

## 改后依赖状态

| 文件 | 改前 | 改后 |
|------|------|------|
| Models/Index.cshtml.cs | ProxyRequestMetadataCache? | AdminCacheInvalidationService |
| Models/Edit.cshtml.cs | ProxyRequestMetadataCache? | AdminCacheInvalidationService |
| Sites/Index.cshtml.cs | ProxyRequestMetadataCache? | AdminCacheInvalidationService |
| Sites/Edit.cshtml.cs | ProxyRequestMetadataCache? | AdminCacheInvalidationService |
| Sites/Import.cshtml.cs | ProxyRequestMetadataCache? | AdminCacheInvalidationService |
| RouteRulesApiController | ProxyRequestMetadataCache + ModelConcurrencyLimiter | AdminCacheInvalidationService + AdminConcurrencyControlService |
| ModelsApiController | ModelConcurrencyLimiter | AdminConcurrencyControlService |
| Developer/Invocations/Index | ModelConcurrencyLimiter (常量) | ModelConcurrencyQueryService.RecentRetention |
| ChatApiController | ProxyRequestMetadataCache + ModelConcurrencyLimiter | **不动** |
