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
  clientEmulation?: string
  extraHeadersJson?: string
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
export async function clearAllModels(): Promise<{ deletedModels: number; deletedMappings: number; deletedMonitors: number }> {
  return httpPost<{ deletedModels: number; deletedMappings: number; deletedMonitors: number }>('/api/admin/models/clear-all')
}
export async function getVendorCatalog(): Promise<ModelVendorCatalog> {
  return httpGet<ModelVendorCatalog>('/api/admin/models/vendor-catalog')
}
export async function saveVendorCatalog(payload: ModelVendorCatalog): Promise<void> {
  await httpPut('/api/admin/models/vendor-catalog', payload)
}

// 模型价格表（本地 JSON，查询时动态计价）
export interface ModelOffPeakPricing {
  input: number
  output: number
  cacheRead: number
  cacheWrite?: number | null
}
export interface ModelPriceEntry {
  id: string
  displayName: string
  /** 按厂商规则解析出的厂商名（GET 时服务端填充；保存时忽略） */
  vendorName?: string | null
  /** USD / 百万 tokens（基准/高峰档） */
  input: number
  output: number
  cacheRead: number
  cacheWrite: number
  offPeak?: ModelOffPeakPricing | null
  /** 高峰时段窗口（HH:mm-HH:mm，支持跨午夜）；窗口内用基准价，窗口外用低峰价 */
  peakWindows?: string[] | null
  peakTimeZoneOffsetMinutes?: number
}
export interface ModelPricingCatalog {
  usdToCny: number
  models: ModelPriceEntry[]
}
export async function getModelPricing(): Promise<ModelPricingCatalog> {
  return httpGet<ModelPricingCatalog>('/api/admin/models/pricing')
}
export async function saveModelPricing(payload: ModelPricingCatalog): Promise<void> {
  await httpPut('/api/admin/models/pricing', payload)
}

// 模型详情 + 映射管理（原 Models/Edit 功能）
export interface ModelSiteMapping {
  mappingId: string
  siteId: string
  siteName: string
  remoteModelName: string
  isEnabled: boolean
  maxConcurrency: number
  clientEmulation?: string
  extraHeadersJson?: string
  egressProxyUrl?: string
}
export interface UpdateMappingPayload {
  remoteModelName?: string
  isEnabled?: boolean
  maxConcurrency?: number
  clientEmulation?: string
  extraHeadersJson?: string
  egressProxyUrl?: string
}
export interface ModelDetail {
  id: string
  modelName: string
  displayName: string
  isEnabled: boolean
  overrideReasoningEffort: string
  compatibilityProfileId: string | null
  clientEmulation?: string
  extraHeadersJson?: string
  siteMappings: ModelSiteMapping[]
  availableSites: { id: string; name: string }[]
}
export async function getModelDetail(id: string): Promise<ModelDetail> {
  return httpGet<ModelDetail>(`/api/admin/models/${id}`)
}
export async function addModelMapping(
  id: string,
  siteId: string,
  remoteModelName: string,
  maxConcurrency = 0,
  clientEmulation = 'None',
  extraHeadersJson?: string,
  egressProxyUrl?: string,
  isEnabled = true
): Promise<void> {
  await httpPost(`/api/admin/models/${id}/mappings`, {
    siteId,
    remoteModelName,
    maxConcurrency,
    clientEmulation,
    extraHeadersJson,
    egressProxyUrl,
    isEnabled
  })
}
export async function updateModelMapping(mappingId: string, payload: UpdateMappingPayload): Promise<ModelSiteMapping> {
  return httpPut<ModelSiteMapping>(`/api/admin/models/mappings/${mappingId}`, payload)
}
export async function updateMappingConcurrency(mappingId: string, maxConcurrency: number): Promise<{ maxConcurrency: number }> {
  return httpPut<{ maxConcurrency: number }>(`/api/admin/models/mappings/${mappingId}/concurrency`, { maxConcurrency })
}
export async function deleteModelMapping(id: string, mappingId: string): Promise<void> {
  await httpDelete(`/api/admin/models/${id}/mappings/${mappingId}`)
}
