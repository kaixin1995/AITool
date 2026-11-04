import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, defineComponent, nextTick, ssrContextKey, type Ref } from 'vue'
import RoutesView from '../RoutesView.vue'
import * as api from '@/api/routes'
import type { RouteRuleItem } from '@/api/routes'
import { ApiError } from '@/api/http'

const mocks = vi.hoisted(() => ({
  warning: vi.fn(),
  message: { warning: vi.fn(), success: vi.fn(), error: vi.fn() },
  push: vi.fn()
}))
vi.mock('vue-router', () => ({ useRouter: () => ({ push: mocks.push }) }))
vi.mock('naive-ui', () => ({
  NButton: {}, NInput: {}, NPopconfirm: {}, NSelect: {}, NTimePicker: {},
  useDialog: () => ({ warning: mocks.warning }),
  useMessage: () => mocks.message
}))
vi.mock('@/api/routes', async (importOriginal) => ({
  ...await importOriginal<typeof import('@/api/routes')>(),
  getRouteEntries: vi.fn(), getRouteRules: vi.fn(), getRouteSiteInstances: vi.fn(),
  getRouteReferenceTargets: vi.fn(), saveRouteRules: vi.fn(), savePoolQueue: vi.fn(), createRouteEntry: vi.fn(),
  deleteRouteEntry: vi.fn()
}))

interface EditorSetup {
  rules: Ref<RouteRuleItem[]>
  selectedEntryName: Ref<string | null>
  entryNameInput: Ref<string>
  selectedSiteInstanceKey: Ref<string | null>
  availabilityDraft: Ref<{ key: string; mode: string; ranges: Array<{ start: string; end: string }> } | null>
  availabilityDirty: Ref<boolean>
  openAvailabilityKey: Ref<string | null>
  canModifyQueue: Ref<boolean>
  canEdit: Ref<boolean>
  creatingEntry: Ref<boolean>
  loadedRulesEntryName: Ref<string | null>
  dirty: Ref<boolean>
  saving: Ref<boolean>
  toggleAvailabilityEditor(rule: RouteRuleItem): void
  handleAvailabilityChange(mode: string): void
  confirmAvailability(): void
  cancelAvailability(): void
  toggleRule(rule: RouteRuleItem): void
  handleMoveRule(index: number, direction: -1 | 1): void
  addSelectedCandidate(): void
  removeRule(index: number): void
  handleSave(): Promise<void>
  changeEntry(name: string): void
  loadSelectedEntry(name: string): Promise<void>
  createEntry(): void
  deleteCurrentEntry(confirmReferenced?: boolean, expectedEntryName?: string | null): Promise<void>
}

// Execute the actual SFC setup inside a Vue component instance without a DOM,
// so lifecycle, computed guards, API calls and dialog callbacks stay real.
const renderer = createRenderer<object, object>({
  patchProp: () => {}, insert: () => {}, remove: () => {},
  createElement: () => ({}), createText: () => ({}), createComment: () => ({}),
  setText: () => {}, setElementText: () => {}, parentNode: () => null,
  nextSibling: () => null
})

async function flushAsync(): Promise<void> {
  for (let index = 0; index < 12; index++) await Promise.resolve()
  await nextTick()
}

async function mountEditor(): Promise<EditorSetup> {
  let editor!: EditorSetup
  const component = RoutesView as unknown as {
    setup(props: { embedded: boolean }, context: { expose: () => void }): EditorSetup
  }
  const app = renderer.createApp(defineComponent({
    setup() {
      editor = component.setup({ embedded: true }, { expose: () => {} })
      return () => null
    }
  }))
  app.provide(ssrContextKey, {})
  app.mount({})
  await flushAsync()
  return editor
}

function openDirtyDraft(editor: EditorSetup): void {
  editor.toggleAvailabilityEditor(editor.rules.value[0])
  editor.handleAvailabilityChange('Unavailable')
  editor.availabilityDraft.value!.ranges[0] = { start: '10:00', end: '11:00' }
  expect(editor.availabilityDirty.value).toBe(true)
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.getRouteEntries).mockResolvedValue([
    { entryName: 'auto', candidateCount: 2 }, { entryName: 'backup', candidateCount: 0 }
  ])
  vi.mocked(api.getRouteSiteInstances).mockResolvedValue([
    { siteId: 'site', siteName: 'Primary', siteModelName: 'model', siteEnabled: true, protocolType: 'OpenAI' }
  ])
  vi.mocked(api.getRouteReferenceTargets).mockResolvedValue([])
  vi.mocked(api.getRouteRules).mockImplementation(async () => ['first', 'second'].map((ruleId) => ({
    ruleId, siteId: 'site', siteName: 'Primary', siteEnabled: true,
    siteModelName: 'model', upstreamModelName: 'model', priority: 0,
    modelPriority: 0, instancePriority: 0, isEnabled: true,
    availabilityMode: 'AllDay', timeRangesJson: ''
  })))
  vi.mocked(api.saveRouteRules).mockResolvedValue({ message: 'saved' })
  vi.mocked(api.deleteRouteEntry).mockResolvedValue()
})

describe('RoutesView availability draft protection', () => {
  it('does not toggle, move, add, remove, save or reload over an unconfirmed time draft', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    const original = [...editor.rules.value]
    const draft = editor.availabilityDraft.value
    editor.selectedSiteInstanceKey.value = 'site::model'

    editor.toggleRule(editor.rules.value[0])
    editor.handleMoveRule(0, 1)
    editor.addSelectedCandidate()
    editor.removeRule(0)
    editor.toggleAvailabilityEditor(editor.rules.value[1])
    await editor.handleSave()
    await editor.loadSelectedEntry('auto')
    await flushAsync()

    expect(editor.canModifyQueue.value).toBe(false)
    expect(editor.rules.value).toEqual(original)
    expect(editor.rules.value[0].isEnabled).toBe(true)
    expect(editor.rules.value[0].availabilityMode).toBe('AllDay')
    expect(editor.availabilityDraft.value).toBe(draft)
    expect(editor.availabilityDraft.value).toMatchObject({ mode: 'Unavailable', ranges: [{ start: '10:00', end: '11:00' }] })
    expect(api.saveRouteRules).not.toHaveBeenCalled()
    expect(api.getRouteRules).toHaveBeenCalledTimes(1)
  })

  it('saves the explicit time confirmation before allowing unrelated autosaves', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    editor.confirmAvailability()
    await flushAsync()

    expect(api.saveRouteRules).toHaveBeenCalledWith('auto', expect.arrayContaining([
      expect.objectContaining({ availabilityMode: 'Unavailable', timeRangesJson: '[{"start":"10:00","end":"11:00"}]' })
    ]))
    expect(editor.availabilityDraft.value).toBeNull()
    expect(editor.availabilityDirty.value).toBe(false)
    expect(editor.canModifyQueue.value).toBe(true)
  })

  it('allows a normal toggle autosave only after explicit draft cancellation', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    editor.cancelAvailability()
    editor.toggleRule(editor.rules.value[0])
    await flushAsync()
    expect(api.saveRouteRules).toHaveBeenCalledWith('auto', expect.arrayContaining([
      expect.objectContaining({ isEnabled: false, availabilityMode: 'AllDay', timeRangesJson: '' })
    ]))
  })

  it('requires discard confirmation to switch entries when only the time draft is dirty', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    expect(editor.dirty.value).toBe(false)
    editor.changeEntry('backup')
    expect(editor.selectedEntryName.value).toBe('auto')
    expect(editor.availabilityDirty.value).toBe(true)
    expect(api.getRouteRules).toHaveBeenCalledTimes(1)

    // A queued discard callback cannot switch entries during an active save.
    const discard = mocks.warning.mock.calls[0][0].onPositiveClick
    editor.saving.value = true
    discard()
    expect(editor.selectedEntryName.value).toBe('auto')
    expect(editor.availabilityDirty.value).toBe(true)
    editor.saving.value = false
    discard()
    await flushAsync()
    expect(editor.selectedEntryName.value).toBe('backup')
    expect(editor.availabilityDraft.value).toBeNull()
    expect(api.getRouteRules).toHaveBeenLastCalledWith('backup')
  })

  it('locks queue and availability editing until a delayed create has loaded the new entry', async () => {
    const editor = await mountEditor()
    let resolveCreate!: () => void
    vi.mocked(api.createRouteEntry).mockReturnValueOnce(new Promise<void>((resolve) => {
      resolveCreate = resolve
    }))
    vi.mocked(api.getRouteEntries).mockResolvedValueOnce([
      { entryName: 'auto', candidateCount: 2 }, { entryName: 'new-entry', candidateCount: 0 }
    ])
    vi.mocked(api.getRouteRules).mockResolvedValueOnce([])
    const original = [...editor.rules.value]
    editor.selectedSiteInstanceKey.value = 'site::model'
    editor.entryNameInput.value = 'new-entry'
    editor.createEntry()
    await flushAsync()

    expect(editor.creatingEntry.value).toBe(true)
    expect(editor.canEdit.value).toBe(false)
    expect(editor.canModifyQueue.value).toBe(false)
    editor.toggleAvailabilityEditor(editor.rules.value[0])
    editor.handleAvailabilityChange('Unavailable')
    editor.confirmAvailability()
    editor.toggleRule(editor.rules.value[0])
    editor.handleMoveRule(0, 1)
    editor.addSelectedCandidate()
    editor.removeRule(0)
    expect(editor.availabilityDraft.value).toBeNull()
    expect(editor.availabilityDirty.value).toBe(false)
    expect(editor.rules.value).toEqual(original)
    expect(api.saveRouteRules).not.toHaveBeenCalled()
    expect(editor.selectedEntryName.value).toBe('auto')

    resolveCreate()
    await flushAsync()
    expect(editor.creatingEntry.value).toBe(false)
    expect(editor.selectedEntryName.value).toBe('new-entry')
    expect(editor.loadedRulesEntryName.value).toBe('new-entry')
    expect(editor.rules.value).toEqual([])
    expect(editor.availabilityDraft.value).toBeNull()
    expect(editor.canEdit.value).toBe(true)
    expect(api.getRouteRules).toHaveBeenLastCalledWith('new-entry')
  })

  it('requires explicit discard before creating and switching to another entry', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    editor.entryNameInput.value = 'new-entry'
    editor.createEntry()
    expect(api.createRouteEntry).not.toHaveBeenCalled()
    expect(editor.availabilityDirty.value).toBe(true)
    mocks.warning.mock.calls[0][0].onPositiveClick()
    await flushAsync()
    expect(api.createRouteEntry).toHaveBeenCalledWith('new-entry')
    expect(editor.availabilityDraft.value).toBeNull()
  })

  it('keeps time draft and dirty flag when deletion fails with non-409 error', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    const originalDraft = editor.availabilityDraft.value
    vi.mocked(api.deleteRouteEntry).mockRejectedValueOnce(new Error('网络异常，删除失败'))

    await editor.deleteCurrentEntry()
    await flushAsync()

    expect(api.deleteRouteEntry).toHaveBeenCalledWith('auto', false)
    expect(editor.availabilityDraft.value).toBe(originalDraft)
    expect(editor.availabilityDirty.value).toBe(true)
    expect(editor.openAvailabilityKey.value).toBe('first')
    expect(mocks.message.error).toHaveBeenCalledWith('网络异常，删除失败')
  })

  it('retains time draft when 409 reference confirmation dialog is shown and not confirmed', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    const originalDraft = editor.availabilityDraft.value
    vi.mocked(api.deleteRouteEntry).mockRejectedValueOnce(new ApiError('被子路由引用', 'ROUTE_ENTRY_REFERENCED', 409))

    await editor.deleteCurrentEntry()
    await flushAsync()

    expect(api.deleteRouteEntry).toHaveBeenCalledWith('auto', false)
    expect(mocks.warning).toHaveBeenCalledTimes(1)
    expect(mocks.warning.mock.calls[0][0]).toMatchObject({
      title: '该入口正被其他路由引用',
      positiveText: '仍然删除被引用入口',
      negativeText: '取消'
    })
    expect(editor.availabilityDraft.value).toBe(originalDraft)
    expect(editor.availabilityDirty.value).toBe(true)
    expect(editor.openAvailabilityKey.value).toBe('first')
  })

  it('retries with confirmReferenced=true on 409 dialog confirmation and clears draft on success', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    vi.mocked(api.deleteRouteEntry)
      .mockRejectedValueOnce(new ApiError('被子路由引用', 'ROUTE_ENTRY_REFERENCED', 409))
      .mockResolvedValueOnce()
    vi.mocked(api.getRouteEntries).mockResolvedValueOnce([{ entryName: 'backup', candidateCount: 0 }])
    vi.mocked(api.getRouteRules).mockResolvedValueOnce([])

    await editor.deleteCurrentEntry()
    await flushAsync()

    expect(api.deleteRouteEntry).toHaveBeenCalledTimes(1)
    expect(api.deleteRouteEntry).toHaveBeenLastCalledWith('auto', false)
    expect(editor.availabilityDraft.value).not.toBeNull()

    const confirmDelete = mocks.warning.mock.calls[0][0].onPositiveClick
    await confirmDelete()
    await flushAsync()

    expect(api.deleteRouteEntry).toHaveBeenCalledTimes(2)
    expect(api.deleteRouteEntry).toHaveBeenLastCalledWith('auto', true)
    expect(editor.availabilityDraft.value).toBeNull()
    expect(editor.openAvailabilityKey.value).toBeNull()
    expect(editor.availabilityDirty.value).toBe(false)
    expect(mocks.message.success).toHaveBeenCalledWith('主入口已删除')
  })

  it('clears time draft and open availability state only after direct deletion succeeds', async () => {
    const editor = await mountEditor()
    openDirtyDraft(editor)
    vi.mocked(api.deleteRouteEntry).mockResolvedValueOnce()
    vi.mocked(api.getRouteEntries).mockResolvedValueOnce([{ entryName: 'backup', candidateCount: 0 }])
    vi.mocked(api.getRouteRules).mockResolvedValueOnce([])

    await editor.deleteCurrentEntry()
    await flushAsync()

    expect(api.deleteRouteEntry).toHaveBeenCalledWith('auto', false)
    expect(editor.availabilityDraft.value).toBeNull()
    expect(editor.openAvailabilityKey.value).toBeNull()
    expect(editor.availabilityDirty.value).toBe(false)
    expect(mocks.message.success).toHaveBeenCalledWith('主入口已删除')
  })
})

describe('pool-managed route queue', () => {
  it('allows reordering and sends loaded versions without enabling destructive operations', async () => {
    vi.mocked(api.getRouteEntries).mockResolvedValue([{ entryId: 'pool-entry', managedByPoolId: 'pool', entryName: 'auto', candidateCount: 2 }])
    vi.mocked(api.savePoolQueue).mockResolvedValue({ message: 'saved' })
    const editor = await mountEditor()
    expect(editor.canModifyQueue.value).toBe(true)
    editor.removeRule(0)
    editor.addSelectedCandidate()
    expect(editor.rules.value).toHaveLength(2)
    editor.handleMoveRule(0, 1)
    await flushAsync()
    expect(api.savePoolQueue).toHaveBeenCalledWith('pool-entry', [
      expect.objectContaining({ ruleId: 'second', expectedIsEnabled: true, isEnabled: true }),
      expect.objectContaining({ ruleId: 'first', expectedIsEnabled: true, isEnabled: true })
    ])
    expect(api.saveRouteRules).not.toHaveBeenCalled()
  })

  it('allows disabling one candidate and reloads on conflict', async () => {
    vi.mocked(api.getRouteEntries).mockResolvedValue([{ entryId: 'pool-entry', managedByPoolId: 'pool', entryName: 'auto', candidateCount: 2 }])
    vi.mocked(api.savePoolQueue).mockRejectedValue(new Error('候选队列已变更，请刷新后重试'))
    const editor = await mountEditor()
    editor.toggleRule(editor.rules.value[0])
    await flushAsync()
    expect(api.savePoolQueue).toHaveBeenCalledWith('pool-entry', [
      expect.objectContaining({ ruleId: 'first', expectedIsEnabled: true, isEnabled: false }),
      expect.objectContaining({ ruleId: 'second', expectedIsEnabled: true, isEnabled: true })
    ])
    expect(mocks.message.error).toHaveBeenCalledWith('候选队列已变更，请刷新后重试')
    expect(api.getRouteRules).toHaveBeenCalledTimes(2)
    expect(editor.rules.value[0].isEnabled).toBe(true)
  })
})