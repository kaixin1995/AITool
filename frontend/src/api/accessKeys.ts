import { httpGet, httpPost } from './http'

export interface AccessKeyItem {
  id: string
  keyName: string
  maskedValue: string
  isEnabled: boolean
  allowedRouteNames: string
  createdAt: string
}

export async function listAccessKeys(): Promise<AccessKeyItem[]> {
  return httpGet<AccessKeyItem[]>('/api/admin/access-keys')
}

export async function createAccessKey(keyName: string, allowedRouteNames?: string[]): Promise<{ plainKey: string }> {
  return httpPost<{ plainKey: string }>('/api/admin/access-keys/create', { keyName, allowedRouteNames })
}

export async function toggleAccessKey(keyId: string): Promise<void> {
  await httpPost(`/api/admin/access-keys/toggle/${keyId}`)
}

export async function deleteAccessKey(keyId: string): Promise<void> {
  await httpPost(`/api/admin/access-keys/delete/${keyId}`)
}

export async function updateAccessKeyRoutes(keyId: string, allowedRouteNames: string[]): Promise<void> {
  await httpPost(`/api/admin/access-keys/update-routes/${keyId}`, { allowedRouteNames })
}
