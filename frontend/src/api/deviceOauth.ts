import { getAuthSessionKey, httpGet, httpPost, httpPut, httpDelete } from './http'
import type { AccountNetworkSelection } from './accountNetwork'

export interface DeviceOAuthVendor {
  vendorKey: string
  label: string
  chatSupported: boolean
  modelFetchSupported: boolean
  modelCatalogScope?: 'account' | 'public'
}

export function usesPublicModelCatalog(provider: string, vendors: DeviceOAuthVendor[]): boolean {
  return vendors.some(v => v.vendorKey === provider && v.modelCatalogScope === 'public')
}

const builtInModelProviders = new Set(['codex', 'antigravity', 'kimi', 'xai'])

// 卡片、表格与弹窗共用能力判断；未知/静态目录厂商不能冒充支持动态拉取。
export function supportsOAuthModelFetch(provider: string, vendors: DeviceOAuthVendor[]): boolean {
  return builtInModelProviders.has(provider)
    || vendors.some(v => v.vendorKey === provider && v.modelFetchSupported)
}

export function supportsOAuthModelImport(
  account: { provider: string; modelImportSupported?: boolean } | null,
  vendors: DeviceOAuthVendor[]
): boolean {
  if (!account) return false
  return builtInModelProviders.has(account.provider)
    || (supportsOAuthModelFetch(account.provider, vendors)
      && vendors.some(v => v.vendorKey === account.provider && v.chatSupported)
      && account.modelImportSupported === true)
}

export interface DeviceOAuthWindow {
  id: string
  label: string
  usedPercent: number | null
  resetLabel: string
  resetAtUtc: string | null
  limitWindowSeconds: number | null
}

export interface DeviceOAuthAccount {
  id: string
  vendorKey: string
  vendorLabel: string
  displayName: string
  email: string | null
  isEnabled: boolean
  manuallyDisabled: boolean
  disabledByFeatureToggle: boolean
  requiresReauth: boolean
  tokenExpiresAt: string | null
  linkedSiteId: string
  modelImportSupported: boolean
  createdAt: string
  lastQuotaCheckedAt: string | null
  planType: string | null
  windows: DeviceOAuthWindow[]
}

export interface DeviceOAuthStartResult {
  sessionId: string
  authorizeUrl: string
  userCode: string | null
  instructions: string
}

let vendorsSessionKey: string | null = null
let vendorsInFlight: Promise<DeviceOAuthVendor[]> | null = null
let emptyVendorsCachedUntil = 0
const emptyVendorsCacheMs = 60_000

export function fetchDeviceOAuthVendors(): Promise<DeviceOAuthVendor[]> {
  const sessionKey = getAuthSessionKey()
  if (sessionKey !== vendorsSessionKey) {
    vendorsSessionKey = sessionKey
    vendorsInFlight = null
    emptyVendorsCachedUntil = 0
  }
  if (vendorsInFlight) return vendorsInFlight
  // 空清单也是成功结果，短暂缓存以避免账号轮询持续补拉；限时缓存允许后端配置变更后恢复。
  if (Date.now() < emptyVendorsCachedUntil) return Promise.resolve([])

  const request = httpGet<DeviceOAuthVendor[]>('/api/admin/device-oauth/vendors')
    .then(vendors => {
      if (vendorsSessionKey === sessionKey && getAuthSessionKey() === sessionKey) {
        emptyVendorsCachedUntil = vendors.length === 0 ? Date.now() + emptyVendorsCacheMs : 0
      }
      return vendors
    })
    .finally(() => {
      if (vendorsInFlight === request) vendorsInFlight = null
    })
  vendorsInFlight = request
  return request
}

export async function fetchDeviceOAuthAccounts(): Promise<DeviceOAuthAccount[]> {
  return httpGet<DeviceOAuthAccount[]>('/api/admin/device-oauth/accounts')
}

export async function startDeviceOAuthSignIn(vendor: string, networkSelection?: AccountNetworkSelection): Promise<DeviceOAuthStartResult> {
  return httpPost<DeviceOAuthStartResult>('/api/admin/device-oauth/sign-in/start', { vendor, ...(networkSelection ? { networkSelection } : {}) })
}

export async function pollDeviceOAuthSignIn(
  sessionId: string,
  displayName?: string,
  callbackUrl?: string
): Promise<{ status: 'pending' | 'success' | 'failed'; error?: string; accountId?: string; nextUrl?: string | null; message?: string | null }> {
  return httpPost('/api/admin/device-oauth/sign-in/poll', { sessionId, displayName, callbackUrl })
}

export async function toggleDeviceOAuthAccount(id: string, enabled: boolean): Promise<void> {
  await httpPost(`/api/admin/device-oauth/accounts/${id}/toggle`, { enabled })
}

export async function updateDeviceOAuthAccount(id: string, displayName?: string, refreshToken?: string): Promise<void> {
  await httpPut(`/api/admin/device-oauth/accounts/${id}`, { displayName, refreshToken })
}

export async function refreshDeviceOAuthQuota(id: string): Promise<void> {
  await httpPost(`/api/admin/device-oauth/accounts/${id}/refresh-quota`)
}

export async function deleteDeviceOAuthAccount(id: string): Promise<void> {
  await httpDelete(`/api/admin/device-oauth/accounts/${id}`)
}

export interface DeviceOAuthRemoteModelItem {
  remoteModelName: string
  displayName: string
  existingMappingId: string | null
  isEnabled: boolean
  existingDisplayName: string | null
}

export async function fetchDeviceOAuthModels(id: string): Promise<DeviceOAuthRemoteModelItem[]> {
  return httpGet<DeviceOAuthRemoteModelItem[]>(`/api/admin/device-oauth/accounts/${id}/fetch-models`)
}

export async function importSelectedDeviceModels(
  id: string,
  selections: Array<{ remoteModelName: string; displayName: string; selected: boolean }>
): Promise<void> {
  await httpPost(`/api/admin/device-oauth/accounts/${id}/import-models`, { models: selections })
}

/** 批量应用模型选择的逐账号结果（同组账号）。 */
export interface DeviceBatchApplyResult {
  results: Array<{ accountId: string; displayName: string | null; ok: boolean; changed: number; error: string | null }>
  succeeded: number
  failed: number
}

/** 设备 OAuth：仅同步源账号，其他账号因权限无法验证而跳过。 */
export async function batchApplyDeviceModels(
  sourceAccountId: string,
  selections: Array<{ remoteModelName: string; displayName: string; selected: boolean }>
): Promise<DeviceBatchApplyResult> {
  return httpPost<DeviceBatchApplyResult>('/api/admin/device-oauth/accounts/batch-apply-models', { sourceAccountId, models: selections })
}
