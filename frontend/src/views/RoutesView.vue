<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  NButton,
  NInput,
  NPopconfirm,
  NSelect,
  NTimePicker,
  useDialog,
  useMessage
} from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/routes'
import type {
  RouteAvailabilityMode,
  RouteEntry,
  RouteRuleItem,
  SiteInstanceItem
} from '@/api/routes'
import {
  appendCandidate,
  buildSaveRules,
  chooseSelectedEntryAfterReload,
  createRuleKeyResolver,
  filterSiteInstances,
  getDeleteEntryConfirmation,
  isLatestRouteLoad,
  moveCandidate
} from './routes/routeEditorState'

interface TimeRange {
  start: string
  end: string
}

const props = withDefaults(defineProps<{ embedded?: boolean }>(), { embedded: false })
const dialog = useDialog()
const message = useMessage()
const entries = ref<RouteEntry[]>([])
const selectedEntryName = ref<string | null>(null)
const rules = ref<RouteRuleItem[]>([])
const siteInstances = ref<SiteInstanceItem[]>([])
const entryNameInput = ref('')
const candidateSearch = ref('')
const selectedSiteInstanceKey = ref<string | null>(null)
const loading = ref(false)
const refreshingPool = ref(false)
const creatingEntry = ref(false)
const saving = ref(false)
const dirty = ref(false)
// 时间规则编辑草稿：模式与时间段只改草稿，点「确定」才写回规则并保存，点「取消」或再次点击按钮丢弃。
const availabilityDraft = ref<{ key: string; mode: RouteAvailabilityMode; ranges: TimeRange[] } | null>(null)
const openAvailabilityKey = ref<string | null>(null)
const draggingRuleIndex = ref<number | null>(null)
const dragOverRuleIndex = ref<number | null>(null)
const resolveRuleKey = createRuleKeyResolver()
let latestLoadToken = 0
let pendingSaveAfterCurrent = false

const availabilityOptions = api.routeAvailabilityOptions
const selectedEntry = computed(
  () => entries.value.find((entry) => entry.entryName === selectedEntryName.value) ?? null
)
const filteredInstances = computed(
  () => filterSiteInstances(siteInstances.value, candidateSearch.value)
)
const siteInstanceOptions = computed(() => filteredInstances.value.map((instance) => ({
  label: `${instance.siteName} / ${instance.siteModelName}`,
  value: `${instance.siteId}::${instance.siteModelName}`
})))
const selectedSiteInstance = computed(() => filteredInstances.value.find(
  (instance) => `${instance.siteId}::${instance.siteModelName}` === selectedSiteInstanceKey.value
) ?? null)
const canEdit = computed(
  () => Boolean(selectedEntryName.value) && !loading.value && !saving.value
)

function parseTimeRanges(json: string): TimeRange[] {
  if (!json) return [{ start: '00:00', end: '23:59' }]

  try {
    const ranges = JSON.parse(json)
    return Array.isArray(ranges) && ranges.length > 0
      ? ranges
      : [{ start: '00:00', end: '23:59' }]
  } catch {
    return [{ start: '00:00', end: '23:59' }]
  }
}

function serializeTimeRanges(ranges: TimeRange[]): string {
  return JSON.stringify(ranges)
}

function getAvailabilitySummary(rule: RouteRuleItem): { text: string; className: string } {
  const ranges = rule.availabilityMode === 'AllDay' ? [] : parseTimeRanges(rule.timeRangesJson)
  if (rule.availabilityMode === 'AllDay' || ranges.length === 0) {
    return { text: '全天可用', className: 'route-badge-muted' }
  }

  const range = ranges[0]
  if (rule.availabilityMode === 'AvailableOnly') {
    return { text: `${range.start}-${range.end} 可用`, className: '' }
  }

  return { text: `${range.start}-${range.end} 不可用`, className: 'route-badge-warning' }
}

function isAvailabilityOpen(rule: RouteRuleItem): boolean {
  return openAvailabilityKey.value === resolveRuleKey(rule)
}

function toggleAvailabilityEditor(rule: RouteRuleItem): void {
  const key = resolveRuleKey(rule)
  if (openAvailabilityKey.value === key) {
    // 再次点击同一行：丢弃草稿并折叠
    availabilityDraft.value = null
    openAvailabilityKey.value = null
    return
  }
  // 打开编辑器：用已保存值初始化草稿，编辑期间不落库
  availabilityDraft.value = { key, mode: rule.availabilityMode, ranges: parseTimeRanges(rule.timeRangesJson) }
  openAvailabilityKey.value = key
}

function markDirty(): void {
  dirty.value = true
}

function saveAfterChange(): void {
  markDirty()
  void handleSave()
}

function handleAvailabilityChange(mode: RouteAvailabilityMode): void {
  // 只改草稿，不保存；由「确定」统一写回
  if (availabilityDraft.value) {
    availabilityDraft.value.mode = mode
  }
}

function confirmAvailability(): void {
  const draft = availabilityDraft.value
  if (!draft) return
  const rule = rules.value.find((item) => resolveRuleKey(item) === draft.key)
  if (rule) {
    rule.availabilityMode = draft.mode
    rule.timeRangesJson = draft.mode === 'AllDay' ? '' : serializeTimeRanges(draft.ranges)
    saveAfterChange()
  }
  availabilityDraft.value = null
  openAvailabilityKey.value = null
}

function cancelAvailability(): void {
  // 丢弃草稿并折叠，规则保持已保存状态
  availabilityDraft.value = null
  openAvailabilityKey.value = null
}

function timeToTimestamp(value: string): number {
  const [hours, minutes] = value.split(':').map(Number)
  const date = new Date()
  date.setHours(hours || 0, minutes || 0, 0, 0)
  return date.getTime()
}

function timestampToTime(value: number): string {
  const date = new Date(value)
  return `${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
}

async function loadEntries(preferredEntryName?: string | null): Promise<void> {
  const loadedEntries = await api.getRouteEntries()
  entries.value = loadedEntries
  selectedEntryName.value = chooseSelectedEntryAfterReload(
    loadedEntries,
    selectedEntryName.value,
    preferredEntryName
  )
}

async function refreshSiteInstances(): Promise<void> {
  refreshingPool.value = true
  try {
    siteInstances.value = await api.getRouteSiteInstances()
  } finally {
    refreshingPool.value = false
  }
}

async function loadSelectedEntry(entryName: string | null): Promise<void> {
  const token = ++latestLoadToken

  if (!entryName) {
    rules.value = []
    availabilityDraft.value = null
    openAvailabilityKey.value = null
    loading.value = false
    return
  }

  loading.value = true
  try {
    const loadedRules = await api.getRouteRules(entryName)
    if (!isLatestRouteLoad(token, latestLoadToken)) return

    rules.value = loadedRules
    // 规则重载后旧的编辑草稿/展开状态失效，一并清理
    availabilityDraft.value = null
    openAvailabilityKey.value = null
    dirty.value = false
  } finally {
    if (isLatestRouteLoad(token, latestLoadToken)) {
      loading.value = false
    }
  }
}

function changeEntry(entryName: string | null): void {
  if (saving.value || creatingEntry.value || entryName === selectedEntryName.value) return

  const proceed = (): void => {
    selectedEntryName.value = entryName
    void loadSelectedEntry(entryName)
  }

  if (!dirty.value) {
    proceed()
    return
  }

  dialog.warning({
    title: '放弃未保存修改？',
    content: '切换主入口会放弃当前候选队列的未保存修改。',
    positiveText: '放弃并切换',
    negativeText: '继续编辑',
    onPositiveClick: proceed
  })
}

async function createEntryNow(entryName: string): Promise<void> {
  creatingEntry.value = true
  try {
    await api.createRouteEntry(entryName)
    entryNameInput.value = ''
    await loadEntries(entryName)
    await loadSelectedEntry(selectedEntryName.value)
    message.success('主入口已创建')
  } finally {
    creatingEntry.value = false
  }
}

function createEntry(): void {
  const entryName = entryNameInput.value.trim()
  if (!entryName) {
    message.warning('请输入主入口名称')
    return
  }

  const proceed = (): void => {
    void createEntryNow(entryName)
  }

  if (!dirty.value) {
    proceed()
    return
  }

  dialog.warning({
    title: '放弃未保存修改？',
    content: '创建并切换到新主入口会放弃当前候选队列的未保存修改。',
    positiveText: '放弃并创建',
    negativeText: '继续编辑',
    onPositiveClick: proceed
  })
}

async function deleteCurrentEntry(): Promise<void> {
  const entryName = selectedEntryName.value
  if (!entryName) return

  await api.deleteRouteEntry(entryName)
  rules.value = []
  availabilityDraft.value = null
  openAvailabilityKey.value = null
  dirty.value = false
  await loadEntries()
  await loadSelectedEntry(selectedEntryName.value)
  message.success('主入口已删除')
}

function addCandidate(instance: SiteInstanceItem): void {
  rules.value = appendCandidate(rules.value, instance)
  saveAfterChange()
}

function addSelectedCandidate(): void {
  if (!selectedSiteInstance.value) {
    message.warning('请选择站点实例')
    return
  }

  addCandidate(selectedSiteInstance.value)
  selectedSiteInstanceKey.value = null
}

function handleRuleDragStart(index: number, event: DragEvent): void {
  if (!canEdit.value) {
    event.preventDefault()
    return
  }

  const target = event.target as HTMLElement | null
  if (target?.closest('button, input, select, .n-button, .n-select, .n-time-picker, .availability-popover, .route-actions, .order-actions')) {
    event.preventDefault()
    return
  }

  draggingRuleIndex.value = index
  dragOverRuleIndex.value = index

  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'move'
    // Firefox / Safari 强制要求在 dragstart 中调用 setData，否则判定为无效拖拽并直接取消
    event.dataTransfer.setData('text/plain', String(index))
    event.dataTransfer.setData('application/x-aitool-route-index', String(index))
  }
}

function handleRuleDragOver(index: number, event: DragEvent): void {
  event.preventDefault()
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = 'move'
  }
  if (draggingRuleIndex.value === null) return
  if (dragOverRuleIndex.value !== index) {
    dragOverRuleIndex.value = index
  }
}

function handleRuleDragEnter(index: number, event: DragEvent): void {
  event.preventDefault()
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = 'move'
  }
  if (draggingRuleIndex.value !== null) {
    dragOverRuleIndex.value = index
  }
}

function handleRuleDrop(targetIndex: number, event: DragEvent): void {
  event.preventDefault()
  const from = draggingRuleIndex.value
  if (from !== null && from !== targetIndex && from >= 0 && from < rules.value.length) {
    const nextRules = [...rules.value]
    const [draggedRule] = nextRules.splice(from, 1)
    nextRules.splice(targetIndex, 0, draggedRule)
    rules.value = nextRules
    saveAfterChange()
  }
  draggingRuleIndex.value = null
  dragOverRuleIndex.value = null
}

function handleRuleDragEnd(): void {
  draggingRuleIndex.value = null
  dragOverRuleIndex.value = null
}

function handleMoveRule(index: number, direction: -1 | 1): void {
  if (!canEdit.value) return
  const next = moveCandidate(rules.value, index, direction)
  if (next !== rules.value) {
    rules.value = next
    saveAfterChange()
  }
}

function removeRule(index: number): void {
  rules.value = rules.value.filter((_, ruleIndex) => ruleIndex !== index)
  availabilityDraft.value = null
  openAvailabilityKey.value = null
  saveAfterChange()
}

async function handleSave(): Promise<void> {
  const entryName = selectedEntryName.value
  if (!entryName || !dirty.value) return
  if (saving.value) {
    pendingSaveAfterCurrent = true
    return
  }

  saving.value = true
  pendingSaveAfterCurrent = false
  try {
    const response = await api.saveRouteRules(entryName, buildSaveRules(rules.value))
    await Promise.all([
      loadEntries(entryName),
      refreshSiteInstances()
    ])
    if (!pendingSaveAfterCurrent) {
      await loadSelectedEntry(entryName)
    }
    message.success(response.message || '路由规则已保存')
  } finally {
    saving.value = false
    if (pendingSaveAfterCurrent) {
      void handleSave()
    }
  }
}

async function initialize(): Promise<void> {
  loading.value = true
  try {
    await Promise.all([loadEntries(), refreshSiteInstances()])
    await loadSelectedEntry(selectedEntryName.value)
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void initialize()
})
</script>

<template>
  <div :class="{ 'page-container': !props.embedded }">
    <PageHeader
      v-if="!props.embedded"
      title="路由规则管理"
      subtitle="主入口绑定有序候选实例队列，请求时按顺序失败切换、成功即停止"
    />

    <div class="route-layout">
      <section class="route-panel route-panel-left">
        <div class="route-panel-header">
          <div>
            <h5 class="route-panel-title">主入口</h5>
            <div class="route-panel-subtitle">管理对外暴露的本地路由入口</div>
          </div>
        </div>

        <div class="route-create-box">
          <label class="route-field-label" for="route-entry-name">新建主入口</label>
          <div class="entry-create-row">
            <NInput
              id="route-entry-name"
              v-model:value="entryNameInput"
              placeholder="例如 auto"
              :disabled="creatingEntry || saving"
              @keyup.enter="createEntry"
            />
            <NButton type="primary" :loading="creatingEntry" :disabled="saving" @click="createEntry">创建</NButton>
          </div>
        </div>

        <div v-if="entries.length === 0" class="route-empty-state">暂无主入口，请先创建</div>
        <div v-else class="entry-list">
          <button
            v-for="entry in entries"
            :key="entry.entryName"
            type="button"
            class="entry-item"
            :class="{ active: entry.entryName === selectedEntryName }"
            @click="changeEntry(entry.entryName)"
          >
            <span class="entry-name">{{ entry.displayName || entry.entryName }}</span>
            <span class="entry-count">{{ entry.candidateCount }} 个候选</span>
          </button>
        </div>
      </section>

      <section class="route-panel route-panel-right">
        <div class="route-panel-header route-panel-header-stack">
          <div>
            <h5 class="route-panel-title">候选实例队列</h5>
            <div class="route-panel-subtitle">
              <template v-if="selectedEntry">当前主入口：{{ selectedEntry.displayName || selectedEntry.entryName }}</template>
              <template v-else>请选择或创建一个主入口</template>
            </div>
          </div>
          <NPopconfirm
            v-if="selectedEntryName"
            positive-text="删除"
            negative-text="取消"
            @positive-click="deleteCurrentEntry"
          >
            <template #trigger>
              <NButton size="small" type="error" secondary :disabled="saving || creatingEntry">删除当前主入口</NButton>
            </template>
            {{ getDeleteEntryConfirmation(dirty) }}
          </NPopconfirm>
        </div>

        <div v-if="!selectedEntryName" class="route-empty-state">请选择或创建一个主入口</div>
        <template v-else>
          <div class="route-add-box">
            <label class="route-add-field">
              <span class="route-field-label">搜索候选实例</span>
              <NInput v-model:value="candidateSearch" clearable placeholder="搜索站点或模型" :disabled="saving" />
            </label>
            <label class="route-add-field">
              <span class="route-field-label">添加候选实例</span>
              <NSelect
                v-model:value="selectedSiteInstanceKey"
                :options="siteInstanceOptions"
                filterable
                clearable
                placeholder="-- 请选择站点实例 --"
                :disabled="saving"
              />
            </label>
            <NButton type="primary" secondary :disabled="!canEdit" @click="addSelectedCandidate">追加到队列末尾</NButton>
            <NButton secondary :loading="refreshingPool" :disabled="saving" @click="refreshSiteInstances">刷新实例池</NButton>
          </div>

          <div class="queue-card">
            <div class="queue-card-header">拖拽调整顺序，修改后立即保存生效</div>
            <div class="queue-card-body">
              <div v-if="!loading && rules.length === 0" class="route-empty-state">当前主入口暂无候选实例，请先添加</div>
              <div v-else class="route-sort-list">
                <div
                  v-for="(rule, index) in rules"
                  :key="resolveRuleKey(rule)"
                  class="route-item"
                  :class="{
                    dragging: draggingRuleIndex === index,
                    'drop-target-active': dragOverRuleIndex === index && draggingRuleIndex !== null && draggingRuleIndex !== index
                  }"
                  :draggable="canEdit"
                  @dragstart="handleRuleDragStart(index, $event)"
                  @dragover="handleRuleDragOver(index, $event)"
                  @dragenter="handleRuleDragEnter(index, $event)"
                  @drop="handleRuleDrop(index, $event)"
                  @dragend="handleRuleDragEnd"
                >
                  <span class="drag-handle" title="按住拖拽调整顺序" aria-hidden="true">⠿</span>
                  <span class="priority-num">{{ index + 1 }}</span>
                  <div class="site-info">
                    <div class="site-name-row">
                      <span class="site-name" :class="{ 'site-disabled': !rule.siteEnabled }">{{ rule.siteName }}</span>
                      <span v-if="!rule.siteEnabled" class="route-badge badge-disabled">站点已禁用</span>
                      <span :class="['route-badge', getAvailabilitySummary(rule).className]">{{ getAvailabilitySummary(rule).text }}</span>
                      <span v-if="!rule.isEnabled" class="route-badge route-badge-muted">规则已禁用</span>
                    </div>
                    <!-- 上游模型优先显示对外名（显示名称），未匹配模型库时回退站点名 -->
                    <div class="remote-name">上游模型：{{ rule.modelDisplayName || rule.upstreamModelName }} · 站点实例：{{ rule.siteModelName }}</div>
                  </div>
                  <div class="order-actions">
                    <NButton
                      size="tiny"
                      quaternary
                      :disabled="!canEdit || index === 0"
                      title="上移"
                      @click.stop="handleMoveRule(index, -1)"
                    >
                      ▲
                    </NButton>
                    <NButton
                      size="tiny"
                      quaternary
                      :disabled="!canEdit || index === rules.length - 1"
                      title="下移"
                      @click.stop="handleMoveRule(index, 1)"
                    >
                      ▼
                    </NButton>
                  </div>
                  <NButton size="small" secondary class="availability-trigger" :disabled="!canEdit" @click.stop="toggleAvailabilityEditor(rule)">时间规则</NButton>
                  <div class="route-actions">
                    <NPopconfirm @positive-click="removeRule(index)">
                      <template #trigger>
                        <NButton size="small" secondary type="error" :disabled="!canEdit">移除</NButton>
                      </template>
                      从候选队列移除此规则？
                    </NPopconfirm>
                  </div>
                  <div :class="['availability-popover', { open: isAvailabilityOpen(rule) }]">
                    <!-- 编辑只改草稿（availabilityDraft），点「确定」才写回规则并保存，点「取消」丢弃 -->
                    <NSelect
                      :value="availabilityDraft?.mode"
                      :options="availabilityOptions"
                      size="small"
                      :disabled="!canEdit"
                      class="availability-select"
                      @update:value="(value: RouteAvailabilityMode) => handleAvailabilityChange(value)"
                    />
                    <span v-show="availabilityDraft?.mode && availabilityDraft.mode !== 'AllDay'" class="time-range-fields">
                      <NTimePicker
                        :value="timeToTimestamp(availabilityDraft?.ranges[0]?.start ?? '00:00')"
                        format="HH:mm"
                        size="small"
                        :disabled="!canEdit"
                        @update:value="(value: number | null) => { if (value !== null && availabilityDraft) availabilityDraft.ranges[0].start = timestampToTime(value) }"
                      />
                      <span>至</span>
                      <NTimePicker
                        :value="timeToTimestamp(availabilityDraft?.ranges[0]?.end ?? '23:59')"
                        format="HH:mm"
                        size="small"
                        :disabled="!canEdit"
                        @update:value="(value: number | null) => { if (value !== null && availabilityDraft) availabilityDraft.ranges[0].end = timestampToTime(value) }"
                      />
                    </span>
                    <div class="availability-actions">
                      <NButton size="tiny" secondary :disabled="!canEdit" @click="cancelAvailability">取消</NButton>
                      <NButton size="tiny" type="primary" :disabled="!canEdit" @click="confirmAvailability">确定</NButton>
                    </div>
                    <span class="availability-hint">留空或无效配置会按全天可用处理</span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </template>
      </section>
    </div>
  </div>
</template>

<style scoped>
.route-layout {
  display: grid;
  grid-template-columns: 320px minmax(0, 1fr);
  gap: 20px;
  align-items: start;
}

.route-panel {
  min-width: 0;
  overflow: hidden;
  border: 1px solid var(--border-color-global);
  border-radius: 12px;
  background: var(--bg-card);
}

.route-panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 16px 18px;
  border-bottom: 1px solid var(--border-color-global);
  background: var(--bg-page);
}

.route-panel-header-stack {
  align-items: flex-start;
}

.route-panel-title {
  margin: 0;
  color: var(--text-primary);
  font-size: 16px;
  font-weight: 700;
}

.route-panel-subtitle {
  margin-top: 4px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.route-create-box,
.route-add-box {
  padding: 16px 18px;
  border-bottom: 1px solid var(--border-color-global);
}

.route-field-label {
  display: block;
  margin-bottom: 6px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.entry-create-row {
  display: flex;
  align-items: flex-end;
  gap: 8px;
}

.route-add-box {
  display: grid;
  grid-template-columns: minmax(180px, 4fr) minmax(220px, 4fr) auto auto;
  align-items: end;
  gap: 8px;
}

.entry-create-row :deep(.n-input) {
  min-width: 0;
}

.entry-list {
  padding: 10px;
}

.entry-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  gap: 12px;
  padding: 12px 14px;
  margin-bottom: 8px;
  border: 1px solid var(--border-color-global);
  border-radius: 10px;
  background: var(--bg-card);
  color: var(--text-primary);
  cursor: pointer;
  text-align: left;
  transition: border-color 0.15s, background 0.15s;
}

.entry-item:hover {
  border-color: var(--status-info-text);
  background: var(--status-info-bg);
}

.entry-item.active {
  border-color: var(--primary-color, #6C9EFF);
  background: var(--status-info-bg);
  color: var(--status-info-text);
}

.entry-name {
  min-width: 0;
  overflow: hidden;
  font-size: 14px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.entry-count {
  flex-shrink: 0;
  color: var(--text-color-secondary);
  font-size: 12px;
}

.route-empty-state {
  padding: 28px 18px;
  color: var(--text-color-secondary);
  text-align: center;
}

.route-add-field {
  min-width: 0;
}


.queue-card-header {
  padding: 14px 18px;
  border-bottom: 1px solid var(--border-color-global);
  background: var(--bg-page);
  color: var(--text-color-secondary);
  font-size: 13px;
}

.queue-card-body {
  min-height: 240px;
}

.route-sort-list {
  min-height: 48px;
}

.route-item {
  display: grid;
  grid-template-columns: auto auto minmax(0, 1fr) auto auto auto;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  border-bottom: 1px solid var(--border-color-global);
  cursor: grab;
  user-select: none;
  -webkit-user-select: none;
  -moz-user-select: none;
  -webkit-user-drag: element;
  transition: background 0.15s, outline 0.15s;
}

.route-item:active {
  cursor: grabbing;
}

.route-item:hover {
  background: var(--bg-page);
}

.route-item.dragging {
  opacity: 0.45;
  background: var(--status-info-bg);
}

.route-item.drop-target-active {
  background: color-mix(in srgb, var(--primary-color) 12%, var(--bg-card));
  outline: 2px dashed var(--primary-color);
  outline-offset: -2px;
}

.drag-handle {
  flex-shrink: 0;
  color: var(--text-color-secondary);
  font-size: 18px;
  cursor: grab;
  touch-action: none;
  user-select: none;
  padding: 2px 4px;
  border-radius: 4px;
  transition: color 0.15s, background 0.15s;
}

.drag-handle:hover {
  color: var(--primary-color);
  background: var(--bg-page);
}

.order-actions {
  display: flex;
  flex-direction: column;
  gap: 2px;
  flex-shrink: 0;
}

.order-actions :deep(.n-button) {
  height: 18px;
  width: 24px;
  padding: 0;
  font-size: 9px;
  line-height: 1;
  color: var(--text-color-secondary);
}

.order-actions :deep(.n-button:hover:not(:disabled)) {
  color: var(--primary-color);
}

.priority-num {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  background: var(--status-info-bg);
  color: var(--status-info-text);
  font-size: 13px;
  font-weight: 600;
}

.site-info {
  min-width: 0;
}

.site-name-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.site-name {
  font-size: 14px;
  font-weight: 600;
}

.site-disabled {
  color: var(--text-color-disabled);
}

.route-badge {
  display: inline-flex;
  align-items: center;
  min-height: 22px;
  padding: 2px 8px;
  border: 1px solid color-mix(in srgb, var(--status-info-text) 28%, transparent);
  border-radius: 999px;
  background: var(--status-info-bg);
  color: var(--status-info-text);
  font-size: 12px;
  line-height: 1.2;
  white-space: nowrap;
}

.route-badge-muted {
  border-color: var(--border-color-global);
  background: var(--bg-surface-soft);
  color: var(--text-color-secondary);
}

.route-badge-warning {
  border-color: color-mix(in srgb, var(--status-warning-text) 34%, transparent);
  background: var(--status-warning-bg);
  color: var(--status-warning-text);
}

.badge-disabled {
  border-color: var(--border-color-global);
  background: var(--bg-page);
  color: var(--text-color-disabled);
}

.remote-name {
  margin-top: 4px;
  color: var(--text-color-secondary);
  font-size: 12px;
}

.availability-trigger {
  min-width: 96px;
  white-space: nowrap;
}

.availability-select {
  width: 160px;
}

.route-actions {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
}

.availability-popover {
  grid-column: 3 / -1;
  display: none;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border: 1px solid var(--border-color-soft);
  border-radius: 8px;
  background: var(--bg-page);
  cursor: default;
}

.availability-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-left: auto;
}

.availability-popover.open {
  display: flex;
}

.time-range-fields {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.time-range-fields :deep(.n-time-picker) {
  width: 116px;
}

.availability-hint {
  margin-left: auto;
  color: var(--text-color-secondary);
  font-size: 12px;
  white-space: nowrap;
}

@media (max-width: 991.98px) {
  .route-layout {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 767.98px) {
  .route-add-box {
    grid-template-columns: 1fr;
    align-items: stretch;
  }

  .route-add-box > * {
    width: 100%;
  }

  .route-item {
    grid-template-columns: auto auto minmax(0, 1fr);
  }

  .availability-select,
  .route-actions {
    grid-column: 3;
    justify-content: flex-start;
  }

  .availability-popover {
    grid-column: 1 / -1;
    flex-wrap: wrap;
  }

  .availability-hint {
    width: 100%;
    margin-left: 0;
  }
}
</style>
