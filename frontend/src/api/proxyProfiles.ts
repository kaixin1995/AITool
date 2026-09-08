import { httpGet, httpPost, httpPut, httpDelete } from './http'

export interface ProxyProfile {
  id: string
  key: string
  name: string
  proxyUrl: string
  description?: string | null
  isEnabled: boolean
  sortOrder: number
  createdAt: string
  updatedAt?: string | null
}

export interface ProxyProfilePayload {
  key: string
  name: string
  proxyUrl: string
  description?: string | null
  isEnabled: boolean
  sortOrder: number
}

export interface TestProxyRequest {
  proxyUrl: string
  targetUrl?: string | null
}

export interface TestProxyResponse {
  isSuccess: boolean
  statusCode: number
  latencyMs: number
  errorMessage?: string | null
  targetUrl: string
}

export async function getProxyProfiles(): Promise<ProxyProfile[]> {
  return await httpGet<ProxyProfile[]>('/api/admin/developer/proxy-profiles')
}

// httpGet/httpPost 已解包 ApiResponse.data 外层，直接声明载荷类型（避免双重解包拿 undefined）。
export async function getProxyProfile(id: string): Promise<ProxyProfile> {
  return await httpGet<ProxyProfile>(`/api/admin/developer/proxy-profiles/${id}`)
}

export async function createProxyProfile(payload: ProxyProfilePayload): Promise<{ id: string; key: string }> {
  return await httpPost<{ id: string; key: string }>('/api/admin/developer/proxy-profiles', payload)
}

export async function updateProxyProfile(id: string, payload: ProxyProfilePayload): Promise<void> {
  await httpPut(`/api/admin/developer/proxy-profiles/${id}`, payload)
}

export async function deleteProxyProfile(id: string): Promise<void> {
  await httpDelete(`/api/admin/developer/proxy-profiles/${id}`)
}

export async function testProxyProfile(request: TestProxyRequest): Promise<TestProxyResponse> {
  return await httpPost<TestProxyResponse>('/api/admin/developer/proxy-profiles/test', request)
}
