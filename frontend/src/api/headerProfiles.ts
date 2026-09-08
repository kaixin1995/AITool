import { httpGet, httpPost, httpPut, httpDelete } from './http'

export interface HeaderProfile {
  id: string
  key: string
  name: string
  description?: string | null
  headersJson?: string | null
  isBuiltIn: boolean
  isEnabled: boolean
  sortOrder: number
  createdAt: string
  updatedAt?: string | null
}

export interface HeaderProfilePayload {
  key: string
  name: string
  description?: string | null
  headersJson?: string | null
  isEnabled: boolean
  sortOrder: number
}

export interface PreviewHeadersRequest {
  emulationPreset?: string
  headersJson?: string | null
  modelName?: string | null
  projectId?: string | null
  isAntigravity?: boolean
}

export interface PreviewHeadersResponse {
  previewHeaders: Record<string, string>
  evaluatedCount: number
}

export async function getHeaderProfiles(): Promise<HeaderProfile[]> {
  return await httpGet<HeaderProfile[]>('/api/admin/developer/header-profiles')
}

// httpGet/httpPost 已解包 ApiResponse.data 外层，直接声明载荷类型即可；
// 再取 res.data 会拿到 undefined（双重解包）。
export async function getHeaderProfile(id: string): Promise<HeaderProfile> {
  return await httpGet<HeaderProfile>(`/api/admin/developer/header-profiles/${id}`)
}

export async function createHeaderProfile(payload: HeaderProfilePayload): Promise<{ id: string; key: string }> {
  return await httpPost<{ id: string; key: string }>('/api/admin/developer/header-profiles', payload)
}

export async function updateHeaderProfile(id: string, payload: HeaderProfilePayload): Promise<void> {
  await httpPut(`/api/admin/developer/header-profiles/${id}`, payload)
}

export async function deleteHeaderProfile(id: string): Promise<void> {
  await httpDelete(`/api/admin/developer/header-profiles/${id}`)
}

export async function previewHeaders(request: PreviewHeadersRequest): Promise<PreviewHeadersResponse> {
  return await httpPost<PreviewHeadersResponse>('/api/admin/developer/header-profiles/preview', request)
}

export interface AiLatestVersionResponse {
  success: boolean
  error?: string | null
  upToDate: boolean
  changed: boolean
  currentVersion: string
  latestVersion?: string | null
  note?: string | null
  /** 数据来源说明（如「数据来源：GitHub Releases: openai/codex」）；无公开源时为 null。 */
  sourceNote?: string | null
  currentHeadersJson?: string | null
  proposedHeadersJson?: string | null
}

// AI 查询该方案客户端的最新版本（耗时较长，单独放宽超时）。
// httpPost 已解包 ApiResponse 外层，直接返回载荷（载荷自带 success 字段表示业务成败）。
export async function aiFetchLatestVersion(id: string): Promise<AiLatestVersionResponse> {
  return await httpPost<AiLatestVersionResponse>(
    `/api/admin/developer/header-profiles/${id}/ai-latest-version`,
    {},
    { timeout: 120000 }
  )
}
