import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, defineComponent, nextTick, ssrContextKey, type App, type Ref } from 'vue'
import OAuthView from './OAuthView.vue'
import oauthTemplate from './OAuthView.vue?raw'
import * as api from '@/api/oauth'
import * as deviceApi from '@/api/deviceOauth'
import { getAccountNetworkCapabilities, getAccountNetworkSummaries, getAccountProxyOptions, saveAccountNetworkPolicy, type AccountNetworkSummary } from '@/api/accountNetwork'

const mocks = vi.hoisted(() => ({ message: { warning: vi.fn(), success: vi.fn(), error: vi.fn(), info: vi.fn() } }))
vi.mock('vue-router', () => ({ useRoute: () => ({ hash: '', query: {} }), useRouter: () => ({ replace: vi.fn() }) }))
vi.mock('@/composables/useCurrency', () => ({ useCurrency: () => ({ formatTotalCost: (value: number) => `$${value}`, setUsdToCny: vi.fn() }) }))
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
  ...await original<typeof import('@/api/deviceOauth')>(), fetchDeviceOAuthAccounts: vi.fn(), fetchDeviceOAuthVendors: vi.fn(), fetchDeviceOAuthModels: vi.fn()
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
  loading: Ref<boolean>
  vendorsLoading: Ref<boolean>
  vendorsLoaded: Ref<boolean>
  vendorsError: Ref<string>
  canUseVendorCatalog: Ref<boolean>
  providerFilter: Ref<string>
  providerFilterSelectOptions: Ref<Array<{ label: string; value: string }>>
  accountStatusFilter: Ref<string>
  accountViewMode: Ref<string>
  filteredAccounts: Ref<Array<{ id: string }>>
  editModal: Ref<boolean>
  editAccount: Ref<{ provider: string } | null>
  loadDeviceVendors(): Promise<void>
  loadAccounts(showError: boolean): Promise<void>
  setProviderFilter(value: string): void
  editDisplayName: Ref<string>
  editRefreshToken: Ref<string>
  editNetworkSelection: Ref<{ mode: 0 | 1 | 2; proxyProfileId?: string | null } | undefined>
  editNetworkConflict: Ref<boolean>
  editPartialError: Ref<string>
  accounts: Ref<Array<{ id: string; displayName: string }>>
  accountTableRows: Ref<Array<{ id: string; createdAt: string }>>
  accountTableColumns: Ref<Array<{ key: string; title: string; render?(row: { id: string; createdAt: string }): unknown }>>
  checkedModels: Ref<string[]>
  modelCatalogIsPublic: Ref<boolean>
  modelSyncHint: Ref<string>
  applyToGroup: Ref<boolean>
  handleImportModels(): Promise<void>
  openFetchModels(account: object): Promise<void>
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
  vi.mocked(deviceApi.fetchDeviceOAuthVendors).mockResolvedValue([{ vendorKey: 'claude', label: 'Claude', chatSupported: true, modelFetchSupported: true }])
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
describe('OAuth vendor catalog readiness', () => {
  const vendor = { vendorKey: 'claude', label: 'Claude', chatSupported: true, modelFetchSupported: true }
  const account = { id: '1', displayName: 'Account', email: null, accountId: null, planType: null,
    isEnabled: true, isQuotaCooling: false, quotaCoolingUntil: null, lastQuotaCheckedAt: null,
    tokenExpiresAt: null, createdAt: null }

  function deferred<T>() {
    let resolve!: (value: T) => void
    let reject!: (reason: Error) => void
    const promise = new Promise<T>((ok, fail) => { resolve = ok; reject = fail })
    return { promise, resolve, reject }
  }

  // SSR compilation does not expose a client render function in this setup harness.
  // Check the template wiring alongside the reactive state and event guards below.
  it('wires catalog-dependent controls and status messages to the shared state', () => {
    expect(oauthTemplate).toMatch(/:value="providerFilter"\s+:disabled="!canUseVendorCatalog"\s+@update:value="setProviderFilter"/)
    expect(oauthTemplate).toMatch(/aria-label="编辑账号"\s+:disabled="!canUseVendorCatalog \|\| !canOperateAccount\(acc.provider\)"\s+@click="openEdit\(acc\)"/)
    expect(oauthTemplate).toContain("disabled: !canUseVendorCatalog.value || !canOperateAccount(row.raw.provider), onClick: () => openEdit(row.raw)")
    expect(oauthTemplate).toContain('厂商清单加载中，编辑和筛选暂不可用')
    expect(oauthTemplate).toMatch(/v-else-if="vendorsError"[\s\S]*?{{ vendorsError }}[\s\S]*?@click="loadDeviceVendors">重试/)
    expect(oauthTemplate).toContain('暂无可选厂商')
  })

  it('keeps editing and filtering disabled until the parallel catalog request resolves', async () => {
    const pending = deferred<deviceApi.DeviceOAuthVendor[]>()
    vi.mocked(deviceApi.fetchDeviceOAuthVendors).mockReturnValue(pending.promise)
    vi.mocked(api.listOAuthAccounts).mockResolvedValue([account])
    const state = await mountOAuth()
    state.accountViewMode.value = 'card'
    expect(state.accounts.value).toHaveLength(1)
    expect(state.loading.value).toBe(false)
    expect(state.vendorsLoading.value).toBe(true)
    expect(state.canUseVendorCatalog.value).toBe(false)
    expect(state.vendorsError.value).toBe('')
    state.openEdit(state.accounts.value[0])
    state.setProviderFilter('claude')
    expect(state.editAccount.value).toBeNull()
    expect(state.providerFilter.value).toBe('all')
    expect(deviceApi.fetchDeviceOAuthVendors).toHaveBeenCalledTimes(1)
    const refresh = state.loadAccounts(false)
    let refreshFinished = false
    void refresh.then(() => { refreshFinished = true })
    await flushAsync()
    expect(deviceApi.fetchDeviceOAuthVendors).toHaveBeenCalledTimes(1)
    expect(refreshFinished).toBe(false)
    pending.resolve([vendor])
    await refresh
    await flushAsync()
    expect(refreshFinished).toBe(true)
    expect(state.canUseVendorCatalog.value).toBe(true)
    // 当前状态下没有任何账号的厂商不进下拉
    expect(state.providerFilterSelectOptions.value).not.toContainEqual({ label: 'Claude（0）', value: 'claude' })
    state.openEdit(state.accounts.value[0])
    expect(state.editAccount.value?.provider).toBe('codex')
    state.setProviderFilter('claude')
    expect(state.providerFilter.value).toBe('all')
    expect(state.filteredAccounts.value).toHaveLength(1)
  })

  it('shows a retry after failure and restores controls only after the retry succeeds', async () => {
    const pending = deferred<deviceApi.DeviceOAuthVendor[]>()
    vi.mocked(deviceApi.fetchDeviceOAuthVendors).mockReturnValueOnce(pending.promise).mockResolvedValueOnce([vendor])
    vi.mocked(api.listOAuthAccounts).mockResolvedValue([account])
    const state = await mountOAuth()
    state.accountViewMode.value = 'card'
    pending.reject(new Error('网络中断'))
    await flushAsync()
    expect(state.vendorsError.value).toContain('厂商清单加载失败：网络中断')
    expect(state.canUseVendorCatalog.value).toBe(false)
    state.openEdit(state.accounts.value[0])
    state.setProviderFilter('claude')
    expect(state.editAccount.value).toBeNull()
    expect(state.providerFilter.value).toBe('all')
    const accountRequests = vi.mocked(api.listOAuthAccounts).mock.calls.length
    void state.loadDeviceVendors()
    expect(state.vendorsError.value).toBe('')
    await flushAsync()
    expect(deviceApi.fetchDeviceOAuthVendors).toHaveBeenCalledTimes(2)
    expect(api.listOAuthAccounts).toHaveBeenCalledTimes(accountRequests)
    expect(state.canUseVendorCatalog.value).toBe(true)
    expect(state.providerFilterSelectOptions.value).not.toContainEqual({ label: 'Claude（0）', value: 'claude' })
  })

  it('defaults the status filter to enabled and self-heals a vendor selection that lost all accounts', async () => {
    const deviceAccount: deviceApi.DeviceOAuthAccount = {
      id: 'dev-1', vendorKey: 'claude', vendorLabel: 'Claude', displayName: 'Claude Acc', email: 'c@x.test',
      isEnabled: true, manuallyDisabled: false, disabledByFeatureToggle: false, requiresReauth: false,
      tokenExpiresAt: null, linkedSiteId: '', modelImportSupported: false, createdAt: '', lastQuotaCheckedAt: null,
      planType: null, windows: []
    }
    vi.mocked(deviceApi.fetchDeviceOAuthAccounts).mockResolvedValue([deviceAccount])
    vi.mocked(api.listOAuthAccounts).mockResolvedValue([{ ...account, id: 'codex-off', displayName: 'Off', isEnabled: false }])
    const state = await mountOAuth()
    state.accountViewMode.value = 'card'
    // 页面默认「已启用」：禁用的 Codex 账号不进列表
    expect(state.accountStatusFilter.value).toBe('enabled')
    expect(state.filteredAccounts.value.map(a => a.id)).toEqual(['dev-1'])
    expect(state.providerFilterSelectOptions.value).toContainEqual({ label: 'Claude（1）', value: 'claude' })
    state.setProviderFilter('claude')
    expect(state.providerFilter.value).toBe('claude')
    // 该厂商唯一账号被禁用后：选项从下拉消失，残留选中项自动回落到「全部」
    vi.mocked(deviceApi.fetchDeviceOAuthAccounts).mockResolvedValue([{ ...deviceAccount, isEnabled: false }])
    await state.loadAccounts(false)
    await flushAsync()
    expect(state.providerFilterSelectOptions.value).not.toContainEqual({ label: 'Claude（0）', value: 'claude' })
    expect(state.providerFilter.value).toBe('all')
  })

  it('fails closed with an explicit empty state when the catalog is empty', async () => {
    vi.mocked(deviceApi.fetchDeviceOAuthVendors).mockResolvedValue([])
    vi.mocked(api.listOAuthAccounts).mockResolvedValue([account])
    const state = await mountOAuth()
    state.accountViewMode.value = 'card'
    expect(state.vendorsLoaded.value).toBe(true)
    expect(state.canUseVendorCatalog.value).toBe(false)
    state.openEdit(state.accounts.value[0])
    state.setProviderFilter('claude')
    expect(state.editAccount.value).toBeNull()
    expect(state.providerFilter.value).toBe('all')
  })
})

describe('OAuth model selection and ordering', () => {
  it.each([['public', true], [undefined, false]] as const)('marks catalog scope %s as public: %s', async (scope, expected) => {
    vi.mocked(deviceApi.fetchDeviceOAuthVendors).mockResolvedValue([
      { vendorKey: 'claude', label: 'Claude', chatSupported: true, modelFetchSupported: true, modelCatalogScope: scope }
    ])
    vi.mocked(deviceApi.fetchDeviceOAuthModels).mockResolvedValue([
      { remoteModelName: 'model', displayName: 'Model', existingMappingId: null, isEnabled: false, existingDisplayName: null }
    ])
    const state = await mountOAuth()
    await state.openFetchModels({ id: '1', provider: 'claude', displayName: 'Claude', modelImportSupported: true })
    expect(deviceApi.fetchDeviceOAuthModels).toHaveBeenCalledWith('1')
    expect(state.modelCatalogIsPublic.value).toBe(expected)
    expect(state.checkedModels.value).toEqual([])
    expect(state.applyToGroup.value).toBe(false)
  })

  it('sorts accounts by name and starts new model selections unchecked', async () => {
    const base = { email: null, accountId: null, planType: null, isEnabled: true, isQuotaCooling: false,
      quotaCoolingUntil: null, lastQuotaCheckedAt: null, tokenExpiresAt: null, createdAt: null }
    vi.mocked(api.listOAuthAccounts).mockResolvedValue([
      { ...base, id: '2', displayName: 'Account 10' },
      { ...base, id: '1', displayName: 'Account 2' }
    ])
    vi.spyOn(api, 'fetchOAuthModels').mockResolvedValue({ models: [
      { remoteModelName: 'm1', displayName: 'm1', existingMappingId: null, isEnabled: false, existingDisplayName: null }
    ] } as Awaited<ReturnType<typeof api.fetchOAuthModels>>)
    const state = await mountOAuth()
    expect(state.accounts.value.map(a => a.displayName)).toEqual(['Account 2', 'Account 10'])
    await state.openFetchModels({ ...base, id: '1', displayName: 'Account 2', provider: 'codex' })
    expect(state.checkedModels.value).toEqual([])
    expect(state.applyToGroup.value).toBe(false)
  })

  it('sets modelSyncHint distinguishing Codex import-only vs other providers sync semantics', async () => {
    const base = { email: null, accountId: null, planType: null, isEnabled: true, isQuotaCooling: false,
      quotaCoolingUntil: null, lastQuotaCheckedAt: null, tokenExpiresAt: null, createdAt: null }
    vi.spyOn(api, 'fetchOAuthModels').mockResolvedValue({ models: [
      { remoteModelName: 'm1', displayName: 'm1', existingMappingId: 'map-1', isEnabled: true, existingDisplayName: null },
      { remoteModelName: 'm2', displayName: 'm2', existingMappingId: null, isEnabled: false, existingDisplayName: null }
    ] } as Awaited<ReturnType<typeof api.fetchOAuthModels>>)
    vi.spyOn(api, 'fetchGoogleModels').mockResolvedValue([
      { remoteModelName: 'g1', displayName: 'g1', existingMappingId: 'map-g1', isEnabled: true, existingDisplayName: null }
    ])

    const state = await mountOAuth()
    await state.openFetchModels({ ...base, id: 'codex-1', displayName: 'Codex Account', provider: 'codex' })
    expect(state.modelSyncHint.value).toContain('只导入勾选项')
    expect(state.modelSyncHint.value).not.toContain('既有映射会禁用')

    await state.openFetchModels({ ...base, id: 'google-1', displayName: 'Google Account', provider: 'antigravity' })
    expect(state.modelSyncHint.value).toContain('未勾选的既有映射会禁用')
  })

  it('keeps deselected existing mappings in selections with selected:false and alerts Codex import-only notice', async () => {
    const base = { email: null, accountId: null, planType: null, isEnabled: true, isQuotaCooling: false,
      quotaCoolingUntil: null, lastQuotaCheckedAt: null, tokenExpiresAt: null, createdAt: null }
    vi.spyOn(api, 'fetchOAuthModels').mockResolvedValue({ models: [
      { remoteModelName: 'm1', displayName: 'm1', existingMappingId: 'map-1', isEnabled: true, existingDisplayName: null },
      { remoteModelName: 'm2', displayName: 'm2', existingMappingId: null, isEnabled: false, existingDisplayName: null }
    ] } as Awaited<ReturnType<typeof api.fetchOAuthModels>>)
    const importSpy = vi.spyOn(api, 'importSelectedOAuthModels').mockResolvedValue()

    const state = await mountOAuth()
    await state.openFetchModels({ ...base, id: 'codex-1', displayName: 'Codex Account', provider: 'codex' })
    // m1 was previously enabled, so checkedModels contains ['m1']. Uncheck m1 and check m2 instead.
    expect(state.checkedModels.value).toEqual(['m1'])
    state.checkedModels.value = ['m2']

    await state.handleImportModels()

    expect(importSpy).toHaveBeenCalledWith('codex-1', [
      { remoteModelName: 'm1', displayName: 'm1', selected: false },
      { remoteModelName: 'm2', displayName: 'm2', selected: true }
    ])
    expect(mocks.message.info).toHaveBeenCalledWith(
      expect.stringContaining('Codex 只导入勾选项：1 个已启用的既有映射保持启用（未勾选不会停用）')
    )
  })
})

describe('OAuth account table creation time', () => {
  it('renders the formatted creation time and falls back to a dash for missing values', async () => {
    const base = { email: null, accountId: null, planType: null, isEnabled: true, isQuotaCooling: false,
      quotaCoolingUntil: null, lastQuotaCheckedAt: null, tokenExpiresAt: null, createdAt: null }
    vi.mocked(api.listOAuthAccounts).mockResolvedValue([
      { ...base, id: 'with-created-at', displayName: 'With created at', createdAt: '2025-06-15T08:30:00Z' },
      { ...base, id: 'without-created-at', displayName: 'Without created at' }
    ])
    const state = await mountOAuth()
    const withCreatedAt = state.accountTableRows.value.find(row => row.id === 'with-created-at')
    const withoutCreatedAt = state.accountTableRows.value.find(row => row.id === 'without-created-at')
    const createdAtColumn = state.accountTableColumns.value.find(column => column.key === 'createdAt')

    expect(createdAtColumn?.title).toBe('创建时间')
    expect(withCreatedAt?.createdAt).toBe('2025/6/15 16:30:00')
    expect((createdAtColumn?.render?.(withCreatedAt!) as { children?: unknown } | undefined)?.children).toBe('2025/6/15 16:30:00')
    expect((createdAtColumn?.render?.(withoutCreatedAt!) as { children?: unknown } | undefined)?.children).toBe('—')
  })
})

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
