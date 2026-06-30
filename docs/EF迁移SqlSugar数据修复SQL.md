# EF Core → SqlSugar 迁移数据修复指南

## 适用场景

本项目数据访问层从 EF Core 迁移到 SqlSugar 后，**已有的生产数据库**需要执行一次数据修复 SQL，解决两个格式差异：

| 问题 | EF Core 存储 | SqlSugar 期望 | 影响 |
|------|-------------|--------------|------|
| Guid 大小写 | 大写（`D3D1D1E2-...`） | 小写（`d3d1d1e2-...`） | 按主键/外键查询全部查不到（AccessKeys 提示密钥不存在、编辑无作用等） |
| DateTimeOffset 格式 | 带 offset（`2026-06-30 06:10:21+00:00`），UTC 时钟值 | 不带 offset（`2026-06-30 14:10:21`），本地时钟值 | 历史数据时间显示差 8 小时 |

## 执行前提

- ✅ 已部署 SqlSugar 版本代码（含 `DataExecuting ToLocalTime` 修复）
- ⚠️ **必须先备份数据库文件**（`aitool.db`）
- ⚠️ **必须先停止服务**

## 完整修复 SQL

> 将以下内容复制到 SQLite 工具（如 DB Browser for SQLite）中，对 `aitool.db` 执行。
> 已在真实生产数据副本上验证通过。

```sql
-- ==================================================================
-- EF Core → SqlSugar 数据修复 SQL（一次性执行）
-- 三个步骤：① 历史时间加8小时 → ② 去掉残留 offset → ③ Guid 转小写
-- 执行前务必备份 aitool.db！
-- ==================================================================

-- ──────────────────────────────────────
-- 步骤 1：历史 DateTimeOffset 值加 8 小时（UTC 时钟值 → 本地时钟值）
-- 只更新迁移前的旧数据（日期 < '2026-06-30'），不碰迁移后的新数据。
-- ⚠️ 如果你看到这个文档的日期已过，请把 '2026-06-30' 改成你实际迁移的日期！
-- ──────────────────────────────────────

UPDATE ProxyUsageLogs SET RequestedAt = datetime(strftime('%s', RequestedAt) + 8*3600, 'unixepoch', 'subsec') WHERE RequestedAt < '2026-06-30';
UPDATE Sites SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt < '2026-06-30';
UPDATE ModelLibraryItems SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt < '2026-06-30';
UPDATE ModelHealthMonitors SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt < '2026-06-30';
UPDATE ProxyRouteEntries SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt < '2026-06-30';
UPDATE DetectionTasks SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt < '2026-06-30';
UPDATE DetectionTaskExecutions SET StartedAt = datetime(strftime('%s', StartedAt) + 8*3600, 'unixepoch', 'subsec') WHERE StartedAt < '2026-06-30';
UPDATE DetectionTaskExecutions SET FinishedAt = datetime(strftime('%s', FinishedAt) + 8*3600, 'unixepoch', 'subsec') WHERE FinishedAt IS NOT NULL AND FinishedAt < '2026-06-30';
UPDATE SiteModelMappings SET LastCheckedAt = datetime(strftime('%s', LastCheckedAt) + 8*3600, 'unixepoch', 'subsec') WHERE LastCheckedAt IS NOT NULL AND LastCheckedAt < '2026-06-30';
UPDATE SystemRuntimeSettings SET LastUsageLogPrunedAt = datetime(strftime('%s', LastUsageLogPrunedAt) + 8*3600, 'unixepoch', 'subsec') WHERE LastUsageLogPrunedAt IS NOT NULL AND LastUsageLogPrunedAt < '2026-06-30';

-- ──────────────────────────────────────
-- 步骤 2：去掉残留的 +00:00 offset
-- （步骤 1 加完 8 小时后，旧数据的 offset 标记还在，需要去掉统一格式）
-- ──────────────────────────────────────

UPDATE ProxyUsageLogs SET RequestedAt = REPLACE(RequestedAt, '+00:00', '') WHERE RequestedAt LIKE '%+00:00%';
UPDATE Sites SET CreatedAt = REPLACE(CreatedAt, '+00:00', '') WHERE CreatedAt LIKE '%+00:00%';
UPDATE ModelLibraryItems SET CreatedAt = REPLACE(CreatedAt, '+00:00', '') WHERE CreatedAt LIKE '%+00:00%';
UPDATE ModelHealthMonitors SET CreatedAt = REPLACE(CreatedAt, '+00:00', '') WHERE CreatedAt LIKE '%+00:00%';
UPDATE ProxyRouteEntries SET CreatedAt = REPLACE(CreatedAt, '+00:00', '') WHERE CreatedAt LIKE '%+00:00%';
UPDATE DetectionTasks SET CreatedAt = REPLACE(CreatedAt, '+00:00', '') WHERE CreatedAt LIKE '%+00:00%';
UPDATE DetectionTaskExecutions SET StartedAt = REPLACE(StartedAt, '+00:00', '') WHERE StartedAt LIKE '%+00:00%';
UPDATE DetectionTaskExecutions SET FinishedAt = REPLACE(FinishedAt, '+00:00', '') WHERE FinishedAt LIKE '%+00:00%';
UPDATE SiteModelMappings SET LastCheckedAt = REPLACE(LastCheckedAt, '+00:00', '') WHERE LastCheckedAt LIKE '%+00:00%';
UPDATE SystemRuntimeSettings SET LastUsageLogPrunedAt = REPLACE(LastUsageLogPrunedAt, '+00:00', '') WHERE LastUsageLogPrunedAt LIKE '%+00:00%';

-- ──────────────────────────────────────
-- 步骤 3：所有 Guid 列转小写（主键 + 外键）
-- ──────────────────────────────────────

-- 主键
UPDATE Sites SET Id = LOWER(Id);
UPDATE ModelLibraryItems SET Id = LOWER(Id);
UPDATE SiteModelMappings SET Id = LOWER(Id);
UPDATE ProxyRouteEntries SET Id = LOWER(Id);
UPDATE ProxyRouteRules SET Id = LOWER(Id);
UPDATE ProxyAccessKeys SET Id = LOWER(Id);
UPDATE ProxyUsageLogs SET Id = LOWER(Id);
UPDATE ModelHealthMonitors SET Id = LOWER(Id);
UPDATE DetectionTasks SET Id = LOWER(Id);
UPDATE DetectionTaskExecutions SET Id = LOWER(Id);

-- 外键
UPDATE SiteModelMappings SET SiteId = LOWER(SiteId), ModelLibraryItemId = LOWER(ModelLibraryItemId);
UPDATE ProxyRouteRules SET SiteId = LOWER(SiteId);
UPDATE ProxyUsageLogs SET RequestId = LOWER(RequestId), AccessKeyId = LOWER(AccessKeyId), TargetSiteId = LOWER(TargetSiteId);
UPDATE DetectionTaskExecutions SET DetectionTaskId = LOWER(DetectionTaskId);
UPDATE DetectionTasks SET ModelLibraryItemId = LOWER(ModelLibraryItemId);
UPDATE ModelHealthMonitors SET ModelLibraryItemId = LOWER(ModelLibraryItemId);
```

## 执行步骤

1. **备份数据库**：复制 `aitool.db` 到安全位置
2. **停止服务**
3. **执行上面的完整 SQL**（步骤 1 → 2 → 3 按顺序执行）
4. **部署最新代码**（含 SqlSugar 迁移 + 时区修复）
5. **启动服务**
6. **验证**：
   - Admin/UsageLogs 链路详情、月范围汇总正常
   - Admin/AccessKeys 保存正常
   - Admin/Models / Sites 编辑正常
   - Admin/Analytics 月范围正常
   - 历史数据时间显示正确（不再差 8 小时）

## 注意事项

- **此 SQL 只需执行一次**，是迁移的一次性数据修复
- **步骤 1 的日期 `'2026-06-30'`** 是迁移日期，如果你的迁移日期不同请修改
- **步骤 1 和步骤 2 的顺序不能颠倒**：必须先加 8 小时（此时 offset 还在，用于 WHERE 区分新旧数据），再去掉 offset
- **新安装的系统不需要执行此 SQL**（没有 EF 时代的历史数据）
- 执行后**无法回滚**，务必先备份
