import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, defineComponent, nextTick, ssrContextKey, type App, type Ref } from 'vue'
import AccountPoolsView from './AccountPoolsView.vue'
import * as api from '@/api/accountPools'
import * as deviceApi from '@/api/deviceOauth'
import type { AccountPoolCandidate } from '@/api/accountPools'

const mocks = vi.hoisted(() => ({ message: { error: vi.fn(), warning: vi.fn(), success: vi.fn() } }))
vi.mock('naive-ui', () => ({
  NButton: {}, NCard: {}, NCheckbox: {}, NEmpty: {}, NInput: {}, NModal: {}, NPopconfirm: {},
  NRadio: {}, NRadioGroup: {}, NSelect: {}, NSpace: {}, NSpin: {}, NSwitch: {}, NTag: {}, NTooltip: {},
  useMessage: () => mocks.message,
}))
vi.mock('@/api/accountPools', async original => ({
  ...await original<typeof import('@/api/accountPools')>(),
  fetchAccountPools: vi.fn(), fetchPoolCandidates: vi.fn(), previewPoolModels: vi.fn(), createAccountPool: vi.fn(),
}))
vi.mock('@/api/deviceOauth', () => ({ fetchDeviceOAuthVendors: vi.fn() }))

interface CreateCandidatesSetup {
  newVendor: Ref<string | null>
  newPlan: Ref<string | null>
  candidates: Ref<AccountPoolCandidate[]>
  candidatesLoading: Ref<boolean>
  openCreate(): Promise<void>
  refreshCandidates(): Promise<void>
}

const renderer = createRenderer<object, object>({
  patchProp: () => {}, insert: () => {}, remove: () => {}, createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
  setText: () => {}, setElementText: () => {}, parentNode: () => null, nextSibling: () => null,
})
let app: App | undefined
async function mountView(): Promise<CreateCandidatesSetup> {
  let state!: CreateCandidatesSetup
  const component = AccountPoolsView as unknown as { setup(props: object, context: { expose: () => void }): CreateCandidatesSetup }
  app = renderer.createApp(defineComponent({ setup() { state = component.setup({}, { expose: () => {} }); return () => null } }))
  app.provide(ssrContextKey, {})
  app.mount({})
  await nextTick()
  return state
}

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function candidate(accountId: string): AccountPoolCandidate {
  return { accountId, displayName: accountId, linkedSiteId: 'site' }
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.fetchAccountPools).mockResolvedValue([])
  vi.mocked(api.previewPoolModels).mockResolvedValue([])
  vi.mocked(deviceApi.fetchDeviceOAuthVendors).mockResolvedValue([])
})
afterEach(() => { app?.unmount(); app = undefined })

describe('建池候选列表', () => {
  it('过期候选响应不覆盖新厂商', async () => {
    const oldResponse = deferred<AccountPoolCandidate[]>()
    const kimiCandidates = [candidate('kimi-account')]
    vi.mocked(api.fetchPoolCandidates)
      .mockReturnValueOnce(oldResponse.promise)
      .mockResolvedValueOnce(kimiCandidates)
    const state = await mountView()

    state.newVendor.value = 'codex'
    const oldRequest = state.refreshCandidates()
    state.newVendor.value = 'kimi'
    await state.refreshCandidates()
    oldResponse.resolve([candidate('codex-account')])
    await oldRequest

    expect(api.fetchPoolCandidates).toHaveBeenNthCalledWith(1, 'codex', null)
    expect(api.fetchPoolCandidates).toHaveBeenNthCalledWith(2, 'kimi', null)
    expect(state.candidates.value).toEqual(kimiCandidates)
    expect(state.candidatesLoading.value).toBe(false)
  })

  it('重新打开弹窗丢弃上一轮在途请求', async () => {
    const oldResponse = deferred<AccountPoolCandidate[]>()
    vi.mocked(api.fetchPoolCandidates).mockReturnValueOnce(oldResponse.promise)
    const state = await mountView()

    state.newVendor.value = 'codex'
    const oldRequest = state.refreshCandidates()
    await state.openCreate()
    oldResponse.resolve([candidate('old-account')])
    await oldRequest

    expect(state.candidates.value).toEqual([])
    expect(state.candidatesLoading.value).toBe(false)
  })

  it('过期请求失败不弹错误', async () => {
    const oldResponse = deferred<AccountPoolCandidate[]>()
    vi.mocked(api.fetchPoolCandidates)
      .mockReturnValueOnce(oldResponse.promise)
      .mockResolvedValueOnce([candidate('kimi-account')])
    const state = await mountView()

    state.newVendor.value = 'codex'
    const oldRequest = state.refreshCandidates()
    state.newVendor.value = 'kimi'
    await state.refreshCandidates()
    oldResponse.reject(new Error('旧请求失败'))
    await oldRequest

    expect(mocks.message.error).not.toHaveBeenCalled()
  })

  it('无厂商时 loading 收敛并清空候选', async () => {
    const oldResponse = deferred<AccountPoolCandidate[]>()
    vi.mocked(api.fetchPoolCandidates).mockReturnValueOnce(oldResponse.promise)
    const state = await mountView()

    state.newVendor.value = 'codex'
    const oldRequest = state.refreshCandidates()
    state.candidates.value = [candidate('old-account')]
    state.newVendor.value = null
    await state.refreshCandidates()
    oldResponse.resolve([candidate('codex-account')])
    await oldRequest

    expect(state.candidates.value).toEqual([])
    expect(state.candidatesLoading.value).toBe(false)
  })
})
