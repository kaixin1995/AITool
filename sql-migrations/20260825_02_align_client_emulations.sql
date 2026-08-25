-- ============================================================
-- 迁移脚本：20260825_02_align_client_emulations.sql
-- 目标：为托管站点模型映射对齐客户端特征模拟类型（ClientEmulation）
-- 说明：
--   1. 将存量未配置特征模拟的 Codex 映射对齐为 CodexCli（Codex Desktop 官方客户端）；
--   2. 将存量未配置特征模拟的 Google Antigravity 映射对齐为 Antigravity；
--   3. 将存量未配置特征模拟的 Google Gemini 映射对齐为 GeminiCli。
-- ============================================================

-- 1. 对齐 Codex 映射
UPDATE SiteModelMappings
SET ClientEmulation = 'CodexCli'
WHERE SiteId IN (SELECT Id FROM Sites WHERE ManagedSource = 'Codex')
  AND (ClientEmulation IS NULL OR ClientEmulation = '' OR ClientEmulation = 'None');

-- 2. 对齐 Google Antigravity 映射（BaseUrl 包含 sandbox.google.com 或 api/antigravity）
UPDATE SiteModelMappings
SET ClientEmulation = 'Antigravity'
WHERE SiteId IN (
    SELECT Id FROM Sites 
    WHERE ManagedSource = 'Google' 
      AND (BaseUrl LIKE '%sandbox.google.com%' OR BaseUrl LIKE '%/antigravity%')
)
AND (ClientEmulation IS NULL OR ClientEmulation = '' OR ClientEmulation = 'None');

-- 3. 对齐 Google 普通 Gemini 映射
UPDATE SiteModelMappings
SET ClientEmulation = 'GeminiCli'
WHERE SiteId IN (
    SELECT Id FROM Sites 
    WHERE ManagedSource = 'Google' 
      AND NOT (BaseUrl LIKE '%sandbox.google.com%' OR BaseUrl LIKE '%/antigravity%')
)
AND (ClientEmulation IS NULL OR ClientEmulation = '' OR ClientEmulation = 'None');
