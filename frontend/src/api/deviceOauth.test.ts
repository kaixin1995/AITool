import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet, httpPost } from './http'
import {
  fetchDeviceOAuthModels,
  startDeviceOAuthSignIn,
  importSelectedDeviceModels,
  supportsOAuthModelFetch,
  supportsOAuthModelImport,
  usesPublicModelCatalog,
  type DeviceOAuthVendor
} from './deviceOauth'

vi.mock('./http', () => ({
  httpGet: vi.fn(),
  httpPost: vi.fn()
}))

const mockedHttpGet = vi.mocked(httpGet)
const mockedHttpPost = vi.mocked(httpPost)

const vendors: DeviceOAuthVendor[] = [
  { vendorKey: 'remote-chat', label: 'Remote Chat', chatSupported: true, modelFetchSupported: true },
  { vendorKey: 'quota-only', label: 'Quota Only', chatSupported: false, modelFetchSupported: true },
  { vendorKey: 'static-only', label: 'Static Only', chatSupported: true, modelFetchSupported: false }
]

beforeEach(() => vi.resetAllMocks())

describe('device OAuth model capabilities', () => {
  it('freezes optional numeric network selection at login start and preserves omitted legacy requests', async () => {
    await startDeviceOAuthSignIn('kiro')
    expect(mockedHttpPost).toHaveBeenLastCalledWith('/api/admin/device-oauth/sign-in/start', { vendor: 'kiro' })
    await startDeviceOAuthSignIn('kiro', { mode: 2, proxyProfileId: 'stable-id' })
    expect(mockedHttpPost).toHaveBeenLastCalledWith('/api/admin/device-oauth/sign-in/start', { vendor: 'kiro', networkSelection: { mode: 2, proxyProfileId: 'stable-id' } })
  })

  it('recognizes public catalogs and treats missing or account scope as non-public', () => {
    const scoped: DeviceOAuthVendor[] = [
      { ...vendors[0], modelCatalogScope: 'public' },
      { ...vendors[1], modelCatalogScope: 'account' },
      vendors[2]
    ]
    expect(usesPublicModelCatalog('remote-chat', scoped)).toBe(true)
    expect(usesPublicModelCatalog('quota-only', scoped)).toBe(false)
    expect(usesPublicModelCatalog('static-only', scoped)).toBe(false)
    expect(usesPublicModelCatalog('unknown', scoped)).toBe(false)
  })

  it('gates dynamic model fetch on SupportsModelFetch rather than a static catalog', () => {
    expect(supportsOAuthModelFetch('remote-chat', vendors)).toBe(true)
    expect(supportsOAuthModelFetch('quota-only', vendors)).toBe(true)
    expect(supportsOAuthModelFetch('static-only', vendors)).toBe(false)
    expect(supportsOAuthModelFetch('unknown', vendors)).toBe(false)
  })

  it('allows read-only fetch without offering import for vendors without chat sites', () => {
    expect(supportsOAuthModelImport({ provider: 'quota-only', modelImportSupported: false }, vendors)).toBe(false)
    expect(supportsOAuthModelImport({ provider: 'remote-chat', modelImportSupported: true }, vendors)).toBe(true)
    expect(supportsOAuthModelImport({ provider: 'static-only', modelImportSupported: true }, vendors)).toBe(false)
    expect(supportsOAuthModelImport(null, vendors)).toBe(false)
  })

  it('uses the shared device OAuth fetch and full-selection import endpoints', async () => {
    const models = [{
      remoteModelName: 'remote-model',
      displayName: 'Remote Model',
      existingMappingId: null,
      isEnabled: false,
      existingDisplayName: null
    }]
    mockedHttpGet.mockResolvedValueOnce(models)
    mockedHttpPost.mockResolvedValueOnce({ changed: 1 })

    await expect(fetchDeviceOAuthModels('account-1')).resolves.toEqual(models)
    await importSelectedDeviceModels('account-1', [
      { remoteModelName: 'remote-model', displayName: 'public-alias', selected: true },
      { remoteModelName: 'old-model', displayName: 'old-model', selected: false }
    ])

    expect(mockedHttpGet).toHaveBeenCalledWith('/api/admin/device-oauth/accounts/account-1/fetch-models')
    expect(mockedHttpPost).toHaveBeenCalledWith('/api/admin/device-oauth/accounts/account-1/import-models', {
      models: [
        { remoteModelName: 'remote-model', displayName: 'public-alias', selected: true },
        { remoteModelName: 'old-model', displayName: 'old-model', selected: false }
      ]
    })
  })
})
