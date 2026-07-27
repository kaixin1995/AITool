import { httpGet, httpPost, httpPut, httpDelete } from './http'

export interface ModelListItem {
  id: string
  modelName: string
  displayName: string
  isEnabled: boolean
  overrideReasoningEffort: string
  compatibilityProfileId: string | null
  siteCount: number
}
export interface ModelVendorGroup {
  vendorName: string
  iconSvgBody: string
  headerBackground: string
  models: ModelListItem[]
}
export interface ModelListResponse {
  vendorGroups: ModelVendorGroup[]
}
export interface ModelPayload {
  modelName: string
  displayName?: string
  isEnabled?: boolean
  overrideReasoningEffort?: string
  compatibilityProfileId?: string | null
}

export async function listModels(): Promise<ModelListResponse> {
  return httpGet<ModelListResponse>('/api/admin/models')
}

export async function createModel(payload: ModelPayload): Promise<{ id: string }> {
  return httpPost<{ id: string }>('/api/admin/models', payload)
}

export async function updateModel(id: string, payload: ModelPayload): Promise<void> {
  await httpPut(`/api/admin/models/${id}`, payload)
}

export async function toggleModel(id: string): Promise<{ isEnabled: boolean }> {
  return httpPost<{ isEnabled: boolean }>(`/api/admin/models/${id}/toggle`)
}

export async function deleteModel(id: string): Promise<void> {
  await httpDelete(`/api/admin/models/${id}`)
}
