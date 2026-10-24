import { httpGet, httpPost, httpPut, httpDelete } from './http'

export interface DeviceOAuthVendor {
  vendorKey: string
  label: string
  chatSupported: boolean
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

export async function fetchDeviceOAuthVendors(): Promise<DeviceOAuthVendor[]> {
  return httpGet<DeviceOAuthVendor[]>('/api/admin/device-oauth/vendors')
}

export async function fetchDeviceOAuthAccounts(): Promise<DeviceOAuthAccount[]> {
  return httpGet<DeviceOAuthAccount[]>('/api/admin/device-oauth/accounts')
}

export async function startDeviceOAuthSignIn(vendor: string): Promise<DeviceOAuthStartResult> {
  return httpPost<DeviceOAuthStartResult>('/api/admin/device-oauth/sign-in/start', { vendor })
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

export async function updateDeviceOAuthAccount(id: string, displayName: string, refreshToken?: string): Promise<void> {
  await httpPut(`/api/admin/device-oauth/accounts/${id}`, { displayName, refreshToken })
}

export async function refreshDeviceOAuthQuota(id: string): Promise<void> {
  await httpPost(`/api/admin/device-oauth/accounts/${id}/refresh-quota`)
}

export async function deleteDeviceOAuthAccount(id: string): Promise<void> {
  await httpDelete(`/api/admin/device-oauth/accounts/${id}`)
}
