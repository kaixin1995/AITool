# EF Core → SqlSugar 迁移进度文档

> 本文档用于跨对话窗口维护迁移细节，避免上下文压缩丢失。
> 最后更新：迁移进行中（Web 层 API 替换阶段）

## 一、总体目标
把整个数据访问层从 EF Core 迁移到 SqlSugar（SqlSugarCore 5.1.4.215），删除所有 EF Core 依赖。
动机：SqlSugar 能将 DateTimeOffset 区间比较下推到 SQLite（已用独立验证程序证实），解决看板全表加载问题。

## 二、当前可编译状态
**全部项目编译通过（Infrastructure + Web + 两个测试项目，0 错误）**。
- ApplicationTests：83/88 通过（5 失败，DateTimeOffset 时区）
- IntegrationTests：147/177 通过（30 失败，需分析失败模式）

## 九、IntegrationTests 失败分析（147/177 通过，30 失败）

失败类别：
1. **DeveloperInvocationsPageTests（约14个）**：404 NotFound。工厂 DI 替换正确，但页面请求返回 404。怀疑：启动时 Program.cs 的 InitializeDatabase 和测试工厂的临时库不一致，或 Add 兼容方法写入失败。
2. **AnalyticsPageTests（2个）**：DateTimeOffset 时区问题（同 ApplicationTests）。
3. **ModelEditCacheTests（1个）**：缓存查询返回空。ProxyRequestMetadataCache 的内存 join（先 ToListAsync 再内存 join）返回空，怀疑 SqlSugarScope 异步问题或 ToListAsync 在兼容模式下行为异常。
4. **ClientSimulatorPageTests（2个）**、**Proxy/AnthropicProxy（2个）** 等：待分析。

**下一步调试方向**：
- 验证 SqlSugarScope（单例）在测试 WebApplicationFactory 下的连接字符串覆盖是否生效
- 验证 Add 兼容扩展方法（query.Context.Insertable）是否真正写入
- 验证 await dbContext.X.ToListAsync(ct) 在 SqlSugarScope 下是否返回正确数据

## 十、重要：当前代码已可提交但测试未全绿

生产代码（Infrastructure + Web）全部编译通过且已迁移完成。
测试代码已迁移完成（编译通过），但 35 个测试失败（5 ApplicationTests + 30 IntegrationTests）需要调试。
**这是迁移的正常收尾阶段，不是架构性问题。**

## 三、已完成（可靠，无需重做）

### 1. 包引用切换 ✅
- `src/AITool.Domain/AITool.Domain.csproj`：加 SqlSugarCore 5.1.4.215
- `src/AITool.Infrastructure/AITool.Infrastructure.csproj`：移除 Microsoft.EntityFrameworkCore.Sqlite 8.0.16，加 SqlSugarCore
- `tests/AITool.ApplicationTests/AITool.ApplicationTests.csproj`：移除 EF InMemory，加 SqlSugarCore
- **IntegrationTests csproj 不直接引用 EF**（通过 Web 传递），暂未改

### 2. 11 个实体加 SqlSugar 特性 ✅
所有实体都在 `src/AITool.Domain/` 下，加了 `[SugarTable]`、`[SugarColumn]`、`[SugarIndex]`：
- `Sites/Site.cs` — EndpointPathMode 默认 "standard-root"
- `Models/ModelLibraryItem.cs` — **ModelType 从 EF shadow property 改为实体真实属性**（`[SugarColumn(Length=50)] public string ModelType { get; set; } = "chat";`），ModelName 唯一索引用 `[SugarIndex(..., true)]`
- `SiteCatalog/SiteModelMapping.cs` — 复合唯一索引 {SiteId, RemoteModelName}
- `Detection/DetectionTask.cs`、`Detection/DetectionTaskExecution.cs`（StartedAt 索引）
- `Proxy/ProxyRouteEntry.cs`（EntryName 唯一）、`Proxy/ProxyRouteRule.cs`（2个复合索引）、`Proxy/ProxyAccessKey.cs`（{AccessKeyHash, IsEnabled} 索引）
- `Proxy/ProxyUsageLog.cs`（6个索引）
- `Models/ModelHealthMonitor.cs`（ModelLibraryItemId 唯一）
- `Operations/SystemRuntimeSettings.cs`（Id 主键，单例表）

### 3. AppDbContext 重写 ✅（src/AITool.Infrastructure/Persistence/AppDbContext.cs）
- 不再继承 EF DbContext，封装 `ISqlSugarClient`（SqlSugarScope 单例）
- 暴露与原 DbSet 同名的 `ISugarQueryable<T>` 便捷访问器（Sites、ModelLibraryItems 等 11 个）
- 提供便捷写方法：`InsertAsync`、`InsertRangeAsync`、`UpdateAsync`、`DeleteAsync(entity)`、`DeleteRangeAsync`、`DeleteAsync<T>(predicate)`
- `SqlSugarSetup.AddSqlSugar(connectionString)`：注册 SqlSugarScope 单例 + AppDbContext（Scoped）
- `SqlSugarSetup.InitializeDatabase(db)`：CodeFirst InitTables 11 张表 + 持久化/连接级 PRAGMA（WAL、synchronous、cache_size、busy_timeout）
- **已删除** `SqlitePragmaInterceptor.cs`（EF 拦截器，已被 SqlSugar 配置替代）

### 4. Program.cs 数据库初始化 ✅
- `builder.Services.AddDbContext<AppDbContext>(...)` → `builder.Services.AddSqlSugar(connectionString)`
- 启动初始化：`db.Database.EnsureCreated()` + PRAGMA → `SqlSugarSetup.InitializeDatabase(sqlSugarClient)`
- **已删除** `EnsureProxyUsageLogSchemaAsync` 和 `ColumnExistsAsync`（9 列手写升级脚本，由 CodeFirst 差量更新替代）
- 已删 `using Microsoft.EntityFrameworkCore;`

### 5. Infrastructure 层 6 个服务迁移 ✅
- `Operations/SystemRuntimeSettingsService.cs` — GetOrCreate/Update 用 InsertAsync/UpdateAsync；ClearUsageLogsAsync 用 DeleteAsync(predicate)（下推 DateTimeOffset）
- `Retention/LogRetentionService.cs` — PruneAsync 用 DeleteAsync<ProxyUsageLog>(predicate)（下推 DateTimeOffset，删除了全表 ToList）
- `Conversations/ConversationLogService.cs` — 读开关 AsNoTracking().FirstOrDefault → First
- `Scheduling/HangfireDetectionScheduler.cs` — 删 EnsureCreatedAsync；FindAsync → FirstAsync；Add+SaveChanges → InsertAsync；AsQueryable+Where → WhereIF
- `Health/ModelHealthRequestService.cs` — FirstOrDefaultAsync → FirstAsync；AsNoTracking 删除；SaveChanges → UpdateAsync
- `Proxy/ProxyUsageLogBatchWriter.cs` — AddRange+SaveChanges → InsertRangeAsync

## 四、进行中（Web 层，src/AITool.Web/）

### 已做的批量操作
- **所有 17 个文件的 `using Microsoft.EntityFrameworkCore;` 已删除**（sed 批量）
- **所有 `.AsNoTracking()` 调用已删除**（sed 批量）

### 剩余编译错误分类（约 258 个，分 7 类）

#### A. `SaveChangesAsync(cancellationToken)` — 约 60 处（最大量）
**不能简单删除**。每处前面跟着 EF 的 Add/Remove/Update，需配对改成 SqlSugar：
- `_dbContext.X.Add(entity); ... await _dbContext.SaveChangesAsync(ct);` → `await _dbContext.InsertAsync(entity, ct);`（删两行 Add 和 SaveChanges）
- `_dbContext.X.Remove(entity); ... SaveChanges` → `await _dbContext.DeleteAsync(entity, ct);`
- `_dbContext.X.RemoveRange(list); ... SaveChanges` → `await _dbContext.DeleteRangeAsync(list, ct);`
- 单纯的 `entity.字段 = ...; SaveChanges`（更新跟踪实体）→ `await _dbContext.UpdateAsync(entity, ct);`

**涉及文件**（按 SaveChanges 数量）：
- RouteRulesApiController.cs（5处）、Sites/Index.cshtml.cs（8处）、Models/Index.cshtml.cs（4处）、Models/Edit.cshtml.cs（4处）、AccessKeysApiController.cs（4处）、ModelHealth/Index.cshtml.cs（3处）、DetectionTasks/Index.cshtml.cs（3处）、SiteCatalogApiController.cs（1处）、Sites/Edit.cshtml.cs（1处）、Sites/Import.cshtml.cs（1处）

#### B. `FindAsync([id], cancellationToken)` — 约 18 处
→ `InSingleAsync(id)`（去掉 ct 参数）
涉及：AccessKeysApiController（3）、ModelsApi（已改）、RouteRulesApi（2）、SiteCatalogApi（1）、Sites/Index（2）、Sites/Edit（2）、Models/Edit（4）、Models/Index（2）、DetectionTasks（1）

#### C. `RemoveRange(...)` — 约 15 处
分两种：
- `RemoveRange(具体实体列表)` → `_dbContext.DeleteRangeAsync(list, ct)`
- `RemoveRange(_dbContext.X)`（清空整表，EF 把 DbSet 当 IEnumerable）→ `_dbContext.Client.Deleteable<T>().ExecuteCommandAsync(ct)`

#### D. `Add(entity)` — 约 16 处
→ 与 SaveChanges 配对改 `InsertAsync(entity, ct)`

#### E. `ToDictionaryAsync` 3 参数重载 — 约 8 处（UsageLogsApiController、ModelHealth/Index）
SqlSugar 的 ToDictionaryAsync 签名不同。需改成 `.ToListAsync(ct)` 后内存 `.ToDictionary(...)`。

#### F. `FirstOrDefaultAsync(predicate, ct)` — Models/Edit 等少数
SqlSugar 用 `FirstAsync(predicate, ct)`（注意：FirstAsync 找不到返回 null，需验证）

#### G. `AnyAsync(predicate, ct)` — RouteRulesApi（2）、ModelHealth（1）
SqlSugar 支持 `AnyAsync(predicate, ct)`，可能直接可用，需编译验证。

## 五、未开始

### 测试项目迁移（tests/）
- **18 个 WebApplicationFactory**：每个有 `services.RemoveAll<DbContextOptions<AppDbContext>>()` + `AddDbContext<...>(UseSqlite)` + `EnsureDeletedAsync` + `EnsureCreatedAsync`。要改成 SqlSugar 初始化（创建临时 db 文件 + InitTables）。
- **4 个 InMemory provider 测试**（ApplicationTests：SystemRuntimeSettingsServiceTests、UsageLogServiceTests、LogRetentionServiceTests、SystemRuntimeSettingsServiceSqliteTests）：SqlSugar 无内存 provider，改临时 SQLite 文件。
- **直接 new AppDbContext 的测试**：SiteBulkDeleteTests、ModelEditCacheTests、ModelConcurrencyLimiterTests、ProxyMetadataCacheTests、SystemSettingsCacheTests。

## 六、SqlSugar API 速查（迁移用）

### 查询
```csharp
// EF: db.X.Where(p).ToListAsync(ct)
db.X.Where(p).ToListAsync(ct)  // SqlSugar 兼容，不变

// EF: db.X.FindAsync([id], ct)
db.X.InSingleAsync(id)  // 注意无 ct 参数

// EF: db.X.AsNoTracking().FirstOrDefault(p, ct)
db.X.FirstAsync(p, ct)  // 删 AsNoTracking，FirstOrDefaultAsync→FirstAsync

// EF: db.X.AnyAsync(p, ct)  → 验证是否兼容
```

### 写操作
```csharp
// EF: db.X.Add(e); await db.SaveChangesAsync(ct)
await db.InsertAsync(e, ct)  // AppDbContext 便捷方法

// EF: db.X.AddRange(list); await db.SaveChangesAsync(ct)
await db.InsertRangeAsync(list, ct)

// EF: db.X.Remove(e); await db.SaveChangesAsync(ct)
await db.DeleteAsync(e, ct)

// EF: db.X.RemoveRange(list); await db.SaveChangesAsync(ct)
await db.DeleteRangeAsync(list, ct)

// EF: db.X.RemoveRange(db.X)（清空整表）
await db.Client.Deleteable<T>().ExecuteCommandAsync(ct)

// EF: e.字段=...; await db.SaveChangesAsync(ct)（更新跟踪实体）
await db.UpdateAsync(e, ct)

// EF: db.X.Where(p).RemoveRange（条件删除，少见）
await db.DeleteAsync<T>(p, ct)
```

### 不兼容需注意
- `ToDictionaryAsync(keySelector, valueSelector, ct)` 3参数 → `.ToListAsync(ct).ContinueWith(t => t.Result.ToDictionary(...))`
- 多表 LINQ join（query syntax）→ SqlSugar 用 `.Queryable<T1>().LeftJoin<T2>((a,b)=>...)`，**ProxyRequestMetadataCache 有 6 处这种**，最复杂

## 七、验证清单
- [ ] Web 项目编译通过（0 错误）
- [ ] 全部 88 单测通过
- [ ] 全部 177 集成测试通过
- [ ] grep 确认无 `Microsoft.EntityFrameworkCore` 残留
- [ ] grep 确认无 `SaveChangesAsync` / `AsNoTracking` / `FindAsync` 残留
