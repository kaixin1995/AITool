-- ============================================================
-- 迁移脚本：20260825_03_cleanup_legacy_codex_headers.sql
-- 目标：清理存量 Codex 站点中的历史硬编码请求头（如 codex_cli_rs/0.133.0）
-- 说明：
--   清洗后由系统内置的 HeaderProfile / ClientEmulationEngine 统一注入
--   最新的真实 Codex Desktop 官方请求头。
-- ============================================================

-- 1. 无 Chatgpt-Account-Id 的历史硬编码 Codex 站点直接置空 ExtraHeadersJson
UPDATE Sites
SET ExtraHeadersJson = NULL
WHERE ManagedSource = 'Codex'
  AND ExtraHeadersJson LIKE '%codex_cli_rs%'
  AND (ExtraHeadersJson NOT LIKE '%Chatgpt-Account-Id%' OR ExtraHeadersJson IS NULL);

-- 2. 包含 Chatgpt-Account-Id 的 Codex 站点，提取并规范化 JSON
UPDATE Sites
SET ExtraHeadersJson = json_object('Chatgpt-Account-Id', json_extract(ExtraHeadersJson, '$.Chatgpt-Account-Id'))
WHERE ManagedSource = 'Codex'
  AND ExtraHeadersJson LIKE '%codex_cli_rs%'
  AND json_extract(ExtraHeadersJson, '$.Chatgpt-Account-Id') IS NOT NULL;
