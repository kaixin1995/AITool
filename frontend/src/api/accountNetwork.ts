import { ApiError, httpGet, httpPost, httpPut } from './http'

// Wire values match AccountNetworkSelection / NetworkMode in the backend.
export type NetworkMode = 0 | 1 | 2
export interface AccountNetworkSelection { mode: NetworkMode; proxyProfileId?: string | null }
export interface AccountIdentity { providerKey: string; accountId: string }
export interface AccountNetworkSummary extends AccountIdentity {
  networkSelection: AccountNetworkSelection
  revision: string | null
  proxyProfileName: string | null
  blockReason: string | null
}
export interface AccountProxyOption { id: string; name: string; isEnabled: boolean; revision: number; hasCredentials: boolean }
export interface AccountNetworkBatchResult {
  results: Array<AccountIdentity & { success: boolean; errorCode: string | null; error: string | null }>
  succeeded: number
  failed: number
}
const base = '/api/admin/oauth/network-policies'
export function getAccountNetworkCapabilities(): Promise<{ ready: boolean }> {
  return httpGet(`${base}/capabilities`, { skipErrorNotify: true })
}

// Only a capabilities 404 indicates an old server. Preserve downstream 404 details
// without presenting them as an ApiError that the legacy detection would accept.
export class NetworkPolicyDataNotFoundError extends Error {
  readonly status: number
  readonly errorCode: string
  readonly cause: ApiError
  constructor(cause: ApiError) {
    super(cause.message)
    this.name = 'NetworkPolicyDataNotFoundError'
    this.status = cause.status
    this.errorCode = cause.errorCode
    this.cause = cause
  }
}
async function loadNetworkPolicyData<T>(request: Promise<T>): Promise<T> {
  try {
    return await request
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) throw new NetworkPolicyDataNotFoundError(error)
    throw error
  }
}
export function getAccountProxyOptions(): Promise<AccountProxyOption[]> {
  return loadNetworkPolicyData(httpGet(`${base}/proxy-options`, { skipErrorNotify: true }))
}
export function getAccountNetworkSummaries(accounts?: AccountIdentity[]): Promise<AccountNetworkSummary[]> {
  return loadNetworkPolicyData(accounts ? httpPost(`${base}/summary`, { accounts }, { skipErrorNotify: true }) : httpGet(base, { skipErrorNotify: true }))
}
export function saveAccountNetworkPolicy(account: AccountIdentity, networkSelection: AccountNetworkSelection, expectedRevision?: string | null): Promise<AccountNetworkSummary> {
  return httpPut(base, { ...account, networkSelection, ...(expectedRevision != null ? { expectedRevision } : {}) })
}
export function batchSaveAccountNetworkPolicies(accounts: AccountIdentity[], networkSelection: AccountNetworkSelection): Promise<AccountNetworkBatchResult> {
  return httpPost(`${base}/batch`, { accounts, networkSelection })
}
// Raw credential bodies and multipart files remain untouched. Metadata lives in query parameters.
export function withNetworkSelectionQuery(url: string, selection?: AccountNetworkSelection): string {
  if (!selection) return url
  const query = new URLSearchParams({ networkMode: String(selection.mode) })
  if (selection.mode === 2 && selection.proxyProfileId) query.set('proxyProfileId', selection.proxyProfileId)
  return `${url}${url.includes('?') ? '&' : '?'}${query}`
}
export function accountProxySelectOptions(profiles: AccountProxyOption[], currentId?: string | null): Array<{ label: string; value: string; disabled: boolean }> {
  const options = profiles.map(p => ({ label: `${p.name}${p.hasCredentials ? '（已配置认证）' : ''}${p.isEnabled ? '' : '（已停用）'}`, value: p.id, disabled: !p.isEnabled }))
  if (currentId && !options.some(p => p.value === currentId)) options.push({ label: `当前方案不可用：${currentId}`, value: currentId, disabled: true })
  return options
}
export function networkSelectionValid(selection: AccountNetworkSelection | undefined): boolean {
  return !!selection && ([0, 1].includes(selection.mode) || (selection.mode === 2 && !!selection.proxyProfileId))
}
export function networkSummaryLabel(summary?: AccountNetworkSummary): string {
  if (!summary) return '网络策略未加载（不修改）'
  const mode = summary.networkSelection.mode
  const label = mode === 0 ? '默认' : mode === 1 ? '直连' : `代理：${summary.proxyProfileName ?? summary.networkSelection.proxyProfileId ?? '未知方案'}`
  return summary.blockReason ? `${label} · 已阻断：${summary.blockReason}` : label
}
