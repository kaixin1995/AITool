-- ============================================================
-- 迁移脚本：20261104_01_settle_post_completion_canceled_usage.sql
-- 修复 25cc83d 之前，OpenAI Chat 出口跨协议桥接流在完成事件后
-- 被客户端取消、误记为 canceled 的历史用量行。
--
-- 安全约束：用量行不记录 Chat/Responses 具体入口、上游协议或完成事件，
-- token>0 也不能证明流已完成。因此默认白名单为空，执行不会修改任何行。
-- 不可根据 token、时长或普通取消文案自行推断并批量加入白名单。
--
-- 使用前，逐条核对同一 RequestId 和 AttemptIndex 的独立证据，确认：
--   1. 该次尝试为受缺少 [DONE] 影响的 OpenAI Chat 跨协议桥接流，且发生在
--      实际部署 25cc83d 修复之前；
--   2. 已收到该次上游完整完成事件，客户端在此之后才断开。
-- 将证据位置及核对结果保存在迁移审计之外的工单中，无法核实的记录保留原状。
-- 在下方 CTE 用 SELECT '用量行Id' UNION ALL SELECT '另一行Id' 替换空集合，
-- 仅填写逐条核实的 ProxyUsageLogs.Id，先试运行并检查影响行数后再执行。
-- 不要把请求链路 ID 当作用量行 ID。
--
-- 时间窗口为 UTC：从 2026-11-04 00:00 到修复提交前的 14:00（不含）。
-- 这是保守的上限而非部署时间证明，窗口外记录不会修改；如果本地时间存储
-- 或部署时间无法确认，必须先核实，不应将相应记录加入白名单。
-- 历史数据若已被旧版宽泛脚本修改，无法由此脚本自动恢复，需结合备份审计。
-- ============================================================

WITH VerifiedUsageIds(Id) AS (
    SELECT NULL WHERE 0
)
UPDATE ProxyUsageLogs
SET Status = 'success',
    ErrorMessage = '',
    ErrorCategory = NULL,
    IsStreamInterrupted = 0
WHERE Id IN (SELECT Id FROM VerifiedUsageIds)
  AND Status = 'canceled'
  AND ProtocolType = 'OpenAI'
  AND ForwardingMode = 'bridge'
  AND IsStreaming = 1
  AND IsFinalResult = 1
  AND datetime(RequestedAt) >= '2026-11-04 00:00:00'
  AND datetime(RequestedAt) < '2026-11-04 14:00:00';
