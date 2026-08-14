import { httpGet, httpPost } from './http'

export const routeAvailabilityModes = [
  'AllDay',
  'AvailableOnly',
  'Unavailable'
] as const

export type RouteAvailabilityMode = (typeof routeAvailabilityModes)[number]

export const routeAvailabilityOptions: Array<{
  label: string
  value: RouteAvailabilityMode
}> = [
  { label: '全天可用', value: 'AllDay' },
  { label: '仅指定时间可用', value: 'AvailableOnly' },
  { label: '指定时间不可用', value: 'Unavailable' }
]

export interface RouteEntry {
  entryName: string
  displayName?: string | null
  candidateCount: number
}
export interface SiteInstanceItem {
  siteId: string
  siteName: string
  siteModelName: string
  protocolType: string
  siteEnabled: boolean
}
export interface RouteRuleItem {
  ruleId: string
  siteId: string
  siteName: string
  siteEnabled: boolean
  upstreamModelName: string
  siteModelName: string
  priority: number
  modelPriority: number
  instancePriority: number
  isEnabled: boolean
  availabilityMode: RouteAvailabilityMode
  timeRangesJson: string
}
export interface RouteModelItem {
  modelName: string
  displayName: string
  siteCount: number
  hasRouteRules: boolean
}
export interface DiscoveredSite {
  siteId: string
  siteName: string
  remoteModelName: string
  siteEnabled: boolean
}

export async function getRouteEntries(): Promise<RouteEntry[]> {
  return httpGet<RouteEntry[]>('/api/admin/route-rules/entries')
}
export async function createRouteEntry(entryName: string): Promise<void> {
  await httpPost('/api/admin/route-rules/entries', { entryName })
}
export async function deleteRouteEntry(entryName: string): Promise<void> {
  await httpPost('/api/admin/route-rules/entries/delete', { entryName })
}
export async function getRouteSiteInstances(): Promise<SiteInstanceItem[]> {
  return httpGet<SiteInstanceItem[]>('/api/admin/route-rules/site-instances')
}
export async function getRouteModels(): Promise<RouteModelItem[]> {
  return httpGet<RouteModelItem[]>('/api/admin/route-rules/models')
}
export async function discoverSites(modelName: string): Promise<DiscoveredSite[]> {
  return httpGet<DiscoveredSite[]>(`/api/admin/route-rules/discover-sites?modelName=${encodeURIComponent(modelName)}`)
}
export async function getRouteRules(modelName: string): Promise<RouteRuleItem[]> {
  return httpGet<RouteRuleItem[]>(`/api/admin/route-rules/list?modelName=${encodeURIComponent(modelName)}`)
}
export interface SaveRuleItem {
  siteId: string
  siteModelName: string
  upstreamModelName: string
  isEnabled: boolean
  // 可用性模式与后端 RouteRules API 契约保持一致。
  availabilityMode?: RouteAvailabilityMode
  // 时间段 JSON：[{start:"HH:mm", end:"HH:mm"}]，availabilityMode 为 AllDay 时为空
  timeRangesJson?: string
}
export interface SaveRouteRulesResponse {
  message: string
}
export async function saveRouteRules(
  modelName: string,
  rules: SaveRuleItem[]
): Promise<SaveRouteRulesResponse> {
  // 后端字段名为 externalModelName（对应 ProxyRouteEntry.EntryName），不是 modelName
  return httpPost<SaveRouteRulesResponse>(
    '/api/admin/route-rules/save',
    { externalModelName: modelName, rules }
  )
}
export async function toggleRouteRule(ruleId: string): Promise<void> {
  await httpPost(`/api/admin/route-rules/toggle/${ruleId}`)
}
export async function deleteRouteRule(ruleId: string): Promise<void> {
  await httpPost(`/api/admin/route-rules/delete/${ruleId}`)
}
