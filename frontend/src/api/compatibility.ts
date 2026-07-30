import { httpGet, httpPost, httpPut, httpDelete } from './http'

export interface CompatibilityProfileListItem {
  id: string
  name: string
  description: string
  isEnabled: boolean
  ruleCount: number
  createdAt: string
  updatedAt: string
}
export interface CompatibilityProfileDetail {
  id: string
  name: string
  description: string
  rulesJson: string
  isEnabled: boolean
}

export async function listProfiles(): Promise<CompatibilityProfileListItem[]> {
  return httpGet<CompatibilityProfileListItem[]>('/api/admin/compatibility-profiles')
}
export async function getProfile(id: string): Promise<CompatibilityProfileDetail> {
  return httpGet<CompatibilityProfileDetail>(`/api/admin/compatibility-profiles/${id}`)
}
export interface CompatibilityProfilePayload {
  name: string
  description?: string
  rulesJson?: string
  isEnabled: boolean
}

export async function createProfile(payload: CompatibilityProfilePayload): Promise<void> {
  await httpPost('/api/admin/compatibility-profiles', payload)
}
export async function updateProfile(id: string, payload: CompatibilityProfilePayload): Promise<void> {
  await httpPut(`/api/admin/compatibility-profiles/${id}`, payload)
}
export async function toggleProfile(id: string): Promise<void> {
  await httpPost(`/api/admin/compatibility-profiles/${id}/toggle`)
}
export async function deleteProfile(id: string): Promise<void> {
  await httpDelete(`/api/admin/compatibility-profiles/${id}`)
}
