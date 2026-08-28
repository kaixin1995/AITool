import { httpGet, httpPost } from './http'

export interface AccessKeyItem {
  id: string
  keyName: string
  maskedValue: string
  isEnabled: boolean
  // 后端返回 List<string>，空列表=允许全部路由；为兼容旧数据也可能是 JSON 字符串。
  allowedRouteNames: string[] | string
}

export async function listAccessKeys(): Promise<AccessKeyItem[]> {
  return httpGet<AccessKeyItem[]>('/api/admin/access-keys')
}

// 按需获取单条密钥的完整明文（列表接口出于安全只返回脱敏值）。
export async function getAccessKeyPlain(keyId: string): Promise<{ plainKey: string }> {
  return httpGet<{ plainKey: string }>(`/api/admin/access-keys/${keyId}/plain`)
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
