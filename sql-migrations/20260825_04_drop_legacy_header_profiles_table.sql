-- ============================================================
-- 迁移脚本：20260825_04_drop_legacy_header_profiles_table.sql
-- 目标：清理数据库中废弃的 HeaderProfiles 表
-- 说明：
--   请求头模板与客户端仿真配置已全面迁移至本地独立 JSON 配置文件（client-header-profiles.json），
--   脱离数据库管理。本脚本用于安全清理 SQLite 数据库中的遗留数据表。
-- ============================================================

DROP TABLE IF EXISTS HeaderProfiles;
