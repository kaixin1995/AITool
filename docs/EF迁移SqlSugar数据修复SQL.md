# EF Core → SqlSugar 迁移数据修复指南

## 适用场景

本项目数据访问层从 EF Core 迁移到 SqlSugar 后，**已有的生产数据库**需要执行一次数据修复 SQL，解决两个格式差异：

| 问题 | EF Core 存储 | SqlSugar 期望 | 影响 |
|------|-------------|--------------|------|
| Guid 大小写 | 大写（`D3D1D1E2-...`） | 小写（`d3d1d1e2-...`） | 按主键/外键查询全部查不到（AccessKeys 提示密钥不存在、编辑无作用等） |
| DateTimeOffset 格式 | 带 offset（`2026-06-30 06:10:21+00:00`），UTC 时钟值 | 不带 offset（`2026-06-30 14:10:21`），本地时钟值 | 历史数据时间显示差 8 小时 |

## 智能识别原理

本 SQL 不依赖固定日期，而是通过数据本身的特征自动区分新旧：

- **EF 时代写入的数据**：DateTimeOffset 值**带 `+00:00` offset**，时钟值是 UTC
- **SqlSugar 写入的数据**：DateTimeOffset 值**不带 offset**，时钟值是本地时间

因此：
- 步骤 1 只对**带 `+00:00` 的数据**加 8 小时（旧数据修正），新数据不碰
- 步骤 2 去掉**残留的 `+00:00` offset**（统一格式）
- 步骤 3 所有 Guid 转小写

## 执行前提

- ✅ 已部署 SqlSugar 版本代码
- ⚠️ **必须先备份数据库文件**（`aitool.db`）
- ⚠️ **必须先停止服务**（避免执行期间有新数据写入干扰识别）

## 完整修复 SQL

> 将以下内容复制到 SQLite 工具（如 DB Browser for SQLite）中，对 `aitool.db` 执行。
> 已在真实生产数据副本上验证通过。

```sql
-- ==================================================================
-- EF Core → SqlSugar 数据修复 SQL（一次性执行）
-- 三个步骤：① 历史时间加8小时（智能识别带offset的旧数据）
--           → ② 去掉残留 offset → ③ Guid 转小写
-- 执行前务必备份 aitool.db！
-- ==================================================================

-- ──────────────────────────────────────
-- 步骤 1：对带 '+00:00' offset 的历史数据加 8 小时（UTC 时钟值 → 本地时钟值）
-- 智能识别：只有 EF 写入的旧数据带 '+00:00'，SqlSugar 新数据不带 offset，不会误伤。
-- ──────────────────────────────────────

UPDATE ProxyUsageLogs SET RequestedAt = datetime(strftime('%s', RequestedAt) + 8*3600, 'unixepoch', 'subsec') WHERE RequestedAt LIKE '%+00:00%' AND RequestedAt IS NOT NULL;
UPDATE Sites SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt LIKE '%+00:00%' AND CreatedAt IS NOT NULL;
UPDATE ModelLibraryItems SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt LIKE '%+00:00%' AND CreatedAt IS NOT NULL;
UPDATE ModelHealthMonitors SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt LIKE '%+00:00%' AND CreatedAt IS NOT NULL;
UPDATE ProxyRouteEntries SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt LIKE '%+00:00%' AND CreatedAt IS NOT NULL;
UPDATE DetectionTasks SET CreatedAt = datetime(strftime('%s', CreatedAt) + 8*3600, 'unixepoch', 'subsec') WHERE CreatedAt LIKE '%+00:00%' AND CreatedAt IS NOT NULL;
UPDATE DetectionTaskExecutions SET StartedAt = datetime(strftime('%s', StartedAt) + 8*3600, 'unixepoch', 'subsec') WHERE StartedAt LIKE '%+00:00%' AND StartedAt IS NOT NULL;
UPDATE DetectionTaskExecutions SET FinishedAt = datetime(strftime('%s', FinishedAt) + 8*3600, 'unixepoch', 'subsec') WHERE FinishedAt LIKE '%+00:00%' AND FinishedAt IS NOT NULL;
UPDATE SiteModelMappings SET LastCheckedAt = datetime(strftime('%s', LastCheckedAt) + 8*3600, 'unixepoch', 'subsec') WHERE LastCheckedAt LIKE '%+00:00%' AND LastCheckedAt IS NOT NULL;
UPDATE SystemRuntimeSettings SET LastUsageLogPrunedAt = datetime(strftime('%s', LastUsageLogPrunedAt) + 8*3600, 'unixepoch', 'subsec') WHERE LastUsageLogPrunedAt LIKE '%+00:00%' AND LastUsageLogPrunedAt IS NOT NULL;

-- ──────────────────────────────────────
-- 步骤 2：去掉残留的 +00:00 offset（步骤1加完8小时后 offset 标记还在，去掉统一格式）
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

## ⚠️ 重要：如果你之前已经执行过部分 SQL

如果你之前已经单独执行过去掉 offset 或 Guid 转小写的 SQL，**步骤 2 和 3 仍然可以安全执行**（它们是幂等的——已处理的数据不会再匹配）。

但**步骤 1 可能无法修正已去掉 offset 的历史数据**——因为这些数据已经不带 `+00:00`，无法被 `WHERE ... LIKE '%+00:00%'` 识别。

如果历史时间仍然不对，**最彻底的做法**：
1. 用备份恢复数据库到迁移前的状态
2. 重新执行完整 SQL（步骤 1 → 2 → 3）

## 注意事项

- **此 SQL 只需执行一次**，是迁移的一次性数据修复
- **步骤 1 和步骤 2 的顺序不能颠倒**：必须先加 8 小时（此时 offset 还在，用于 WHERE 识别旧数据），再去掉 offset
- **新安装的系统不需要执行此 SQL**（没有 EF 时代的历史数据）
- 执行后**无法回滚**，务必先备份
