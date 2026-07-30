<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  NButton,
  NCard,
  NEmpty,
  NInput,
  NPopconfirm,
  NSelect,
  NSpace,
  NTag,
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

const dialog = useDialog()
const message = useMessage()
const entries = ref<RouteEntry[]>([])
const selectedEntryName = ref<string | null>(null)
const rules = ref<RouteRuleItem[]>([])
const siteInstances = ref<SiteInstanceItem[]>([])
const entryNameInput = ref('')
const candidateSearch = ref('')
const loading = ref(false)
const refreshingPool = ref(false)
const creatingEntry = ref(false)
const saving = ref(false)
const dirty = ref(false)
const editingRanges = ref<Record<string, TimeRange[]>>({})
const resolveRuleKey = createRuleKeyResolver()
let latestLoadToken = 0

const availabilityOptions = api.routeAvailabilityOptions
const selectedEntry = computed(
  () => entries.value.find((entry) => entry.entryName === selectedEntryName.value) ?? null
)
const filteredInstances = computed(
  () => filterSiteInstances(siteInstances.value, candidateSearch.value)
)
const canEdit = computed(
  () => Boolean(selectedEntryName.value) && !loading.value && !saving.value
)

function parseTimeRanges(json: string): TimeRange[] {
  if (!json) return [{ start: '09:00', end: '18:00' }]

  try {
    const ranges = JSON.parse(json)
    return Array.isArray(ranges) && ranges.length > 0
      ? ranges
      : [{ start: '09:00', end: '18:00' }]
  } catch {
    return [{ start: '09:00', end: '18:00' }]
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

function markDirty(): void {
  dirty.value = true
}

function handleAvailabilityChange(
  rule: RouteRuleItem,
  index: number,
  mode: RouteAvailabilityMode
): void {
  rule.availabilityMode = mode

  if (mode === 'AllDay') {
    rule.timeRangesJson = ''
  } else {
    rule.timeRangesJson = serializeTimeRanges(getRanges(rule))
  }

  markDirty()
}

function addTimeRange(rule: RouteRuleItem, index: number): void {
  getRanges(rule).push({ start: '09:00', end: '18:00' })
  rule.timeRangesJson = serializeTimeRanges(getRanges(rule))
  markDirty()
}

function removeTimeRange(
  rule: RouteRuleItem,
  index: number,
  rangeIndex: number
): void {
  const ranges = getRanges(rule)
  ranges.splice(rangeIndex, 1)
  rule.timeRangesJson = serializeTimeRanges(ranges)
  markDirty()
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
  markDirty()
}

function moveRule(index: number, direction: -1 | 1): void {
  const moved = moveCandidate(rules.value, index, direction)
  if (moved !== rules.value) {
    rules.value = moved
    markDirty()
  }
}

function toggleRule(rule: RouteRuleItem): void {
  rule.isEnabled = !rule.isEnabled
  markDirty()
}

function removeRule(index: number): void {
  rules.value = rules.value.filter((_, ruleIndex) => ruleIndex !== index)
  editingRanges.value = {}
  markDirty()
}

async function handleSave(): Promise<void> {
  const entryName = selectedEntryName.value
  if (!entryName || saving.value || !dirty.value) return

  saving.value = true
  try {
    const response = await api.saveRouteRules(entryName, buildSaveRules(rules.value))
    await Promise.all([
      loadEntries(entryName),
      refreshSiteInstances()
    ])
    await loadSelectedEntry(entryName)
    message.success(response.message || '路由规则已保存')
  } finally {
    saving.value = false
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
      subtitle="主入口绑定有序候选实例队列，请求按顺序失败切换、成功即停止"
    >
      <template #actions>
        <NTag v-if="dirty" type="warning" :bordered="false">有未保存修改</NTag>
        <NButton
          type="primary"
          :loading="saving"
          :disabled="!selectedEntryName || !dirty"
          @click="handleSave"
        >
          保存候选队列
        </NButton>
      </template>
    </PageHeader>

    <div class="route-layout">
      <NCard class="entry-panel" :bordered="false">
        <template #header>
          <div class="panel-heading">
            <span>主入口</span>
            <small>管理对外暴露的本地路由入口</small>
          </div>
        </template>

        <div class="entry-create">
          <label for="route-entry-name">新建主入口</label>
          <div class="entry-create-row">
            <NInput
              id="route-entry-name"
              v-model:value="entryNameInput"
              placeholder="例如 auto"
              :disabled="creatingEntry || saving"
              @keyup.enter="createEntry"
            />
            <NButton
              type="primary"
              :loading="creatingEntry"
              :disabled="saving"
              @click="createEntry"
            >
              创建
            </NButton>
          </div>
        </div>

        <NEmpty v-if="entries.length === 0" description="暂无主入口，请先创建" size="small" />
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
            <NTag size="small" :bordered="false">{{ entry.candidateCount }}</NTag>
          </button>
        </div>
      </NCard>

      <NCard class="editor-panel" :bordered="false">
        <template #header>
          <div class="editor-heading">
            <div class="panel-heading">
              <span>候选实例队列</span>
              <small v-if="selectedEntry">当前主入口：{{ selectedEntry.entryName }}</small>
              <small v-else>请选择或创建一个主入口</small>
            </div>
            <NPopconfirm
              v-if="selectedEntryName"
              positive-text="删除"
              negative-text="取消"
              @positive-click="deleteCurrentEntry"
            >
              <template #trigger>
                <NButton
                  size="small"
                  type="error"
                  secondary
                  :disabled="saving || creatingEntry"
                >
                  删除当前主入口
                </NButton>
              </template>
              {{ getDeleteEntryConfirmation(dirty) }}
            </NPopconfirm>
          </div>
        </template>

        <NEmpty v-if="!selectedEntryName" description="请先从左侧选择或创建主入口" />
        <template v-else>
          <div class="candidate-toolbar">
            <NInput
              v-model:value="candidateSearch"
              clearable
              placeholder="搜索站点或模型"
              :disabled="saving"
            />
            <NButton :loading="refreshingPool" :disabled="saving" @click="refreshSiteInstances">刷新实例池</NButton>
          </div>

          <div class="instance-pool" aria-label="可添加候选实例">
            <NEmpty v-if="filteredInstances.length === 0" description="没有匹配的候选实例" size="small" />
            <div
              v-for="instance in filteredInstances"
              :key="`${instance.siteId}-${instance.siteModelName}`"
              class="instance-row"
            >
              <div class="instance-name">
                <strong>{{ instance.siteName }}</strong>
                <NTag size="small" :bordered="false">{{ instance.siteModelName }}</NTag>
                <NTag size="tiny" :bordered="false" type="info">{{ instance.protocolType }}</NTag>
              </div>
              <NButton size="small" type="primary" secondary :disabled="!canEdit" @click="addCandidate(instance)">
                添加
              </NButton>
            </div>
          </div>

          <div class="rules-header">
            <div>
              <h3>已配置候选</h3>
              <p>越靠前优先级越高；修改后统一保存，避免覆盖当前编辑状态。</p>
            </div>
            <NTag :bordered="false">{{ rules.length }} 个候选</NTag>
          </div>

          <NEmpty v-if="!loading && rules.length === 0" description="暂无候选实例，可从上方实例池添加" size="small" />
          <div v-else class="rule-list">
            <div
              v-for="(rule, index) in rules"
              :key="resolveRuleKey(rule)"
              class="rule-row"
            >
              <div class="rule-main">
                <NTag size="small" round :bordered="false">{{ index + 1 }}</NTag>
                <div class="rule-identity">
                  <strong>{{ rule.siteName }}</strong>
                  <span>{{ rule.siteModelName }} → {{ rule.upstreamModelName }}</span>
                </div>
                <NTag v-if="!rule.siteEnabled" size="tiny" type="warning" :bordered="false">站点已禁用</NTag>
                <NTag v-if="!rule.isEnabled" size="tiny" type="default" :bordered="false">规则已禁用</NTag>
                <NSelect
                  :value="rule.availabilityMode"
                  :options="availabilityOptions"
                  size="small"
                  :disabled="!canEdit"
                  class="availability-select"
                  @update:value="(value: RouteAvailabilityMode) => handleAvailabilityChange(rule, index, value)"
                />
              </div>
              <NSpace size="small" class="rule-actions">
                <NButton size="tiny" quaternary :disabled="!canEdit || index === 0" @click="moveRule(index, -1)">上移</NButton>
                <NButton size="tiny" quaternary :disabled="!canEdit || index === rules.length - 1" @click="moveRule(index, 1)">下移</NButton>
                <NButton size="tiny" quaternary :disabled="!canEdit" @click="toggleRule(rule)">
                  {{ rule.isEnabled ? '禁用' : '启用' }}
                </NButton>
                <NPopconfirm @positive-click="removeRule(index)">
                  <template #trigger>
                    <NButton size="tiny" quaternary type="error" :disabled="!canEdit">删除</NButton>
                  </template>
                  从候选队列移除此规则？
                </NPopconfirm>
              </NSpace>
              <div v-if="rule.availabilityMode !== 'AllDay'" class="time-ranges">
                <div
                  v-for="(range, rangeIndex) in getRanges(rule)"
                  :key="rangeIndex"
                  class="time-range-row"
                >
                  <NTimePicker
                    :value="timeToTimestamp(range.start)"
                    format="HH:mm"
                    size="small"
                    :disabled="!canEdit"
                    @update:value="(value: number | null) => { if (value !== null) { range.start = timestampToTime(value); rule.timeRangesJson = serializeTimeRanges(getRanges(rule)); markDirty() } }"
                  />
                  <span>至</span>
                  <NTimePicker
                    :value="timeToTimestamp(range.end)"
                    format="HH:mm"
                    size="small"
                    :disabled="!canEdit"
                    @update:value="(value: number | null) => { if (value !== null) { range.end = timestampToTime(value); rule.timeRangesJson = serializeTimeRanges(getRanges(rule)); markDirty() } }"
                  />
                  <NButton size="tiny" quaternary type="error" :disabled="!canEdit" @click="removeTimeRange(rule, index, rangeIndex)">移除</NButton>
                </div>
                <NButton size="tiny" quaternary :disabled="!canEdit" @click="addTimeRange(rule, index)">添加时段</NButton>
              </div>
            </div>
          </div>
        </template>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.route-layout {
  display: grid;
  grid-template-columns: minmax(280px, 320px) minmax(0, 1fr);
  gap: 16px;
  align-items: start;
}

.entry-panel,
.editor-panel {
  min-width: 0;
}

.panel-heading {
  display: grid;
  gap: 3px;
}

.panel-heading > span {
  color: var(--text-color);
  font-size: 15px;
  font-weight: 650;
}

.panel-heading small,
.rules-header p {
  color: var(--text-color-secondary);
  font-size: 12px;
  line-height: 1.45;
}

.entry-create {
  display: grid;
  gap: 7px;
  margin-bottom: 14px;
}

.entry-create label {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.entry-create-row,
.candidate-toolbar,
.editor-heading,
.rules-header,
.instance-row,
.rule-row,
.rule-main,
.rule-actions,
.time-range-row {
  display: flex;
  align-items: center;
}

.entry-create-row {
  gap: 8px;
}

.entry-create-row :deep(.n-input) {
  min-width: 0;
}

.entry-list {
  display: grid;
  gap: 4px;
}

.entry-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  gap: 10px;
  padding: 9px 10px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-color);
  cursor: pointer;
  text-align: left;
}

.entry-item:hover {
  background: rgba(108, 158, 255, 0.08);
}

.entry-item.active {
  background: rgba(108, 158, 255, 0.16);
  color: var(--primary-color);
}

.entry-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.editor-heading,
.rules-header,
.instance-row,
.rule-row {
  justify-content: space-between;
  gap: 12px;
}

.candidate-toolbar {
  gap: 8px;
  margin-bottom: 10px;
}

.candidate-toolbar :deep(.n-input) {
  flex: 1;
  min-width: 0;
}

.instance-pool {
  max-height: 230px;
  overflow-y: auto;
  border: 1px solid var(--border-color);
  border-radius: 8px;
  margin-bottom: 18px;
}

.instance-row {
  min-height: 42px;
  padding: 7px 10px;
}

.instance-row + .instance-row {
  border-top: 1px solid var(--border-color);
}

.instance-name,
.rule-identity {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 7px;
}

.instance-name strong,
.rule-identity strong {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rules-header {
  margin: 4px 0 10px;
}

.rules-header h3 {
  margin: 0 0 3px;
  color: var(--text-color);
  font-size: 14px;
}

.rules-header p {
  margin: 0;
}

.rule-list {
  display: grid;
  gap: 6px;
}

.rule-row {
  flex-wrap: wrap;
  min-width: 0;
  padding: 9px 10px;
  border-radius: 8px;
  background: rgba(108, 158, 255, 0.06);
}

.rule-main {
  flex: 1;
  min-width: 0;
  gap: 8px;
}

.rule-identity {
  min-width: 120px;
  flex: 1;
}

.rule-identity span {
  overflow: hidden;
  color: var(--text-color-secondary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.availability-select {
  width: 142px;
  flex: 0 0 auto;
}

.rule-actions {
  flex: 0 0 auto;
  flex-wrap: nowrap;
}

.time-ranges {
  display: grid;
  width: 100%;
  gap: 6px;
  padding: 8px 0 0 34px;
  border-top: 1px dashed var(--border-color);
}

.time-range-row {
  gap: 6px;
}

.time-range-row :deep(.n-time-picker) {
  width: 100px;
}

@media (max-width: 991.98px) {
  .route-layout {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 640px) {
  .candidate-toolbar,
  .editor-heading,
  .rule-main,
  .rule-row,
  .time-range-row {
    align-items: stretch;
    flex-direction: column;
  }

  .candidate-toolbar > :last-child,
  .editor-heading > :last-child,
  .availability-select {
    width: 100%;
  }

  .instance-name {
    flex-wrap: wrap;
  }

  .rule-actions {
    width: 100%;
    justify-content: flex-end;
  }

  .time-ranges {
    padding-left: 0;
  }
}
</style>
