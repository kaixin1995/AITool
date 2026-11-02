import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, defineComponent, nextTick, ssrContextKey, type App, type Ref } from 'vue'
import OAuthView from './OAuthView.vue'
import * as api from '@/api/oauth'
import * as deviceApi from '@/api/deviceOauth'
import { getAccountNetworkCapabilities, getAccountNetworkSummaries, getAccountProxyOptions, saveAccountNetworkPolicy, type AccountNetworkSummary } from '@/api/accountNetwork'

const mocks = vi.hoisted(() => ({ message: { warning: vi.fn(), success: vi.fn(), error: vi.fn(), info: vi.fn() } }))
vi.mock('vue-router', () => ({ useRoute: () => ({ hash: '', query: {} }), useRouter: () => ({ replace: vi.fn() }) }))
vi.mock('naive-ui', () => ({
  NAlert: {}, NCard: {}, NButton: {}, NButtonGroup: {}, NDropdown: {}, NSpace: {}, NTag: {}, NEmpty: {}, NSpin: {}, NModal: {}, NInput: {}, NPopconfirm: {}, NProgress: {}, NCheckbox: {}, NTabs: {}, NTabPane: {}, NSelect: {}, NDataTable: {},
  useMessage: () => mocks.message
}))
vi.mock('@/api/oauth', async (original) => ({
  ...await original<typeof import('@/api/oauth')>(),
  listOAuthAccounts: vi.fn(), listGoogleAccounts: vi.fn(), listKimiAccounts: vi.fn(), listXaiAccounts: vi.fn(),
  startKimiDeviceFlow: vi.fn(), startXaiDeviceFlow: vi.fn(), pollKimiToken: vi.fn(), pollXaiToken: vi.fn(), updateKimiAccount: vi.fn()
}))
vi.mock('@/api/deviceOauth', async (original) => ({
  ...await original<typeof import('@/api/deviceOauth')>(), fetchDeviceOAuthAccounts: vi.fn(), fetchDeviceOAuthVendors: vi.fn()
}))
vi.mock('@/api/accountNetwork', async (original) => ({
  ...await original<typeof import('@/api/accountNetwork')>(), getAccountNetworkCapabilities: vi.fn(), getAccountNetworkSummaries: vi.fn(), getAccountProxyOptions: vi.fn(), saveAccountNetworkPolicy: vi.fn()
}))
interface OAuthSetup {
  oauthProvider: Ref<string>
  oauthModal: Ref<boolean>
  networkReady: Ref<boolean>
  oauthNetworkSelection: Ref<{ mode: 0 | 1 | 2 }>
  kimiServerSessionId: Ref<string>
  xaiServerSessionId: Ref<string>
  kimiDeviceCode: Ref<string>
  xaiDeviceCode: Ref<string>
  deviceSessionId: Ref<string>
  deviceFlowError: Ref<string>
  networkSummaries: Ref<Record<string, AccountNetworkSummary>>
  editDisplayName: Ref<string>
  editRefreshToken: Ref<string>
  editNetworkSelection: Ref<{ mode: 0 | 1 | 2; proxyProfileId?: string | null } | undefined>
  editNetworkConflict: Ref<boolean>
  editPartialError: Ref<string>
  openEdit(account: object): void
  rebaseEditNetwork(): Promise<void>
  handleSaveEdit(): Promise<void>
  handleStartOAuth(): Promise<void>
  handleManualCheckDeviceAuth(): Promise<void>
  clearAllDeviceFlows(): void
}
const renderer = createRenderer<object, object>({
  patchProp: () => {}, insert: () => {}, remove: () => {}, createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
  setText: () => {}, setElementText: () => {}, parentNode: () => null, nextSibling: () => null
})
let app: App | undefined
async function flushAsync() { for (let i = 0; i < 20; i++) await Promise.resolve(); await nextTick() }
async function mountOAuth(): Promise<OAuthSetup> {
  let state!: OAuthSetup
  const component = OAuthView as unknown as { setup(props: object, context: { expose: () => void }): OAuthSetup }
  app = renderer.createApp(defineComponent({ setup() { state = component.setup({}, { expose: () => {} }); return () => null } }))
  app.provide(ssrContextKey, {})
  app.mount({})
  await flushAsync()
  state.oauthModal.value = true
  state.networkReady.value = true
  state.oauthNetworkSelection.value = { mode: 1 }
  return state
}
const flow = { deviceCode: 'code', userCode: 'user', verificationUri: 'https://example.test', verificationUriComplete: 'https://example.test', expiresIn: 300, interval: 5 }
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
  vi.mocked(getAccountNetworkCapabilities).mockResolvedValue({ ready: false })
  vi.mocked(getAccountNetworkSummaries).mockResolvedValue([])
  vi.mocked(getAccountProxyOptions).mockResolvedValue([])
  vi.mocked(api.updateKimiAccount).mockResolvedValue(undefined)
  vi.mocked(api.startKimiDeviceFlow).mockResolvedValue({ ...flow, deviceId: 'device', serverSessionId: 'kimi-server' })
  vi.mocked(api.startXaiDeviceFlow).mockResolvedValue({ ...flow, serverSessionId: 'xai-server' })
  vi.mocked(api.pollKimiToken).mockResolvedValue({ status: 'pending' })
  vi.mocked(api.pollXaiToken).mockResolvedValue({ status: 'pending' })
})
afterEach(() => { app?.unmount(); app = undefined; vi.useRealTimers(); vi.unstubAllGlobals() })
describe('OAuthView dedicated device session regression', () => {
  it.each(['kimi', 'xai'])('passes %s serverSessionId through automatic and manual polls and resets it', async (provider) => {
    const state = await mountOAuth()
    state.oauthProvider.value = provider
    await state.handleStartOAuth()
    expect(state.deviceSessionId.value).toBe('')
    expect(provider === 'kimi' ? state.kimiServerSessionId.value : state.xaiServerSessionId.value).toBe(`${provider}-server`)
    await vi.advanceTimersByTimeAsync(5000)
    await state.handleManualCheckDeviceAuth()
    const calls = provider === 'kimi' ? vi.mocked(api.pollKimiToken).mock.calls : vi.mocked(api.pollXaiToken).mock.calls
    expect(calls).toHaveLength(2)
    for (const args of calls) expect(args).toEqual(provider === 'kimi' ? ['code', 'device', undefined, 'kimi-server'] : ['code', undefined, 'xai-server'])
    state.clearAllDeviceFlows()
    expect(state.kimiServerSessionId.value).toBe('')
    expect(state.xaiServerSessionId.value).toBe('')
  })
  it.each(['kimi', 'xai'])('stops %s automatic polling and reports terminal session/policy errors', async (provider) => {
    const state = await mountOAuth()
    state.oauthProvider.value = provider
    const poll = provider === 'kimi' ? vi.mocked(api.pollKimiToken) : vi.mocked(api.pollXaiToken)
    poll.mockRejectedValueOnce(Object.assign(new Error('The selected proxy profile has changed.'), { status: 400, errorCode: 'proxy_profile_revision_conflict' }))
    await state.handleStartOAuth()
    await vi.advanceTimersByTimeAsync(5000)
    expect(state.deviceFlowError.value).toContain('登录已停止')
    expect(mocks.message.error).toHaveBeenCalled()
    expect(state.kimiDeviceCode.value).toBe('')
    expect(state.xaiDeviceCode.value).toBe('')
    await vi.advanceTimersByTimeAsync(15000)
    expect(poll).toHaveBeenCalledTimes(1)
  })
  it('restarting against a legacy response clears the old dedicated server session', async () => {
    const state = await mountOAuth()
    state.oauthProvider.value = 'kimi'
    await state.handleStartOAuth()
    expect(state.kimiServerSessionId.value).toBe('kimi-server')
    vi.mocked(api.startKimiDeviceFlow).mockResolvedValueOnce({ ...flow, deviceId: 'device' })
    await state.handleStartOAuth()
    expect(state.kimiServerSessionId.value).toBe('')
    await state.handleManualCheckDeviceAuth()
    expect(api.pollKimiToken).toHaveBeenLastCalledWith('code', 'device', undefined, undefined)
  })
  it('keeps polling after transient connectivity errors rather than treating them as policy failures', async () => {
    const state = await mountOAuth()
    state.oauthProvider.value = 'xai'
    vi.mocked(api.pollXaiToken).mockRejectedValueOnce(Object.assign(new Error('Temporary unavailable'), { status: 503 }))
    await state.handleStartOAuth()
    await vi.advanceTimersByTimeAsync(5000)
    expect(state.xaiServerSessionId.value).toBe('xai-server')
    expect(state.deviceFlowError.value).toBe('')
    await vi.advanceTimersByTimeAsync(5000)
    expect(api.pollXaiToken).toHaveBeenCalledTimes(2)
  })
  it('manual invalid-session failure also clears the timer rather than leaving an infinite spinner', async () => {
    const state = await mountOAuth()
    state.oauthProvider.value = 'kimi'
    await state.handleStartOAuth()
    vi.mocked(api.pollKimiToken).mockRejectedValueOnce(Object.assign(new Error('登录会话无效或已过期'), { status: 400 }))
    await state.handleManualCheckDeviceAuth()
    expect(state.kimiServerSessionId.value).toBe('')
    expect(state.deviceFlowError.value).toContain('登录已停止')
    await vi.advanceTimersByTimeAsync(10000)
    expect(api.pollKimiToken).toHaveBeenCalledTimes(1)
  })
})

function openNetworkRepair(state: OAuthSetup) {
  state.networkSummaries.value['kimi:account'] = { providerKey: 'kimi', accountId: 'account', networkSelection: { mode: 2, proxyProfileId: 'broken-profile' }, revision: 'old-revision', proxyProfileName: 'Broken', blockReason: 'proxy_profile_disabled' }
  state.openEdit({ id: 'account', provider: 'kimi', displayName: 'Old name' })
  state.editDisplayName.value = 'Pending name'
  state.editRefreshToken.value = 'pending-token'
  state.editNetworkSelection.value = { mode: 1 }
}
const repaired: AccountNetworkSummary = { providerKey: 'kimi', accountId: 'account', networkSelection: { mode: 1 }, revision: 'new-revision', proxyProfileName: null, blockReason: null }
describe('OAuthView network-first save and conflict handling', () => {
  it('repairs a disabled profile before refreshing replacement credentials', async () => {
    const state = await mountOAuth()
    openNetworkRepair(state)
    const order: string[] = []
    vi.mocked(saveAccountNetworkPolicy).mockImplementationOnce(async () => { order.push('network'); return repaired })
    vi.mocked(api.updateKimiAccount).mockImplementationOnce(async () => { order.push('credentials') })
    await state.handleSaveEdit()
    expect(order).toEqual(['network', 'credentials'])
    expect(saveAccountNetworkPolicy).toHaveBeenCalledWith({ providerKey: 'kimi', accountId: 'account' }, { mode: 1 }, 'old-revision')
    expect(api.updateKimiAccount).toHaveBeenCalledWith('account', 'Pending name', 'pending-token')
  })
  it('reports network-saved/credentials-failed and retries credentials without repeating policy save', async () => {
    const state = await mountOAuth()
    openNetworkRepair(state)
    vi.mocked(saveAccountNetworkPolicy).mockResolvedValueOnce(repaired)
    vi.mocked(api.updateKimiAccount).mockRejectedValueOnce(new Error('credential refresh failed'))
    await state.handleSaveEdit()
    expect(state.editPartialError.value).toContain('网络配置已保存，账号资料更新失败')
    expect(state.editRefreshToken.value).toBe('pending-token')
    expect(mocks.message.success).not.toHaveBeenCalled()
    await state.handleSaveEdit()
    expect(saveAccountNetworkPolicy).toHaveBeenCalledTimes(1)
    expect(api.updateKimiAccount).toHaveBeenCalledTimes(2)
  })
  it('refreshes summary/options on CAS conflict, preserves dirty fields and blocks retry until explicit rebase', async () => {
    const state = await mountOAuth()
    openNetworkRepair(state)
    vi.mocked(getAccountNetworkCapabilities).mockResolvedValue({ ready: true })
    const latest = { ...repaired, networkSelection: { mode: 0 as const }, revision: 'concurrent-revision' }
    vi.mocked(getAccountNetworkSummaries).mockResolvedValue([latest])
    vi.mocked(saveAccountNetworkPolicy).mockRejectedValueOnce(Object.assign(new Error('changed'), { status: 409, errorCode: 'account_network_revision_conflict' }))
    await state.handleSaveEdit()
    expect(state.editNetworkConflict.value).toBe(true)
    expect(getAccountProxyOptions).toHaveBeenCalled()
    expect(getAccountNetworkSummaries).toHaveBeenCalled()
    expect(state.editDisplayName.value).toBe('Pending name')
    expect(state.editRefreshToken.value).toBe('pending-token')
    expect(state.editNetworkSelection.value).toEqual({ mode: 1 })
    await state.handleSaveEdit()
    expect(saveAccountNetworkPolicy).toHaveBeenCalledTimes(1)
    expect(api.updateKimiAccount).not.toHaveBeenCalled()
    await state.rebaseEditNetwork()
    expect(state.editNetworkConflict.value).toBe(false)
    expect(api.updateKimiAccount).not.toHaveBeenCalled()
    vi.mocked(saveAccountNetworkPolicy).mockResolvedValueOnce(repaired)
    await state.handleSaveEdit()
    expect(saveAccountNetworkPolicy).toHaveBeenLastCalledWith({ providerKey: 'kimi', accountId: 'account' }, { mode: 1 }, 'concurrent-revision')
    expect(api.updateKimiAccount).toHaveBeenCalledTimes(1)
  })
})
