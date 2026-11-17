-- ============================================================
-- 迁移脚本：20261117_01_purge_session_stats_residue.sql
-- 清理「会话统计」功能（1.0.1.58 起移除）在库中的残留数据。
--
-- 清理范围：
--   1. GatewayRequestMetrics 全表：1.0.1.57 及之前由「会话与流量统计」开关写入的
--      指标账本行（含全部未识别行）。1.0.1.58 起该表只服务于密钥预算结算，
--      本脚本执行后账本从零开始，过期清理由预算查询视图接管。
--   2. ProxyUsageLogs 的 SessionDigest / ParentSessionDigest 两列：自该版本起
--      不再写入，历史值没有任何读取方，置 NULL 回到未采集形态
--      （列本身保留，不做删列，避免 CodeFirst 对存量表做结构手术）。
--
-- 执行前提与安全约束：
--   1. 先部署 1.0.1.58 或确认「会话与流量统计」开关已关闭：旧版本仍在写入时执行，
--      清理后会产生新残留。
--   2. 已配置密钥预算的部署不要执行语句 1：预算准入按窗口内账本行求和，清空后
--      当前窗口用量从零重算，等于变相放宽限额。未配置预算则无此顾虑。
--   3. 语句 2/3 只清两列摘要（不可逆地放弃历史会话分组能力），用量数值本身不动。
--   4. 先试运行核对影响行数（语句 1 应约等于指标账本总行数，语句 2/3 为有摘要的
--      历史行数），确认后再正式执行；执行后无需 VACUUM，空间由 SQLite 自行复用。
-- ============================================================
DELETE FROM GatewayRequestMetrics;
UPDATE ProxyUsageLogs SET SessionDigest = NULL WHERE SessionDigest IS NOT NULL;
UPDATE ProxyUsageLogs SET ParentSessionDigest = NULL WHERE ParentSessionDigest IS NOT NULL;
