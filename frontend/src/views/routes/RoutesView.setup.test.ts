import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createRenderer, defineComponent, nextTick, ssrContextKey, type Ref } from 'vue'
import RoutesView from '../RoutesView.vue'
import * as api from '@/api/routes'
import type { RouteRuleItem } from '@/api/routes'

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
  getRouteReferenceTargets: vi.fn(), saveRouteRules: vi.fn(), savePoolQueue: vi.fn(), createRouteEntry: vi.fn()
}))

interface EditorSetup {
  rules: Ref<RouteRuleItem[]>
  selectedEntryName: Ref<string | null>
  entryNameInput: Ref<string>
  selectedSiteInstanceKey: Ref<string | null>
  availabilityDraft: Ref<{ key: string; mode: string; ranges: Array<{ start: string; end: string }> } | null>
  availabilityDirty: Ref<boolean>
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