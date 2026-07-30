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
export interface ModelVendorDefinition {
  vendorName: string
  iconSvgBody: string
  headerBackground: string
  sortOrder: number
}
export interface ModelVendorRuleDefinition {
  vendorName: string
  matchType: string
  pattern: string
  priority: number
}
export interface ModelVendorCatalog {
  vendors: ModelVendorDefinition[]
  rules: ModelVendorRuleDefinition[]
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

// 模型详情 + 映射管理（原 Models/Edit 功能）
export interface ModelSiteMapping {
  mappingId: string
  siteId: string
  siteName: string
  remoteModelName: string
  isEnabled: boolean
  maxConcurrency: number
}
export interface ModelDetail {
  id: string
  modelName: string
  displayName: string
  isEnabled: boolean
  overrideReasoningEffort: string
  compatibilityProfileId: string | null
  siteMappings: ModelSiteMapping[]
  availableSites: { id: string; name: string }[]
}
export async function getModelDetail(id: string): Promise<ModelDetail> {
  return httpGet<ModelDetail>(`/api/admin/models/${id}`)
}
export async function addModelMapping(id: string, siteId: string, remoteModelName: string, isEnabled = true): Promise<void> {
  await httpPost(`/api/admin/models/${id}/mappings`, { siteId, remoteModelName, isEnabled })
}
export async function updateMappingConcurrency(mappingId: string, maxConcurrency: number): Promise<{ maxConcurrency: number }> {
  return httpPut<{ maxConcurrency: number }>(`/api/admin/models/mappings/${mappingId}/concurrency`, { maxConcurrency })
}
export async function deleteModelMapping(id: string, mappingId: string): Promise<void> {
  await httpDelete(`/api/admin/models/${id}/mappings/${mappingId}`)
}
