import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpPost, httpPut } from './http'
import { createProxyProfile, testProxyProfile, updateProxyProfile } from './proxyProfiles'
vi.mock('./http', () => ({ httpGet: vi.fn(), httpPost: vi.fn(), httpPut: vi.fn(), httpDelete: vi.fn() }))
beforeEach(() => vi.resetAllMocks())
const payload = { key: 'proxy', name: 'Proxy', proxyUrl: 'http://localhost:7890', isEnabled: true, sortOrder: 1 }
describe('proxy profile secret contract', () => {
  it('probes by stable profile ID so protected credentials stay server-side', async () => {
    await testProxyProfile({ profileId: 'profile-id' })
    expect(httpPost).toHaveBeenCalledWith('/api/admin/developer/proxy-profiles/test', { profileId: 'profile-id' })
  })
  it('explicitly distinguishes Keep, Replace and Clear and carries the revision', async () => {
    await updateProxyProfile('profile-id', { ...payload, revision: 3, credentialAction: 'Keep' })
    expect(httpPut).toHaveBeenLastCalledWith('/api/admin/developer/proxy-profiles/profile-id', { ...payload, revision: 3, credentialAction: 'Keep' })
    await createProxyProfile({ ...payload, credentialAction: 'Replace', username: 'new-user', password: 'new-password' })
    expect(httpPost).toHaveBeenLastCalledWith('/api/admin/developer/proxy-profiles', { ...payload, credentialAction: 'Replace', username: 'new-user', password: 'new-password' })
    await updateProxyProfile('profile-id', { ...payload, revision: 4, credentialAction: 'Clear' })
    expect(httpPut).toHaveBeenLastCalledWith('/api/admin/developer/proxy-profiles/profile-id', { ...payload, revision: 4, credentialAction: 'Clear' })
  })
})
