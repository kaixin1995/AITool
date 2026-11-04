import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, defineComponent, nextTick, ssrContextKey, type App, type Ref } from 'vue'
import AccountPoolsView from './AccountPoolsView.vue'
import * as api from '@/api/accountPools'

const mocks = vi.hoisted(() => ({ message: { error: vi.fn(), warning: vi.fn(), success: vi.fn() } }))
vi.mock('naive-ui', () => ({
  NButton: {}, NCard: {}, NCheckbox: {}, NEmpty: {}, NInput: {}, NModal: {}, NPopconfirm: {},
  NRadio: {}, NRadioGroup: {}, NSelect: {}, NSpace: {}, NSpin: {}, NSwitch: {}, NTag: {}, NTooltip: {},
  useMessage: () => mocks.message,
}))
vi.mock('@/api/accountPools', async original => ({
  ...await original<typeof import('@/api/accountPools')>(),
  fetchAccountPools: vi.fn(), previewPoolModels: vi.fn(), createAccountPool: vi.fn(),
}))
vi.mock('@/api/deviceOauth', () => ({ fetchDeviceOAuthVendors: vi.fn().mockResolvedValue([]) }))

interface CreateSetup {
  newName: Ref<string>
  newVendor: Ref<string | null>
  newModelMode: Ref<'all' | 'custom'>
  newModels: Ref<string[]>
  createModelOptions: Ref<string[]>
  createModelsLoading: Ref<boolean>
  openCreate(): Promise<void>
  refreshCreateModelOptions(): Promise<void>
  updateCreateModels(selected: string[]): void
  handleCreate(): Promise<void>
}

const renderer = createRenderer<object, object>({
  patchProp: () => {}, insert: () => {}, remove: () => {}, createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
  setText: () => {}, setElementText: () => {}, parentNode: () => null, nextSibling: () => null,
})
let app: App | undefined
async function mountView(): Promise<CreateSetup> {
  let state!: CreateSetup
  const component = AccountPoolsView as unknown as { setup(props: object, context: { expose: () => void }): CreateSetup }
  app = renderer.createApp(defineComponent({ setup() { state = component.setup({}, { expose: () => {} }); return () => null } }))
  app.provide(ssrContextKey, {})
  app.mount({})
  await nextTick()
  state.newVendor.value = 'codex'
  return state
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.fetchAccountPools).mockResolvedValue([])
})
afterEach(() => { app?.unmount(); app = undefined })

describe('建池同步结果提示', () => {
  // API 只返回跳过的模型名，不区分手工路由和其他号池两种占用来源。
  it.each([
    { source: '手工路由', models: ['manual-model'] },
    { source: '其他号池', models: ['pool-model', 'pool-model-2'] },
  ])('$source 占用时展示合并提示及同步结果', async ({ models }) => {
    vi.mocked(api.createAccountPool).mockResolvedValue({
      poolId: 'pool-id', name: 'pool', memberCount: 2, modelCount: 3, ruleCount: 4, skippedModels: models,
    })
    const state = await mountView()
    state.newName.value = 'pool'
    await state.handleCreate()
    expect(mocks.message.warning).toHaveBeenCalledWith(
      `号池已创建：成员 2 · 模型 3 · 规则 4；跳过 ${models.length} 个被手工路由或其他号池占用的模型（${models.join('、')}）`,
    )
    expect(mocks.message.success).not.toHaveBeenCalled()
  })

  it('没有跳过模型时仍显示成功提示', async () => {
    vi.mocked(api.createAccountPool).mockResolvedValue({
      poolId: 'pool-id', name: 'pool', memberCount: 2, modelCount: 3, ruleCount: 4, skippedModels: [],
    })
    const state = await mountView()
    state.newName.value = 'pool'
    await state.handleCreate()
    expect(mocks.message.success).toHaveBeenCalledWith('号池已创建：成员 2 · 模型 3 · 规则 4')
    expect(mocks.message.warning).not.toHaveBeenCalled()
  })
})

describe('建池模型预览', () => {
  it('保留取消勾选，自动选择新增模型，并在模型消失后重新出现时保留取消状态', async () => {
    vi.mocked(api.previewPoolModels)
      .mockResolvedValueOnce(['a', 'b'])
      .mockResolvedValueOnce(['a', 'b', 'c'])
      .mockResolvedValueOnce(['a', 'c'])
      .mockResolvedValueOnce(['a', 'b', 'c'])
      .mockResolvedValueOnce(['a', 'b', 'c'])
    const state = await mountView()
    await state.refreshCreateModelOptions()
    expect(state.newModels.value).toEqual(['a', 'b'])
    state.newModelMode.value = 'custom'
    state.updateCreateModels(['a'])
    await state.refreshCreateModelOptions()
    expect(state.newModels.value).toEqual(['a', 'c'])
    await state.refreshCreateModelOptions()
    await state.refreshCreateModelOptions()
    expect(state.newModels.value).toEqual(['a', 'c'])
    state.updateCreateModels(['a', 'b', 'c'])
    await state.refreshCreateModelOptions()
    expect(state.newModels.value).toEqual(['a', 'b', 'c'])
  })

  it('清空自选后刷新不会重新勾选，也不会提交为全部模型', async () => {
    vi.mocked(api.previewPoolModels).mockResolvedValue(['a', 'b'])
    const state = await mountView()
    await state.refreshCreateModelOptions()
    state.newModelMode.value = 'custom'
    state.updateCreateModels([])
    await state.refreshCreateModelOptions()
    expect(state.newModels.value).toEqual([])
    state.newName.value = 'pool'
    await state.handleCreate()
    expect(api.createAccountPool).not.toHaveBeenCalled()
    expect(mocks.message.warning).toHaveBeenCalled()
  })

  it('忽略过期预览响应，重新打开时清除选择历史', async () => {
    let resolveOld!: (models: string[]) => void
    vi.mocked(api.previewPoolModels)
      .mockResolvedValueOnce(['a'])
      .mockReturnValueOnce(new Promise(resolve => { resolveOld = resolve }))
      .mockResolvedValueOnce(['a', 'b'])
      .mockResolvedValueOnce(['a'])
    const state = await mountView()
    await state.refreshCreateModelOptions()
    state.newModelMode.value = 'custom'
    state.updateCreateModels([])
    const old = state.refreshCreateModelOptions()
    await state.refreshCreateModelOptions()
    resolveOld(['a', 'old'])
    await old
    expect(state.createModelOptions.value).toEqual(['a', 'b'])
    expect(state.newModels.value).toEqual(['b'])
    expect(state.createModelsLoading.value).toBe(false)
    await state.openCreate()
    state.newVendor.value = 'codex'
    await state.refreshCreateModelOptions()
    expect(state.newModels.value).toEqual(['a'])
  })
})
