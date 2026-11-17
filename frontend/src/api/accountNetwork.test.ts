import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet, httpPost, httpPut } from './http'
import { accountProxySelectOptions, batchSaveAccountNetworkPolicies, getAccountNetworkCapabilities, getAccountNetworkSummaries, getAccountProxyOptions, networkSelectionValid, networkSummaryLabel, saveAccountNetworkPolicy, withNetworkSelectionQuery } from './accountNetwork'
vi.mock('./http', () => ({ httpGet: vi.fn(), httpPost: vi.fn(), httpPut: vi.fn() }))
beforeEach(() => vi.resetAllMocks())
const base = '/api/admin/oauth/network-policies'
describe('account network policies', () => {
  it('loads capabilities, sanitized options and all summaries without developer endpoints', async () => {
    await getAccountNetworkCapabilities()
    await getAccountProxyOptions()
    await getAccountNetworkSummaries()
    expect(httpGet).toHaveBeenNthCalledWith(1, `${base}/capabilities`, { skipErrorNotify: true })
    expect(httpGet).toHaveBeenNthCalledWith(2, `${base}/proxy-options`, { skipErrorNotify: true })
    expect(httpGet).toHaveBeenNthCalledWith(3, base, { skipErrorNotify: true })
  })
  it('scopes account identities by provider and sends numeric mode with optimistic revision', async () => {
    const account = { providerKey: 'google', accountId: 'shared-id' }
    await saveAccountNetworkPolicy(account, { mode: 2, proxyProfileId: 'stable-id' }, 'revision-1')
    expect(httpPut).toHaveBeenCalledWith(base, { ...account, networkSelection: { mode: 2, proxyProfileId: 'stable-id' }, expectedRevision: 'revision-1' })
    await saveAccountNetworkPolicy(account, { mode: 1 }, null)
    expect(httpPut).toHaveBeenLastCalledWith(base, { ...account, networkSelection: { mode: 1 } })
  })
  it('batch summaries and writes retain provider/account tuples and partial result details', async () => {
    const accounts = [{ providerKey: 'codex', accountId: 'same-id' }, { providerKey: 'device-oauth', accountId: 'same-id' }]
    await getAccountNetworkSummaries(accounts)
    expect(httpPost).toHaveBeenCalledWith(`${base}/summary`, { accounts }, { skipErrorNotify: true })
    const result = { succeeded: 1, failed: 1, results: [{ ...accounts[1], success: false, errorCode: 'proxy_profile_disabled', error: 'Disabled' }] }
    vi.mocked(httpPost).mockResolvedValueOnce(result)
    await expect(batchSaveAccountNetworkPolicies(accounts, { mode: 0 })).resolves.toEqual(result)
    expect(httpPost).toHaveBeenLastCalledWith(`${base}/batch`, { accounts, networkSelection: { mode: 0 } })
  })
  it('preserves legacy URLs when omitted and puts raw-import metadata only in query', () => {
    expect(withNetworkSelectionQuery('/import?name=a.json')).toBe('/import?name=a.json')
    expect(withNetworkSelectionQuery('/import', { mode: 1 })).toBe('/import?networkMode=1')
    expect(withNetworkSelectionQuery('/import?name=a.json', { mode: 2, proxyProfileId: 'stable-id' })).toBe('/import?name=a.json&networkMode=2&proxyProfileId=stable-id')
  })
  it('keeps disabled or missing current profiles visible without making them selectable', () => {
    const options = accountProxySelectOptions([
      { id: 'enabled', name: 'Enabled', isEnabled: true, revision: 1, hasCredentials: true },
      { id: 'disabled', name: 'Disabled', isEnabled: false, revision: 2, hasCredentials: false }
    ], 'missing-current')
    expect(options.find(p => p.value === 'enabled')?.disabled).toBe(false)
    expect(options.find(p => p.value === 'disabled')?.disabled).toBe(true)
    expect(options.find(p => p.value === 'missing-current')?.disabled).toBe(true)
  })
  it('never treats unloaded state as Legacy or an incomplete profile as valid', () => {
    expect(networkSelectionValid(undefined)).toBe(false)
    expect(networkSelectionValid({ mode: 2 })).toBe(false)
    expect(networkSelectionValid({ mode: 0 })).toBe(true)
    expect(networkSummaryLabel()).toContain('未加载')
    expect(networkSummaryLabel({ providerKey: 'codex', accountId: 'id', networkSelection: { mode: 2, proxyProfileId: 'profile' }, proxyProfileName: 'Proxy', blockReason: 'proxy_profile_disabled', revision: 'r' })).toContain('已阻断：proxy_profile_disabled')
  })
  it('labels mode 0 as 默认 without leaking implementation jargon', () => {
    expect(networkSummaryLabel({ providerKey: 'codex', accountId: 'id', networkSelection: { mode: 0 }, proxyProfileName: null, blockReason: null, revision: null })).toBe('默认')
  })
})
