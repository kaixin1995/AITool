# Codex 新增功能性能分析报告

## 概述

本文档分析今天新增的三个功能的性能表现：
1. **真实手动重置额度**（reset credits）
2. **可选模型导入**（fetch + import）
3. **卡片 UI 渲染**

---

## 1. Reset Credits 功能性能分析

### 调用链路

```
用户点击小字 → 前端 openResetCreditsModal()
                ↓
GET /api/admin/codex/accounts/{id}/reset-credits
                ↓
CodexResetCreditsService.QueryResetCreditsAsync()
                ↓
GET https://chatgpt.com/backend-api/wham/rate-limit-reset-credits
                ↓
JSON 解析 + 过滤 available credits
                ↓
返回 { availableCount, credits[], success }
```

### 性能评估

#### ✅ 优点

1. **单次 HTTP 请求**：每次查询只调用一次上游 API
2. **数据过滤**：只返回 `status=available` 且 `reset_type=codex_rate_limits` 的 credit
3. **异步处理**：使用 `async/await`，不阻塞主线程
4. **错误处理完善**：catch 异常并返回友好错误信息

#### ❌ 性能问题

**问题 1：无缓存机制**
- 每次打开 modal 都调用上游 API
- Reset credits 数据变化频率低（通常几小时/几天才变一次）
- 重复打开 modal 会产生冗余请求

**影响**：
- 增加上游 API 负担
- modal 打开延迟（需等待网络请求）
- 用户体验稍差（加载中状态）

**问题 2：JSON 手动解析**
- 使用 `JsonDocument.Parse` + 手动遍历
- 每个字段都需要 `TryGetProperty`
- 代码冗长，易出错

**影响**：
- 代码可维护性差
- 性能略低于强类型反序列化
- 但这个问题影响很小（JSON 通常不大）

---

### 优化建议

#### 建议 1：添加内存缓存（推荐）

**方案**：使用 `IMemoryCache` 缓存 reset credits 数据

```csharp
public async Task<CodexResetCreditsInfo> QueryResetCreditsAsync(CodexAccount account, CancellationToken ct)
{
    var cacheKey = $"codex_reset_credits_{account.Id}";
    
    if (_cache.TryGetValue(cacheKey, out CodexResetCreditsInfo? cached))
    {
        return cached;
    }
    
    var info = await FetchFromUpstreamAsync(account, ct);
    
    if (info.Success)
    {
        _cache.Set(cacheKey, info, TimeSpan.FromMinutes(5)); // 缓存 5 分钟
    }
    
    return info;
}
```

**收益**：
- 重复打开 modal 瞬间响应
- 减少上游 API 压力
- 用户体验提升

**成本**：
- 极低（内存占用可忽略）
- 数据可能稍微滞后（5 分钟内）

---

#### 建议 2：改用强类型反序列化（可选）

**方案**：定义 DTO 类，使用 `System.Text.Json` 反序列化

```csharp
private record UpstreamResponse(
    [property: JsonPropertyName("available_count")] int AvailableCount,
    [property: JsonPropertyName("credits")] List<UpstreamCredit> Credits
);

private record UpstreamCredit(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reset_type")] string? ResetType,
    [property: JsonPropertyName("granted_at")] string? GrantedAt,
    [property: JsonPropertyName("expires_at")] string? ExpiresAt
);

// 使用
var response = await httpResponse.Content.ReadFromJsonAsync<UpstreamResponse>(ct);
```

**收益**：
- 代码更简洁
- 类型安全
- 性能略有提升

**成本**：
- 需要定义 DTO 类
- 上游字段变更时需同步修改

---

## 2. 模型导入功能性能分析

### 2.1 FetchModels（预览）性能

#### 调用链路

```
用户点击「拉取模型」→ 前端 openCodexModelModal()
                     ↓
GET /api/admin/codex/accounts/{id}/fetch-models
                     ↓
① _modelFetcher.FetchAsync()          // 上游 API 调用
② 查询 SiteModelMappings (by SiteId)   // 数据库查询 1
③ 查询 ModelLibraryItems (IN remoteNames) // 数据库查询 2
④ 内存 Join（foreach + FirstOrDefault）  // O(n²)
                     ↓
返回 [{ remoteModelName, displayName, existingMappingId, ... }]
```

#### 性能评估

##### ✅ 优点

1. **单次上游调用**：不重复请求
2. **避免 N+1 查询**：提前加载所有 mappings 和 modelItems
3. **有索引支持**：SiteId 和 ModelName 应该有索引

##### ❌ 性能问题

**问题：内存 Join 使用 O(n²) 算法**

```csharp
// Line 328-332
foreach (var remote in remoteModels)  // O(n)
{
    var mapping = existingMappings.FirstOrDefault(m => m.RemoteModelName == remote.Slug);  // O(n)
    var modelItem = modelItems.FirstOrDefault(m => m.ModelName == remote.Slug);            // O(n)
    // ...
}
```

**影响**：
- 当模型数量较多时（如 50+ 个模型），性能下降明显
- 100 个模型 × 100 次查找 = 10,000 次比较

---

### 优化建议

#### 建议 3：使用 Dictionary 优化 Join（强烈推荐）

**方案**：将 List 转为 Dictionary，O(n²) → O(n)

```csharp
// 优化前（O(n²)）
foreach (var remote in remoteModels)
{
    var mapping = existingMappings.FirstOrDefault(m => m.RemoteModelName == remote.Slug);
    var modelItem = modelItems.FirstOrDefault(m => m.ModelName == remote.Slug);
}

// 优化后（O(n)）
var mappingDict = existingMappings.ToDictionary(m => m.RemoteModelName);
var modelItemDict = modelItems.ToDictionary(m => m.ModelName);

foreach (var remote in remoteModels)
{
    mappingDict.TryGetValue(remote.Slug, out var mapping);
    modelItemDict.TryGetValue(remote.Slug, out var modelItem);
    var hasValidImport = mapping != null && modelItem != null && mapping.ModelLibraryItemId == modelItem.Id;
    
    result.Add(new
    {
        remoteModelName = remote.Slug,
        displayName = remote.DisplayName,
        existingMappingId = hasValidImport ? mapping.Id : (Guid?)null,
        isEnabled = hasValidImport && mapping.IsEnabled,
        existingDisplayName = modelItem?.DisplayName
    });
}
```

**收益**：
- 时间复杂度从 O(n²) 降为 O(n)
- 10 个模型：100 次比较 → 10 次
- 50 个模型：2,500 次比较 → 50 次
- 100 个模型：10,000 次比较 → 100 次

**成本**：
- 极低（只是多两行代码）
- 内存增加可忽略

---

### 2.2 ImportSelectedModels（导入）性能

#### 性能评估

##### ✅ 优点

1. **前端预过滤**：只传选中的模型，减少后端处理
2. **批量操作**：`UpsertRemoteModelsAsync` 批量处理
3. **无冗余查询**：直接调用 provisioner

##### ✅ 结论：导入端点性能已最优

---

## 3. 卡片 UI 渲染性能分析

### 渲染流程

```
loadAccounts() → GET /api/admin/codex/accounts
                           ↓
返回账号列表（含 quota 信息 + resetCreditsAvailableCount）
                           ↓
renderAccounts() → codexAccounts.map(renderCard)
                           ↓
renderCard() 为每个账号生成 HTML 字符串
                           ↓
innerHTML 一次性插入 DOM
```

### 性能评估

#### ✅ 优点

1. **批量渲染**：所有卡片一次性插入 DOM（不是逐个 append）
2. **字符串拼接**：使用模板字符串，性能优于 DOM 操作
3. **无不必要的重绘**：只在需要时重新渲染

#### ⚠️ 潜在问题

**问题：大量账号时可能卡顿**
- 如果有 100+ 个账号，`innerHTML` 会一次性解析大量 HTML
- 浏览器可能短暂卡顿

---

### 优化建议

#### 建议 4：虚拟滚动（仅在账号 > 100 时需要）

**方案**：只渲染可见区域的卡片

**收益**：
- 渲染时间从 O(n) 降为 O(1)
- 100 个账号 → 只渲染可见的 10 个

**成本**：
- 实现复杂度较高
- 需要库支持（如 `react-window`）

**建议**：
- 当前账号数量通常 < 20，**无需优化**
- 如果将来账号数 > 100，再考虑虚拟滚动

---

## 4. 账号列表 API 性能分析

### 推测实现

根据前端调用 `GET /api/admin/codex/accounts`，后端应该：
1. 查询所有 CodexAccounts
2. 解析每个账号的 `LastQuotaRawJson`，提取 `resetCreditsAvailableCount`
3. 返回账号列表

### 性能评估

#### ⚠️ 潜在问题

**问题：每次列表加载都解析 JSON**

如果账号列表 API 每次都解析 `LastQuotaRawJson`：
```csharp
// 推测代码
foreach (var account in accounts)
{
    var json = JsonDocument.Parse(account.LastQuotaRawJson);
    account.ResetCreditsAvailableCount = ExtractAvailableCount(json);
}
```

**影响**：
- 10 个账号 × 每次解析 JSON = 10 次解析
- JSON 解析是 CPU 密集型操作

---

### 优化建议

#### 建议 5：将 resetCreditsAvailableCount 存储到数据库字段（推荐）

**方案**：在 `CodexAccount` 表新增字段

```csharp
public sealed class CodexAccount
{
    // 现有字段...
    public string? LastQuotaRawJson { get; set; }
    
    // 新增字段
    public int ResetCreditsAvailableCount { get; set; }
    public DateTimeOffset? ResetCreditsLastSyncAt { get; set; }
}
```

**在刷新 quota 时同步更新**：
```csharp
// 在 CodexQuotaService 刷新 quota 后
account.LastQuotaRawJson = rawJson;
account.ResetCreditsAvailableCount = ExtractAvailableCount(rawJson);
account.ResetCreditsLastSyncAt = DateTimeOffset.UtcNow;
await _dbContext.UpdateAsync(account, ct);
```

**收益**：
- 列表加载无需解析 JSON
- 查询速度更快
- 可按 resetCreditsAvailableCount 排序/过滤

**成本**：
- 需要数据库迁移
- 数据可能略滞后（与 quota 刷新周期一致）

---

## 5. 综合性能评分

| 功能 | 当前性能 | 瓶颈 | 优先级 | 建议优化 |
|------|---------|------|--------|---------|
| Reset Credits 查询 | ⭐⭐⭐☆☆ | 无缓存，每次调上游 | 中 | 添加 5 分钟内存缓存 |
| 模型预览（FetchModels） | ⭐⭐⭐☆☆ | O(n²) 内存 Join | **高** | 改用 Dictionary（O(n)） |
| 模型导入（ImportSelected） | ⭐⭐⭐⭐⭐ | 无 | 低 | 无需优化 |
| 卡片 UI 渲染 | ⭐⭐⭐⭐☆ | 账号 > 100 时可能卡顿 | 低 | 暂不需要（账号数少） |
| 账号列表 API | ⭐⭐⭐☆☆ | 每次解析 JSON | 中 | 新增数据库字段缓存 |

**总体评分：⭐⭐⭐⭐☆（4/5 星）**

---

## 6. 优化优先级排序

### P0（强烈推荐，立即优化）

**✅ 建议 3：FetchModels 使用 Dictionary 优化 Join**
- **收益**：时间复杂度从 O(n²) 降为 O(n)
- **成本**：极低（只需改 2 行代码）
- **实施时间**：5 分钟

---

### P1（推荐，近期优化）

**✅ 建议 1：Reset Credits 添加内存缓存**
- **收益**：重复打开 modal 瞬间响应
- **成本**：低（注入 IMemoryCache，加 5 行代码）
- **实施时间**：10 分钟

**✅ 建议 5：账号列表 API 缓存 resetCreditsAvailableCount**
- **收益**：列表加载无需解析 JSON
- **成本**：中（需要数据库迁移）
- **实施时间**：20 分钟

---

### P2（可选，长期优化）

**建议 2：Reset Credits 改用强类型反序列化**
- **收益**：代码更简洁，类型安全
- **成本**：中（需定义 DTO 类）
- **实施时间**：15 分钟

**建议 4：卡片 UI 虚拟滚动**
- **收益**：账号 > 100 时不卡顿
- **成本**：高（需要库支持）
- **实施时间**：2 小时
- **触发条件**：账号数 > 100

---

## 7. 总结

### 当前性能状态

- ✅ **整体性能良好**：无严重性能问题
- ✅ **功能可用性高**：所有功能响应速度在可接受范围
- ⚠️ **有优化空间**：2 个中等优先级问题，1 个高优先级问题

### 必须优化的问题

**仅 1 个：FetchModels 的 O(n²) Join**
- 当模型数 > 50 时影响明显
- 优化成本极低，强烈建议立即修复

### 推荐优化的问题

**2 个：Reset Credits 缓存 + 账号列表 JSON 解析**
- 影响用户体验
- 优化成本不高，建议近期修复

### 可忽略的问题

**1 个：卡片 UI 虚拟滚动**
- 当前账号数少，暂无需求
- 将来账号数 > 100 时再优化

---

## 8. 性能监控建议

**建议添加性能日志**：

```csharp
// 在关键路径添加耗时日志
var sw = Stopwatch.StartNew();
var remoteModels = await _modelFetcher.FetchAsync(...);
_logger.LogInformation("FetchModels: upstream took {Ms}ms", sw.ElapsedMilliseconds);

sw.Restart();
var result = BuildResult(remoteModels, existingMappings, modelItems);
_logger.LogInformation("FetchModels: join took {Ms}ms for {Count} models", sw.ElapsedMilliseconds, remoteModels.Count);
```

**收益**：
- 生产环境可观测性
- 及时发现性能退化
- 为优化决策提供数据

---

## 9. 参考资料

- **O(n²) 优化案例**：`DetectionTasks+Analytics 分桶优化（确定正向）` commit
- **数据库层聚合优化**：`UsageLogs 列表+汇总改为数据库层分页和聚合` commit
- **缓存最佳实践**：ASP.NET Core Memory Cache 文档

---

## 附录：性能测试建议

如果要精确评估性能，建议：

1. **准备测试数据**：
   - 创建 100 个 Codex 账号
   - 每个账号关联 50 个模型
   - 每个账号有 5 个 reset credits

2. **测试场景**：
   - 账号列表加载时间
   - 打开模型预览 modal 时间
   - 打开 reset credits modal 时间

3. **性能指标**：
   - P50（中位数）< 200ms → 优秀
   - P95 < 500ms → 良好
   - P99 < 1s → 可接受

4. **工具**：
   - 浏览器 DevTools Performance 面板
   - ASP.NET Core 日志（Stopwatch）
   - Application Insights（生产环境）
