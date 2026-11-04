import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, defineComponent, nextTick, ssrContextKey, type App, type Ref } from 'vue'
import OAuthView from './OAuthView.vue'
import * as api from '@/api/oauth'
import * as deviceApi from '@/api/deviceOauth'
import { ApiError, httpGet } from '@/api/http'

const mocks = vi.hoisted(() => ({ message: { warning: vi.fn(), success: vi.fn(), error: vi.fn(), info: vi.fn() } }))
vi.mock('vue-router', () => ({ useRoute: () => ({ hash: '', query: {} }), useRouter: () => ({ replace: vi.fn() }) }))
vi.mock('naive-ui', () => ({
  NAlert: {}, NCard: {}, NButton: {}, NButtonGroup: {}, NDropdown: {}, NSpace: {}, NTag: {}, NEmpty: {}, NSpin: {}, NModal: {}, NInput: {}, NPopconfirm: {}, NProgress: {}, NCheckbox: {}, NTabs: {}, NTabPane: {}, NSelect: {}, NDataTable: {},
  useMessage: () => mocks.message
}))
vi.mock('@/api/http', async (original) => ({ ...await original<typeof import('@/api/http')>(), httpGet: vi.fn() }))
vi.mock('@/api/oauth', async (original) => ({
  ...await original<typeof import('@/api/oauth')>(),
  listOAuthAccounts: vi.fn(), listGoogleAccounts: vi.fn(), listKimiAccounts: vi.fn(), listXaiAccounts: vi.fn(), startKimiDeviceFlow: vi.fn()
}))
vi.mock('@/api/deviceOauth', async (original) => ({
  ...await original<typeof import('@/api/deviceOauth')>(), fetchDeviceOAuthAccounts: vi.fn(), fetchDeviceOAuthVendors: vi.fn()
}))

interface OAuthSetup {
  oauthProvider: Ref<string>
  networkReady: Ref<boolean>
  networkLoadState: Ref<'loading' | 'ready' | 'disabled' | 'legacy' | 'error'>
  legacyNetworkConfirmed: Ref<boolean>
  networkError: Ref<string>
  loadNetwork(): Promise<void>
  handleStartOAuth(): Promise<void>
}
const renderer = createRenderer<object, object>({
  patchProp: () => {}, insert: () => {}, remove: () => {}, createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
  setText: () => {}, setElementText: () => {}, parentNode: () => null, nextSibling: () => null
})
let app: App | undefined
async function mountOAuth(): Promise<OAuthSetup> {
  let state!: OAuthSetup
  const component = OAuthView as unknown as { setup(props: object, context: { expose: () => void }): OAuthSetup }
  app = renderer.createApp(defineComponent({ setup() { state = component.setup({}, { expose: () => {} }); return () => null } }))
  app.provide(ssrContextKey, {})
  app.mount({})
  for (let i = 0; i < 30; i++) await Promise.resolve()
  await nextTick()
  state.oauthProvider.value = 'kimi'
  return state
}
const base = '/api/admin/oauth/network-policies'
const notFound = () => new ApiError('Not found', 'not_found', 404)
beforeEach(() => {
  vi.useFakeTimers()
  vi.resetAllMocks()
  vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn() })
  vi.stubGlobal('document', { visibilityState: 'hidden' })
  vi.mocked(api.listOAuthAccounts).mockResolvedValue([])
  vi.mocked(api.listGoogleAccounts).mockResolvedValue([])
  vi.mocked(api.listKimiAccounts).mockResolvedValue([])
  vi.mocked(api.listXaiAccounts).mockResolvedValue([])
  vi.mocked(deviceApi.fetchDeviceOAuthAccounts).mockResolvedValue([])
  vi.mocked(deviceApi.fetchDeviceOAuthVendors).mockResolvedValue([])
  vi.mocked(api.startKimiDeviceFlow).mockResolvedValue({ deviceCode: 'code', userCode: 'user', verificationUri: 'https://example.test', verificationUriComplete: 'https://example.test', expiresIn: 300, interval: 5, deviceId: 'device' })
  vi.mocked(httpGet).mockImplementation(async (url) => url === `${base}/capabilities` ? { ready: true } : [])
})
afterEach(() => { app?.unmount(); app = undefined; vi.useRealTimers(); vi.unstubAllGlobals() })

describe('OAuth network policy loading', () => {
  it.each([`${base}/proxy-options`, base])('blocks login when %s returns 404, including after legacy confirmation', async (failedUrl) => {
    vi.mocked(httpGet).mockImplementation(async (url) => {
      if (url === failedUrl) throw notFound()
      return url === `${base}/capabilities` ? { ready: true } : []
    })
    const state = await mountOAuth()
    expect(state.networkLoadState.value).toBe('error')
    expect(state.networkReady.value).toBe(false)
    expect(state.networkError.value).toContain('网络策略加载失败')
    state.legacyNetworkConfirmed.value = true
    await state.handleStartOAuth()
    expect(api.startKimiDeviceFlow).not.toHaveBeenCalled()
    expect(mocks.message.warning).toHaveBeenCalledWith('网络策略加载失败，请重试。')

    vi.mocked(httpGet).mockImplementation(async (url) => url === `${base}/capabilities` ? { ready: true } : [])
    await state.loadNetwork()
    expect(state.networkLoadState.value).toBe('ready')
    await state.handleStartOAuth()
    expect(api.startKimiDeviceFlow).toHaveBeenCalledWith(undefined, { mode: 0 })
  })

  it('only falls back to legacy when the capabilities endpoint returns 404, and requires confirmation', async () => {
    vi.mocked(httpGet).mockImplementation(async (url) => {
      if (url === `${base}/capabilities`) throw notFound()
      return []
    })
    const state = await mountOAuth()
    expect(state.networkLoadState.value).toBe('legacy')
    expect(httpGet).not.toHaveBeenCalledWith(`${base}/proxy-options`, expect.anything())
    await state.handleStartOAuth()
    expect(api.startKimiDeviceFlow).not.toHaveBeenCalled()
    state.legacyNetworkConfirmed.value = true
    await state.handleStartOAuth()
    expect(api.startKimiDeviceFlow).toHaveBeenCalledWith(undefined, undefined)
  })

  it('blocks login on non-404 capabilities errors', async () => {
    vi.mocked(httpGet).mockRejectedValue(new ApiError('Unavailable', 'unavailable', 503))
    const state = await mountOAuth()
    expect(state.networkLoadState.value).toBe('error')
    state.legacyNetworkConfirmed.value = true
    await state.handleStartOAuth()
    expect(api.startKimiDeviceFlow).not.toHaveBeenCalled()
  })
})
