import { httpGet, httpPost, httpPut, httpDelete } from './http'

// 与后端 SitesApiController 返回结构对齐。
export interface SiteListItem {
  id: string
  name: string
  baseUrl: string
  endpointPathMode: string
  apiKeyMasked: string
  keyCount: number
  supportsOpenAi: boolean
  supportsAnthropic: boolean
  supportsResponses: boolean
  protocolType: string
  clientEmulation?: string
  extraHeadersJson?: string
  egressProxyUrl?: string
  isEnabled: boolean
  createdAt: string
}

// 站点密钥列表项（KeyValue 脱敏）。
export interface SiteKeyItem {
  id: string
  keyValueMasked: string
  remark: string
  priority: number
  isEnabled: boolean
  createdAt: string
}

export interface SiteDetail {
  id: string
  name: string
  baseUrl: string
  endpointPathMode: string
  supportsOpenAi: boolean
  supportsAnthropic: boolean
  supportsResponses: boolean
  protocolType: string
  clientEmulation?: string
  extraHeadersJson?: string
  egressProxyUrl?: string
  isEnabled: boolean
  createdAt: string
  keys: SiteKeyItem[]
}

export interface SitePayload {
  name: string
  baseUrl: string
  endpointPathMode?: string
  apiKey: string
  supportsOpenAi?: boolean
  supportsAnthropic?: boolean
  supportsResponses?: boolean
  clientEmulation?: string
  extraHeadersJson?: string
  egressProxyUrl?: string
  isEnabled?: boolean
}

// 站点密钥新增/编辑请求。编辑时 keyValue 留空表示保留原值。
export interface SiteKeyPayload {
  keyValue: string
  remark?: string
  priority?: number
  isEnabled?: boolean
}

export interface RemoteModelInfo {
  remoteModelName: string
  existingMappingId?: string | null
  isEnabled: boolean
  existingDisplayName?: string | null
}

export interface SiteFetchResult {
  siteId: string
  siteName: string
  status: 'pending' | 'running' | 'success' | 'fail'
  error?: string | null
  models: RemoteModelInfo[]
}

export interface FetchAllProgress {
  taskId: string
  totalSites: number
  completedSites: number
  isCompleted: boolean
  createdAt?: string
  completedAt?: string | null
  sites: SiteFetchResult[]
}

export interface ModelSelectionItem {
  siteId: string
  remoteModelName: string
  displayName: string
  selected: boolean
}

export async function listSites(includeManaged = false): Promise<SiteListItem[]> {
  return httpGet<SiteListItem[]>(includeManaged ? '/api/admin/sites?includeManaged=true' : '/api/admin/sites')
}

export async function getSite(id: string): Promise<SiteDetail> {
  return httpGet<SiteDetail>(`/api/admin/sites/${id}`)
}

export async function createSite(payload: SitePayload): Promise<{ id: string }> {
  return httpPost<{ id: string }>('/api/admin/sites', payload)
}

export async function updateSite(id: string, payload: SitePayload): Promise<void> {
  await httpPut(`/api/admin/sites/${id}`, payload)
}

export async function toggleSite(id: string): Promise<{ isEnabled: boolean }> {
  return httpPost<{ isEnabled: boolean }>(`/api/admin/sites/${id}/toggle`)
}

export async function deleteSite(id: string): Promise<void> {
  await httpDelete(`/api/admin/sites/${id}`)
}

export async function bulkDeleteSites(siteIds: string[]): Promise<{ deletedCount: number }> {
  return httpPost<{ deletedCount: number }>('/api/admin/sites/bulk-delete', { siteIds })
}

export async function exportSites(): Promise<unknown[]> {
  return httpGet<unknown[]>('/api/admin/sites/export')
}

export async function importSites(items: SitePayload[]): Promise<{ importedCount: number }> {
  return httpPost<{ importedCount: number }>('/api/admin/sites/import', items)
}

export async function fetchSiteModels(siteId: string): Promise<RemoteModelInfo[] | { success: false; message: string }> {
  return httpGet<RemoteModelInfo[] | { success: false; message: string }>(`/api/admin/site-catalog/fetch-models/${siteId}`)
}

export async function fetchAllSiteModels(): Promise<{ taskId: string; message?: string }> {
  return httpPost<{ taskId: string; message?: string }>('/api/admin/site-catalog/fetch-all-models')
}

export async function getFetchAllProgress(taskId: string): Promise<FetchAllProgress> {
  return httpGet<FetchAllProgress>(`/api/admin/site-catalog/fetch-all-progress/${taskId}`)
}

export async function importSelectedModels(selections: ModelSelectionItem[]): Promise<{ importedCount: number }> {
  return httpPost<{ importedCount: number }>('/api/admin/site-catalog/import-selected', { selections })
}

// —— 站点密钥管理 ——

export async function listSiteKeys(siteId: string): Promise<SiteKeyItem[]> {
  return httpGet<SiteKeyItem[]>(`/api/admin/sites/${siteId}/keys`)
}

export async function createSiteKey(siteId: string, payload: SiteKeyPayload): Promise<{ id: string }> {
  return httpPost<{ id: string }>(`/api/admin/sites/${siteId}/keys`, payload)
}

export async function updateSiteKey(siteId: string, keyId: string, payload: SiteKeyPayload): Promise<void> {
  await httpPut(`/api/admin/sites/${siteId}/keys/${keyId}`, payload)
}

export async function deleteSiteKey(siteId: string, keyId: string): Promise<void> {
  await httpDelete(`/api/admin/sites/${siteId}/keys/${keyId}`)
}

export async function toggleSiteKey(siteId: string, keyId: string): Promise<{ isEnabled: boolean }> {
  return httpPost<{ isEnabled: boolean }>(`/api/admin/sites/${siteId}/keys/${keyId}/toggle`)
}
