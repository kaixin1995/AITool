# Usage 日志 InputTokens 语义切换历史数据修复 SQL

## 背景

| 提交 | 时间 | 变更 |
|------|------|------|
| `0a820f5`（Anthropic 协议） | 2026-08-13 | Anthropic 协议 usage 输入 token 改为"不含缓存的新输入" |
| `aba2773`（OpenAI/Responses/Chat 页） | 用户部署时刻 | 其余协议统一为"不含缓存的新输入"语义 |

切换前写入的历史行：

- `InputTokens` 含缓存命中部分（即"含缓存"口径）
- `TotalTokens` 把缓存重复计入（`旧Total = 旧Input(含缓存) + Cached + Output`）

切换后写入的新行：

- `InputTokens` = 新输入（不含缓存）
- `TotalTokens` = 新输入 + Cached + Output（缓存只计一次）

因此 Analytics / Usage 页跨新旧数据的趋势汇总在切换时刻会出现"输入 / 总 Token 骤降"的假象。本脚本把历史行修正为新语义。

## 修正公式

对旧语义行（`CachedTokens > 0`）：

- `新InputTokens = 旧InputTokens - CachedTokens`（钳制非负）
- `新TotalTokens = 旧TotalTokens - CachedTokens`
- `CachedTokens`、`OutputTokens` 不变

> ⚠️ 该 SQL **非幂等**：已修正的行再次执行会被重复扣减。请只执行一次。

## 执行前提

- ⚠️ **先备份数据库文件**（`aitool.db`）
- 需要知道两个部署时间（默认取对应提交的时间，实际以你部署为准）：
  - `@AnthropicCutoff`：`0a820f5` 版本部署时间（Anthropic 行在它之前是旧语义）
  - `@UnifiedCutoff`：`aba2773` 版本部署时间（其余协议行在它之前是旧语义）

## 执行方式（二选一）

**方式 A：网页执行（推荐，远程可用）**

1. 把下方"第一步预检 + 第二步修正"的 SQL 整理成一个 `.sql` 文件（替换好两个 cutoff 时间），上传到服务器的 `sql-migrations` 目录（部署目录下，页面上会显示具体路径）。
2. 打开 管理后台 → 调试工具 → SQL 迁移 Tab，刷新列表即可看到该脚本。
3. 先点「试运行」：事务内执行后回滚，从影响行数核对预检结果（试运行不产生任何数据变更）。
4. 确认无误后点「执行」，输入管理员密码确认。执行全程事务，失败自动回滚；每次尝试（含试运行）都会写入执行审计。
5. 该接口只执行服务器目录里已存在的文件，不接受通过网络传入的 SQL 文本；执行前仍建议备份数据库。

**方式 B：SSH 手动执行（sqlite3 CLI）**

- 需先停止服务，避免执行期间写入新行；执行完第三步验证后再启动。

## 第一步：预检（查看影响行数，确认范围）

```sql
-- 各协议在各自 cutoff 前的旧语义行数（应为 0 或预期值）
SELECT ProtocolType,
       COUNT(*) AS OldSemanticRows,
       SUM(CachedTokens) AS SumCached
FROM ProxyUsageLogs
WHERE CachedTokens > 0
  AND ( (ProtocolType = 'Anthropic' AND julianday(RequestedAt) < julianday('2026-08-13 10:56:00'))
     OR (ProtocolType <> 'Anthropic' AND julianday(RequestedAt) < julianday('2026-08-20 00:00:00')) )
GROUP BY ProtocolType;

-- 抽查 5 条待修正样例（修正前）
SELECT RequestedAt, ProtocolType, InputTokens, CachedTokens, OutputTokens, TotalTokens
FROM ProxyUsageLogs
WHERE CachedTokens > 0
  AND ( (ProtocolType = 'Anthropic' AND julianday(RequestedAt) < julianday('2026-08-13 10:56:00'))
     OR (ProtocolType <> 'Anthropic' AND julianday(RequestedAt) < julianday('2026-08-20 00:00:00')) )
ORDER BY RequestedAt DESC
LIMIT 5;
```

## 第二步：执行修正（把两个时间替换成你的实际部署时间）

```sql
-- ==================================================================
-- Usage 日志 InputTokens 语义历史数据修正（一次性执行）
-- 旧语义：InputTokens 含缓存、TotalTokens 重复计缓存
-- 新语义：InputTokens = 旧Input - Cached；TotalTokens = 旧Total - Cached
-- 执行前务必备份 aitool.db！非幂等，只执行一次！
-- ==================================================================

UPDATE ProxyUsageLogs
SET InputTokens = MAX(0, InputTokens - CachedTokens),
    TotalTokens = TotalTokens - CachedTokens
WHERE CachedTokens > 0
  AND ( (ProtocolType = 'Anthropic' AND julianday(RequestedAt) < julianday('2026-08-13 10:56:00'))
     OR (ProtocolType <> 'Anthropic' AND julianday(RequestedAt) < julianday('2026-08-20 00:00:00')) );
```

## 第三步：验证

```sql
-- 修正后不应再有旧语义行（同一条件应返回 0 行）
SELECT COUNT(*) AS RemainingOldRows
FROM ProxyUsageLogs
WHERE CachedTokens > 0
  AND ( (ProtocolType = 'Anthropic' AND julianday(RequestedAt) < julianday('2026-08-13 10:56:00'))
     OR (ProtocolType <> 'Anthropic' AND julianday(RequestedAt) < julianday('2026-08-20 00:00:00')) );

-- 数值恒等式校验：所有行都应满足 TotalTokens = InputTokens + CachedTokens + OutputTokens
SELECT COUNT(*) AS BrokenRows
FROM ProxyUsageLogs
WHERE TotalTokens <> InputTokens + CachedTokens + OutputTokens;
```

## 说明

- SQLite 的 `julianday()` 兼容 SqlSugar 写入的本地时间字符串格式；若生产库使用其他数据库（达梦/PostgreSQL），`MAX` 与日期函数语法需对应调整。
- 若你**只有一次部署**（Anthropic 修复与 aba2773 同时上线），把两个 cutoff 设为同一时间即可。
- 修正后 Analytics 历史趋势将恢复连续；若旧库中 Anthropic 行在 `0a820f5` 前也已是新语义（你手动修过），可将 Anthropic 条件整体移除，只保留非 Anthropic 分支。
