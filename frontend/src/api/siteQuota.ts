import { httpGet, httpPost } from './http'

// 与后端 SitesApiController 额度端点返回结构对齐（camelCase 序列化）。

/** 额度窗口（如「5 小时窗口」「每周额度」）。 */
export interface SiteQuotaWindow {
  id: string
  label: string
  /** 已用百分比（0-100，忠实搬运不裁剪）。 */
  usedPercent: number
  /** 重置时间简短展示（后端本地时区格式化），无重置时间为 null。 */
  resetLabel: string | null
  /** 重置时间（UTC ISO），供前端做相对倒计时。 */
  resetAtUtc: string | null
}

/** 账户余额（按量计费供应商如 DeepSeek）。 */
export interface SiteQuotaBalance {
  currency: string
  totalBalance: number
  grantedBalance: number | null
  toppedUpBalance: number | null
}

/** 单个密钥的额度信息（密钥值脱敏）。 */
export interface SiteQuotaKey {
  keyId: string
  keyValueMasked: string
  remark: string | null
  priority: number
  isEnabled: boolean
  /** never=从未查询，ok=成功，invalid_credential=密钥失效，error=其他错误。 */
  status: 'never' | 'ok' | 'invalid_credential' | 'error'
  level: string | null
  error: string | null
  checkedAtUtc: string | null
  /** 额度窗口；失败时为上次成功值（UI 置灰展示）。 */
  windows: SiteQuotaWindow[]
  /** 账户余额（余额型供应商如 DeepSeek）；失败时为上次成功值。 */
  balances: SiteQuotaBalance[]
}

/** 单个站点（及其全部密钥）的额度信息。 */
export interface SiteQuotaSite {
  siteId: string
  siteName: string
  baseUrl: string
  providerKey: string
  providerLabel: string
  keys: SiteQuotaKey[]
}

/** 额度总览（纯缓存，不触发上游查询）。 */
export function fetchSiteQuotaOverview(): Promise<SiteQuotaSite[]> {
  return httpGet<SiteQuotaSite[]>('/api/admin/sites/quota/overview')
}

/** 刷新单个站点全部密钥的额度（实时查询上游并落库）。 */
export function refreshSiteQuota(siteId: string): Promise<SiteQuotaSite> {
  return httpPost<SiteQuotaSite>(`/api/admin/sites/${siteId}/quota/refresh`, null)
}
