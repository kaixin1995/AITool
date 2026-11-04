-- ============================================================
-- 迁移脚本：20261104_01_settle_post_completion_canceled_usage.sql
-- 目标：把「完成事件已到达后被客户端断开」的历史用量行从 canceled 修正为 success
-- 背景：
--   2026-11-04 修复（25cc83d）之前，跨协议桥接流（如 1M→gpt-6.1-terra，Codex Responses
--   上游桥接为 OpenAI Chat）正常完成时不发 data: [DONE] 终止符，部分客户端
--   （DeepSeek Harness 等严格等待终止符的实现）收完内容后挂起直至超时 abort。
--   该类请求上游已完整生成并按成功全额计费（usage 来自 response.completed 事件，
--   能提取到 usage 即证明完成事件已到达），但被记为 canceled。
-- 判据：
--   Status='canceled' 且 (Input+Cached+Output)>0 —— token>0 仅可能来自完成事件，
--   即流已完整交付；token=0 的 canceled 是真正的中途断开（流协议盲区），保持原状。
-- 影响列：
--   Status: canceled → success（与 e8ad10b 起的新契约一致：完成事件后的断开按成功入账）
--   ErrorMessage: 清空（成功行不应携带取消文案）
--   IsStreamInterrupted: 清 0（流已完整完成，非中断）
-- 附注：
--   RequestedAt/Attempts/时长等列不动；修复后的新数据由应用层直接按 success 入账，
--   本脚本只修正历史口径。执行后 usage-logs 概览的成功率与总 Tokens 会相应上升。
-- ============================================================

UPDATE ProxyUsageLogs
SET Status = 'success',
    ErrorMessage = '',
    IsStreamInterrupted = 0
WHERE Status = 'canceled'
  AND (InputTokens + CachedTokens + OutputTokens) > 0;
