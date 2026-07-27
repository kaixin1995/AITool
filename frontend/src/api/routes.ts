import { httpGet, httpPost } from './http'

export interface RouteEntry {
  entryName: string
  candidateCount: number
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
  availabilityMode: string
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
}
export async function saveRouteRules(modelName: string, rules: SaveRuleItem[]): Promise<void> {
  await httpPost('/api/admin/route-rules/save', { modelName, rules })
}
export async function toggleRouteRule(ruleId: string): Promise<void> {
  await httpPost(`/api/admin/route-rules/toggle/${ruleId}`)
}
export async function deleteRouteRule(ruleId: string): Promise<void> {
  await httpPost(`/api/admin/route-rules/delete/${ruleId}`)
}
