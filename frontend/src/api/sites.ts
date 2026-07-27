import { httpGet, httpPost, httpPut, httpDelete } from './http'

// 与后端 SitesApiController 返回结构对齐。
export interface SiteListItem {
  id: string
  name: string
  baseUrl: string
  endpointPathMode: string
  apiKeyMasked: string
  supportsOpenAi: boolean
  supportsAnthropic: boolean
  protocolType: string
  isEnabled: boolean
  createdAt: string
}

export interface SiteDetail {
  id: string
  name: string
  baseUrl: string
  endpointPathMode: string
  apiKey: string
  supportsOpenAi: boolean
  supportsAnthropic: boolean
  protocolType: string
  isEnabled: boolean
  createdAt: string
}

export interface SitePayload {
  name: string
  baseUrl: string
  endpointPathMode?: string
  apiKey: string
  supportsOpenAi?: boolean
  supportsAnthropic?: boolean
  isEnabled?: boolean
}

export async function listSites(): Promise<SiteListItem[]> {
  return httpGet<SiteListItem[]>('/api/admin/sites')
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
