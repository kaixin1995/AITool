import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, defineComponent, nextTick, ssrContextKey, type App, type Ref } from 'vue'
import AccountPoolsView from './AccountPoolsView.vue'
import * as api from '@/api/accountPools'
import type { AccountPoolCandidate, AccountPoolSummary } from '@/api/accountPools'

const mocks = vi.hoisted(() => ({ message: { error: vi.fn(), warning: vi.fn(), success: vi.fn() } }))
vi.mock('naive-ui', () => ({
  NButton: {}, NCard: {}, NCheckbox: {}, NEmpty: {}, NInput: {}, NModal: {}, NPopconfirm: {},
  NRadio: {}, NRadioGroup: {}, NSelect: {}, NSpace: {}, NSpin: {}, NSwitch: {}, NTag: {}, NTooltip: {},
  useMessage: () => mocks.message,
}))
vi.mock('@/api/accountPools', async original => ({
  ...await original<typeof import('@/api/accountPools')>(),
  fetchAccountPools: vi.fn(), fetchPoolCandidates: vi.fn(),
}))

interface MembersSetup {
  candidates: Ref<AccountPoolCandidate[]>
  memberChecked: Ref<Record<string, boolean>>
  memberExcluded: Ref<Record<string, boolean>>
  candidateEntries: Ref<Array<{ id: string; name: string }>>
  openMembers(pool: AccountPoolSummary): void
}

const renderer = createRenderer<object, object>({
  patchProp: () => {}, insert: () => {}, remove: () => {}, createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
  setText: () => {}, setElementText: () => {}, parentNode: () => null, nextSibling: () => null,
})
let app: App | undefined
async function mountView(): Promise<MembersSetup> {
  let state!: MembersSetup
  const component = AccountPoolsView as unknown as { setup(props: object, context: { expose: () => void }): MembersSetup }
  app = renderer.createApp(defineComponent({ setup() { state = component.setup({}, { expose: () => {} }); return () => null } }))
  app.provide(ssrContextKey, {})
  app.mount({})
  await nextTick()
  return state
}
function pool(id: string, vendorKey: string): AccountPoolSummary {
  return {
    id, name: id, vendorKey, vendorLabel: vendorKey, planType: null, autoIncludeMembers: false,
    exposedModels: null, managedEntryCount: 0, createdAt: '', updatedAt: '',
    members: [{ accountRefId: 'existing', displayName: '已有成员', linkedSiteId: 'site', priority: 0, excluded: true }],
  }
}
function candidate(accountId: string, displayName: string): AccountPoolCandidate {
  return { accountId, displayName, linkedSiteId: 'site' }
}
async function flushAsync() { await Promise.resolve(); await nextTick() }

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.fetchAccountPools).mockResolvedValue([])
})
afterEach(() => { app?.unmount(); app = undefined })

describe('成员弹窗候选名称', () => {
  it('使用当前候选名称，保留已有成员的回退名称和勾选状态，不读取建池候选', async () => {
    vi.mocked(api.fetchPoolCandidates).mockResolvedValue([candidate('new', '新候选')])
    const state = await mountView()
    state.candidates.value = [candidate('new', '其他厂商旧名称'), candidate('existing', '错误的旧名称')]
    state.openMembers(pool('pool', 'codex'))
    await flushAsync()
    expect(api.fetchPoolCandidates).toHaveBeenCalledWith('codex', null)
    expect(state.candidateEntries.value).toEqual([{ id: 'existing', name: '已有成员' }, { id: 'new', name: '新候选' }])
    expect(state.memberChecked.value).toEqual({ existing: true, new: false })
    expect(state.memberExcluded.value).toEqual({ existing: true, new: false })
  })

  it('重新打开时清空已加载的旧候选，在新请求完成前使用成员回退名称', async () => {
    let resolveNew!: (items: AccountPoolCandidate[]) => void
    vi.mocked(api.fetchPoolCandidates)
      .mockResolvedValueOnce([candidate('existing', '旧候选名称')])
      .mockReturnValueOnce(new Promise(resolve => { resolveNew = resolve }))
    const state = await mountView()
    state.openMembers(pool('first', 'codex'))
    await flushAsync()
    expect(state.candidateEntries.value).toEqual([{ id: 'existing', name: '旧候选名称' }])

    state.openMembers(pool('second', 'kimi'))
    expect(state.candidateEntries.value).toEqual([{ id: 'existing', name: '已有成员' }])
    expect(state.memberChecked.value).toEqual({ existing: true })
    resolveNew([candidate('existing', '新候选名称')])
    await flushAsync()
    expect(state.candidateEntries.value).toEqual([{ id: 'existing', name: '新候选名称' }])
  })

  it('重新打开时忽略先前请求的迟到响应', async () => {
    let resolveOld!: (items: AccountPoolCandidate[]) => void
    let resolveNew!: (items: AccountPoolCandidate[]) => void
    vi.mocked(api.fetchPoolCandidates)
      .mockReturnValueOnce(new Promise(resolve => { resolveOld = resolve }))
      .mockReturnValueOnce(new Promise(resolve => { resolveNew = resolve }))
    const state = await mountView()
    state.openMembers(pool('first', 'codex'))
    state.openMembers(pool('second', 'kimi'))
    expect(state.candidateEntries.value).toEqual([{ id: 'existing', name: '已有成员' }])
    resolveNew([candidate('new', '当前厂商')])
    await flushAsync()
    resolveOld([candidate('old', '旧厂商')])
    await flushAsync()
    expect(state.candidateEntries.value).toEqual([{ id: 'existing', name: '已有成员' }, { id: 'new', name: '当前厂商' }])
    expect(state.memberChecked.value).toEqual({ existing: true, new: false })
  })
})
