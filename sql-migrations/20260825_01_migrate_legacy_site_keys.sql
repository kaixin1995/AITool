-- ============================================================
-- 迁移脚本：20260825_01_migrate_legacy_site_keys.sql
-- 目标：为存量用户自建站点回填默认的 SiteKey 记录（支持多 Key 管理）
-- 说明：
--   1. 仅针对 ManagedSource 为空且 ApiKey 非空的自建站点；
--   2. 幂等执行：若该站点已存在 SiteKey 则跳过。
-- ============================================================

INSERT INTO SiteKeys (Id, SiteId, KeyValue, Remark, Priority, IsEnabled, CreatedAt)
SELECT 
    lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-4' || substr(lower(hex(randomblob(2))),2) || '-a' || substr(lower(hex(randomblob(2))),2) || '-' || lower(hex(randomblob(6))) AS Id,
    s.Id AS SiteId,
    s.ApiKey AS KeyValue,
    '默认' AS Remark,
    0 AS Priority,
    1 AS IsEnabled,
    datetime('now') AS CreatedAt
FROM Sites s
WHERE (s.ManagedSource IS NULL OR s.ManagedSource = '')
  AND (s.ApiKey IS NOT NULL AND s.ApiKey != '')
  AND NOT EXISTS (
      SELECT 1 FROM SiteKeys k WHERE k.SiteId = s.Id
  );
