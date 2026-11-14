import { httpGet, httpPost } from '@/api/http'

export interface SharedMcpServer {
  name: string
  url: string
  apiKey: string
  enabled: boolean
  allowedKeyIds: string[]
}

export interface DeveloperGatewaySettings {
  revision: string
  searchEndpoint: string
  searchApiKey: string
  visionRoute: string
  toolAllowedKeyIds: string[]
  mcpServers: SharedMcpServer[]
  [key: string]: unknown
}

export async function loadDeveloperGatewaySettings() {
  const [settings, status, keys] = await Promise.all([
    httpGet<DeveloperGatewaySettings>('/api/admin/gateway-enhancements', { skipErrorNotify: true }),
    httpGet<{ configurationError: string | null }>('/api/admin/gateway-enhancements/status', { skipErrorNotify: true }),
    httpGet<{ id: string; keyName: string }[]>('/api/admin/access-keys', { skipErrorNotify: true })
  ])
  if (status.configurationError) throw new Error(status.configurationError)
  return { settings, keyOptions: keys.map(key => ({ label: key.keyName, value: key.id })) }
}

/** Merge only this tab's fields into the latest snapshot; never overwrite a concurrent edit of the same fields. */
export async function saveDeveloperGatewayFields(
  original: DeveloperGatewaySettings,
  fields: Partial<DeveloperGatewaySettings>
): Promise<DeveloperGatewaySettings> {
  const [latest, status] = await Promise.all([
    httpGet<DeveloperGatewaySettings>('/api/admin/gateway-enhancements', { skipErrorNotify: true }),
    httpGet<{ configurationError: string | null }>('/api/admin/gateway-enhancements/status', { skipErrorNotify: true })
  ])
  if (status.configurationError) throw new Error(status.configurationError)
  const changesSecret = ('searchApiKey' in fields && fields.searchApiKey !== original.searchApiKey)
    || (fields.mcpServers?.some(server => {
      const before = original.mcpServers.find(item => item.name === server.name)
      return server.apiKey !== (before?.apiKey ?? '')
    }) ?? false)
  if (changesSecret && latest.revision !== original.revision) {
    throw new Error('配置已被其他页面修改，无法确认密钥是否变化。请重新加载后再修改密钥；当前输入已保留。')
  }
  for (const name of Object.keys(fields)) {
    if (JSON.stringify(latest[name]) !== JSON.stringify(original[name])) {
      throw new Error('这部分配置已被其他页面修改。请重新加载后再保存；当前输入已保留。')
    }
  }
  const merged = { ...latest, ...fields }
  const result = await httpPost<{ configuration: DeveloperGatewaySettings }>('/api/admin/gateway-enhancements', merged, { skipErrorNotify: true })
  // Adopt the exact saved, redacted snapshot, including pruned deleted-key references and new revision.
  return result.configuration
}

export function gatewayError(error: unknown): string {
  return error instanceof Error ? error.message : '操作失败，请稍后重试'
}
