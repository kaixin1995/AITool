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

export async function getHeaderProfile(id: string): Promise<HeaderProfile> {
  const res = await httpGet<{ data: HeaderProfile }>(`/api/admin/developer/header-profiles/${id}`)
  return res.data
}

export async function createHeaderProfile(payload: HeaderProfilePayload): Promise<{ id: string; key: string }> {
  const res = await httpPost<{ data: { id: string; key: string } }>('/api/admin/developer/header-profiles', payload)
  return res.data
}

export async function updateHeaderProfile(id: string, payload: HeaderProfilePayload): Promise<void> {
  await httpPut(`/api/admin/developer/header-profiles/${id}`, payload)
}

export async function deleteHeaderProfile(id: string): Promise<void> {
  await httpDelete(`/api/admin/developer/header-profiles/${id}`)
}

export async function previewHeaders(request: PreviewHeadersRequest): Promise<PreviewHeadersResponse> {
  const res = await httpPost<{ data: PreviewHeadersResponse }>('/api/admin/developer/header-profiles/preview', request)
  return res.data
}
