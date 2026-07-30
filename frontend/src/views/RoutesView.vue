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
  isLatestRouteLoad
} from './routes/routeEditorState'

interface TimeRange {
  start: string
  end: string
}

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
const editingRanges = ref<Record<string, TimeRange[]>>({})
const openAvailabilityKey = ref<string | null>(null)
const draggingRuleIndex = ref<number | null>(null)
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
  label: `${instance.siteName} / ${instance.siteModelName} / ${instance.protocolType}`,
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

function getRanges(rule: RouteRuleItem): TimeRange[] {
  const key = resolveRuleKey(rule)
  if (!editingRanges.value[key]) {
    editingRanges.value[key] = parseTimeRanges(rule.timeRangesJson)
  }
  return editingRanges.value[key]
}

function getAvailabilitySummary(rule: RouteRuleItem): { text: string; className: string } {
  const ranges = rule.availabilityMode === 'AllDay' ? [] : getRanges(rule)
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
  openAvailabilityKey.value = openAvailabilityKey.value === key ? null : key
}

function markDirty(): void {
  dirty.value = true
}

function saveAfterChange(): void {
  markDirty()
  void handleSave()
}

function handleAvailabilityChange(
  rule: RouteRuleItem,
  index: number,
  mode: RouteAvailabilityMode
): void {
  rule.availabilityMode = mode
  openAvailabilityKey.value = resolveRuleKey(rule)

  if (mode === 'AllDay') {
    rule.timeRangesJson = ''
  } else {
    rule.timeRangesJson = serializeTimeRanges(getRanges(rule))
  }

  saveAfterChange()
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
    editingRanges.value = {}
    loading.value = false
    return
  }

  loading.value = true
  try {
    const loadedRules = await api.getRouteRules(entryName)
    if (!isLatestRouteLoad(token, latestLoadToken)) return

    rules.value = loadedRules
    editingRanges.value = {}
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
  editingRanges.value = {}
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

function handleRuleDragStart(index: number): void {
  draggingRuleIndex.value = index
}

function handleRuleDragOver(index: number): void {
  const from = draggingRuleIndex.value
  if (from === null || from === index) return

  const nextRules = [...rules.value]
  const [draggedRule] = nextRules.splice(from, 1)
  nextRules.splice(index, 0, draggedRule)
  rules.value = nextRules
  draggingRuleIndex.value = index
}

function handleRuleDragEnd(): void {
  if (draggingRuleIndex.value !== null) {
    draggingRuleIndex.value = null
    saveAfterChange()
  }
}

function removeRule(index: number): void {
  rules.value = rules.value.filter((_, ruleIndex) => ruleIndex !== index)
  editingRanges.value = {}
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
  <div class="page-container">
    <PageHeader
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
            <span class="entry-name">{{ entry.entryName }}</span>
            <span class="entry-count">{{ entry.candidateCount }} 个候选</span>
          </button>
        </div>
      </section>

      <section class="route-panel route-panel-right">
        <div class="route-panel-header route-panel-header-stack">
          <div>
            <h5 class="route-panel-title">候选实例队列</h5>
            <div class="route-panel-subtitle">
              <template v-if="selectedEntry">当前主入口：{{ selectedEntry.entryName }}</template>
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
                  :class="{ dragging: draggingRuleIndex === index }"
                  draggable="true"
                  @dragstart="handleRuleDragStart(index)"
                  @dragover.prevent="handleRuleDragOver(index)"
                  @dragend="handleRuleDragEnd"
                >
                  <span class="drag-handle" aria-hidden="true">⠿</span>
                  <span class="priority-num">{{ index + 1 }}</span>
                  <div class="site-info">
                    <div class="site-name-row">
                      <span class="site-name" :class="{ 'site-disabled': !rule.siteEnabled }">{{ rule.siteName }}</span>
                      <span v-if="!rule.siteEnabled" class="route-badge badge-disabled">站点已禁用</span>
                      <span :class="['route-badge', getAvailabilitySummary(rule).className]">{{ getAvailabilitySummary(rule).text }}</span>
                      <span v-if="!rule.isEnabled" class="route-badge route-badge-muted">规则已禁用</span>
                    </div>
                    <div class="remote-name">上游模型：{{ rule.upstreamModelName }} · 站点实例：{{ rule.siteModelName }}</div>
                  </div>
                  <NButton size="small" secondary class="availability-trigger" :disabled="!canEdit" @click="toggleAvailabilityEditor(rule)">时间规则</NButton>
                  <div class="route-actions">
                    <NPopconfirm @positive-click="removeRule(index)">
                      <template #trigger>
                        <NButton size="small" secondary type="error" :disabled="!canEdit">移除</NButton>
                      </template>
                      从候选队列移除此规则？
                    </NPopconfirm>
                  </div>
                  <div :class="['availability-popover', { open: isAvailabilityOpen(rule) }]">
                    <NSelect
                      :value="rule.availabilityMode"
                      :options="availabilityOptions"
                      size="small"
                      :disabled="!canEdit"
                      class="availability-select"
                      @update:value="(value: RouteAvailabilityMode) => handleAvailabilityChange(rule, index, value)"
                    />
                    <span v-show="rule.availabilityMode !== 'AllDay'" class="time-range-fields">
                      <NTimePicker
                        :value="timeToTimestamp(getRanges(rule)[0]?.start ?? '00:00')"
                        format="HH:mm"
                        size="small"
                        :disabled="!canEdit"
                        @update:value="(value: number | null) => { if (value !== null) { getRanges(rule)[0].start = timestampToTime(value); rule.timeRangesJson = serializeTimeRanges([getRanges(rule)[0]]); saveAfterChange() } }"
                      />
                      <span>至</span>
                      <NTimePicker
                        :value="timeToTimestamp(getRanges(rule)[0]?.end ?? '23:59')"
                        format="HH:mm"
                        size="small"
                        :disabled="!canEdit"
                        @update:value="(value: number | null) => { if (value !== null) { getRanges(rule)[0].end = timestampToTime(value); rule.timeRangesJson = serializeTimeRanges([getRanges(rule)[0]]); saveAfterChange() } }"
                      />
                    </span>
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
  border-color: #b6d4fe;
  background: #f8fbff;
}

.entry-item.active {
  border-color: #0d6efd;
  background: #e8f0fe;
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
  grid-template-columns: auto auto minmax(0, 1fr) auto auto;
  align-items: center;
  gap: 12px;
  padding: 14px 16px;
  border-bottom: 1px solid var(--border-color-global);
  cursor: grab;
  user-select: none;
  transition: background 0.15s;
}

.route-item:hover {
  background: var(--bg-page);
}

.route-item.dragging {
  opacity: 0.4;
  background: #e8f0fe;
}

.drag-handle {
  flex-shrink: 0;
  color: var(--text-color-secondary);
  font-size: 18px;
  cursor: grab;
}

.priority-num {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  background: #e8f0fe;
  color: #0d6efd;
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
  color: #9ca3af;
}

.route-badge {
  display: inline-flex;
  align-items: center;
  min-height: 22px;
  padding: 2px 8px;
  border: 1px solid #dbeafe;
  border-radius: 999px;
  background: #eff6ff;
  color: #1d4ed8;
  font-size: 12px;
  line-height: 1.2;
  white-space: nowrap;
}

.route-badge-muted {
  border-color: var(--border-color-global);
  background: #f8f9fa;
  color: var(--text-color-secondary);
}

.route-badge-warning {
  border-color: #fed7aa;
  background: #fff7ed;
  color: #c2410c;
}

.badge-disabled {
  border-color: #d1d5db;
  background: var(--bg-page);
  color: #9ca3af;
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
  border: 1px solid #dbeafe;
  border-radius: 8px;
  background: var(--bg-page);
  cursor: default;
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
