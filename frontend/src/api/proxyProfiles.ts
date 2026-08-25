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

export async function getProxyProfile(id: string): Promise<ProxyProfile> {
  const res = await httpGet<{ data: ProxyProfile }>(`/api/admin/developer/proxy-profiles/${id}`)
  return res.data
}

export async function createProxyProfile(payload: ProxyProfilePayload): Promise<{ id: string; key: string }> {
  const res = await httpPost<{ data: { id: string; key: string } }>('/api/admin/developer/proxy-profiles', payload)
  return res.data
}

export async function updateProxyProfile(id: string, payload: ProxyProfilePayload): Promise<void> {
  await httpPut(`/api/admin/developer/proxy-profiles/${id}`, payload)
}

export async function deleteProxyProfile(id: string): Promise<void> {
  await httpDelete(`/api/admin/developer/proxy-profiles/${id}`)
}

export async function testProxyProfile(request: TestProxyRequest): Promise<TestProxyResponse> {
  const res = await httpPost<{ data: TestProxyResponse }>('/api/admin/developer/proxy-profiles/test', request)
  return res.data
}
