-- ============================================================
-- 迁移脚本：20260825_02_align_client_emulations.sql
-- 目标：为托管站点模型映射对齐客户端特征模拟类型（ClientEmulation）
-- 说明：
--   1. 将存量未配置特征模拟的 Codex 映射对齐为 CodexCli（Codex Desktop 官方客户端）；
--   2. 将存量未配置特征模拟的 Google 映射统一对齐为 Antigravity（GeminiCLI 接入方式已下线，
--      旧值 'GeminiCli' 由应用层 ClientEmulationConstants.Normalize 归一化为 Antigravity）。
-- ============================================================

-- 1. 对齐 Codex 映射
UPDATE SiteModelMappings
SET ClientEmulation = 'CodexCli'
WHERE SiteId IN (SELECT Id FROM Sites WHERE ManagedSource = 'Codex')
  AND (ClientEmulation IS NULL OR ClientEmulation = '' OR ClientEmulation = 'None');

-- 2. 对齐 Google 映射（Antigravity 唯一接入方式；含历史 'GeminiCli' 值的映射一并归一）
UPDATE SiteModelMappings
SET ClientEmulation = 'Antigravity'
WHERE SiteId IN (SELECT Id FROM Sites WHERE ManagedSource = 'Google')
  AND (ClientEmulation IS NULL OR ClientEmulation = '' OR ClientEmulation = 'None' OR ClientEmulation = 'GeminiCli');
