import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet, httpPost } from '@/api/http'
import { saveDeveloperGatewayFields, type DeveloperGatewaySettings } from './developerGatewaySettings'

vi.mock('@/api/http', () => ({ httpGet: vi.fn(), httpPost: vi.fn() }))
function snapshot(changes: Partial<DeveloperGatewaySettings> = {}): DeveloperGatewaySettings {
  return {
    revision: 'initial', searchEndpoint: '', searchApiKey: '********', visionRoute: '',
    toolAllowedKeyIds: [], mcpServers: [], budgets: [{ keyId: 'key', tokenLimit: 123 }],
    keepAliveEnabled: true, ...changes
  }
}
function latest(settings: DeveloperGatewaySettings) {
  vi.mocked(httpGet).mockImplementation(async url => url.endsWith('/status') ? { configurationError: null } : settings)
}
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(httpPost).mockImplementation(async (_url, body) => {
    const saved = body as DeveloperGatewaySettings
    return { configuration: {
      ...saved, revision: 'after-save', searchApiKey: saved.searchApiKey ? '********' : '',
      mcpServers: saved.mcpServers.map(server => ({ ...server, apiKey: server.apiKey ? '********' : '' }))
    } }
  })
})

describe('developer gateway section saves', () => {
  it('preserves another page’s newer budget and stability settings', async () => {
    const current = snapshot({ revision: 'newer', budgets: [{ keyId: 'key', tokenLimit: 456 }], keepAliveEnabled: false })
    latest(current)
    await saveDeveloperGatewayFields(snapshot(), { visionRoute: 'vision' })
    expect(httpPost).toHaveBeenCalledWith('/api/admin/gateway-enhancements', {
      ...current, visionRoute: 'vision'
    }, { skipErrorNotify: true })
  })
  it('refuses concurrent changes within the edited section', async () => {
    latest(snapshot({ revision: 'newer', visionRoute: 'other-route' }))
    await expect(saveDeveloperGatewayFields(snapshot(), { visionRoute: 'my-route' })).rejects.toThrow('这部分配置已被其他页面修改')
    expect(httpPost).not.toHaveBeenCalled()
  })
  it.each(['', 'replacement'])('refuses a search secret change after the revision changed (%s)', async secret => {
    latest(snapshot({ revision: 'newer' }))
    await expect(saveDeveloperGatewayFields(snapshot(), { searchApiKey: secret })).rejects.toThrow('无法确认密钥是否变化')
    expect(httpPost).not.toHaveBeenCalled()
  })
  it('refuses an MCP secret change hidden behind a concurrent masked secret', async () => {
    const servers = [{ name: 'docs', url: 'https://example/mcp', apiKey: '********', enabled: true, allowedKeyIds: ['key'] }]
    latest(snapshot({ revision: 'newer', mcpServers: servers }))
    await expect(saveDeveloperGatewayFields(snapshot({ mcpServers: servers }), {
      mcpServers: [{ ...servers[0]!, apiKey: 'replacement' }]
    })).rejects.toThrow('无法确认密钥是否变化')
    expect(httpPost).not.toHaveBeenCalled()
  })
  it('normalizes saved secrets so subsequent unrelated edits can be saved', async () => {
    latest(snapshot())
    const saved = await saveDeveloperGatewayFields(snapshot(), { searchApiKey: 'replacement' })
    expect(saved.searchApiKey).toBe('********')
    expect(saved.revision).toBe('after-save')
    latest(snapshot({ revision: 'after-save' }))
    await expect(saveDeveloperGatewayFields(saved, { searchApiKey: 'another-secret', visionRoute: 'vision' })).resolves.toMatchObject({ visionRoute: 'vision', searchApiKey: '********' })
  })
  it('adopts server-pruned key references so the next section save does not falsely conflict', async () => {
    const server = { name: 'docs', url: 'https://example/mcp', apiKey: '********', enabled: true, allowedKeyIds: ['deleted'] }
    const original = snapshot({ toolAllowedKeyIds: ['deleted'], mcpServers: [server] })
    latest(original)
    const actual = snapshot({ revision: 'after-save', toolAllowedKeyIds: [], mcpServers: [{ ...server, allowedKeyIds: [] }] })
    vi.mocked(httpPost).mockResolvedValue({ configuration: actual })
    const saved = await saveDeveloperGatewayFields(original, { toolAllowedKeyIds: ['deleted'] })
    expect(saved.toolAllowedKeyIds).toEqual([])
    expect(saved.mcpServers[0]?.allowedKeyIds).toEqual([])
    latest(actual)
    await expect(saveDeveloperGatewayFields(saved, { toolAllowedKeyIds: [], mcpServers: saved.mcpServers })).resolves.toEqual(actual)
  })
})
