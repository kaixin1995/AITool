<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { NCard, NButton, NSpace, NSelect, NTag, NEmpty, NPopconfirm, NTimePicker, useMessage, type SelectOption } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/routes'
import type { RouteModelItem, RouteRuleItem, DiscoveredSite } from '@/api/routes'

const message = useMessage()
const models = ref<RouteModelItem[]>([])
const selectedModel = ref<string | null>(null)
const rules = ref<RouteRuleItem[]>([])
const discoveredSites = ref<DiscoveredSite[]>([])
const loading = ref(false)
const saving = ref(false)

// 可用性模式选项（对齐原 Razor 页的 availabilityMode）
const availabilityOptions: SelectOption[] = [
  { label: '全天可用', value: 'AllDay' },
  { label: '仅指定时间可用', value: 'TimeRangeOnly' },
  { label: '指定时间不可用', value: 'TimeRangeExcluded' }
]

// 解析/序列化 timeRangesJson（[{start:"HH:mm", end:"HH:mm"}]）
interface TimeRange { start: string; end: string }
function parseTimeRanges(json: string): TimeRange[] {
  if (!json) return [{ start: '09:00', end: '18:00' }]
  try { const arr = JSON.parse(json); return Array.isArray(arr) && arr.length > 0 ? arr : [{ start: '09:00', end: '18:00' }] }
  catch { return [{ start: '09:00', end: '18:00' }] }
}
function serializeTimeRanges(ranges: TimeRange[]): string {
  return JSON.stringify(ranges)
}

// 每条规则的编辑态时间范围（按 rule key 索引）
const editingRanges = ref<Record<string, TimeRange[]>>({})

function handleAvailabilityChange(rule: RouteRuleItem, mode: string): void {
  rule.availabilityMode = mode
  if (mode === 'AllDay') {
    rule.timeRangesJson = ''
  } else {
    // 初始化默认时间段
    const key = rule.ruleId || `${rule.siteId}-${rule.siteModelName}`
    if (!editingRanges.value[key]) {
      editingRanges.value[key] = parseTimeRanges(rule.timeRangesJson)
    }
    rule.timeRangesJson = serializeTimeRanges(editingRanges.value[key])
  }
}

function getRuleKey(rule: RouteRuleItem): string {
  return rule.ruleId || `${rule.siteId}-${rule.siteModelName}`
}

function getRanges(rule: RouteRuleItem): TimeRange[] {
  const key = getRuleKey(rule)
  if (!editingRanges.value[key]) editingRanges.value[key] = parseTimeRanges(rule.timeRangesJson)
  return editingRanges.value[key]
}

function addTimeRange(rule: RouteRuleItem): void {
  const key = getRuleKey(rule)
  if (!editingRanges.value[key]) editingRanges.value[key] = parseTimeRanges(rule.timeRangesJson)
  editingRanges.value[key].push({ start: '09:00', end: '18:00' })
  rule.timeRangesJson = serializeTimeRanges(editingRanges.value[key])
}

function removeTimeRange(rule: RouteRuleItem, idx: number): void {
  const key = getRuleKey(rule)
  if (!editingRanges.value[key]) return
  editingRanges.value[key].splice(idx, 1)
  rule.timeRangesJson = serializeTimeRanges(editingRanges.value[key])
}

// "HH:mm" ↔ 时间戳（NTimePicker 用时间戳）
function timeToTs(hhmm: string): number {
  const [h, m] = hhmm.split(':').map(Number)
  const d = new Date(); d.setHours(h || 0, m || 0, 0, 0)
  return d.getTime()
}
function tsToTime(ts: number): string {
  const d = new Date(ts)
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const modelOptions = computed(() => models.value.map((m) => ({ label: `${m.displayName} (${m.siteCount}站点)`, value: m.modelName })))

async function loadModels(): Promise<void> {
  models.value = await api.getRouteModels()
  if (models.value.length > 0 && !selectedModel.value) {
    selectedModel.value = models.value[0].modelName
  }
}

watch(selectedModel, async (name) => {
  if (!name) { rules.value = []; discoveredSites.value = []; return }
  loading.value = true
  try {
    const [r, d] = await Promise.all([api.getRouteRules(name), api.discoverSites(name)])
    rules.value = r
    discoveredSites.value = d
  } finally { loading.value = false }
})

function moveRule(index: number, direction: -1 | 1): void {
  const target = index + direction
  if (target < 0 || target >= rules.value.length) return
  const arr = rules.value
  ;[arr[index], arr[target]] = [arr[target], arr[index]]
}

async function addFromDiscovered(site: DiscoveredSite): Promise<void> {
  if (rules.value.some((r) => r.siteId === site.siteId && r.siteModelName === site.remoteModelName)) {
    message.warning('该站点已在列表中'); return
  }
  rules.value.push({
    ruleId: '', siteId: site.siteId, siteName: site.siteName, siteEnabled: site.siteEnabled,
    upstreamModelName: selectedModel.value ?? '', siteModelName: site.remoteModelName,
    priority: rules.value.length, modelPriority: 0, instancePriority: 0, isEnabled: true,
    availabilityMode: 'AllDay', timeRangesJson: ''
  })
}

async function handleSave(): Promise<void> {
  if (!selectedModel.value) return
  saving.value = true
  try {
    // 保留 availabilityMode / timeRangesJson，避免保存时把时间规则重置为全天（数据丢失）
    await api.saveRouteRules(selectedModel.value, rules.value.map((r) => ({
      siteId: r.siteId, siteModelName: r.siteModelName, upstreamModelName: r.upstreamModelName, isEnabled: r.isEnabled,
      availabilityMode: r.availabilityMode, timeRangesJson: r.timeRangesJson
    })))
    message.success('路由规则已保存')
    if (selectedModel.value) rules.value = await api.getRouteRules(selectedModel.value)
  } finally { saving.value = false }
}

async function handleToggleRule(rule: RouteRuleItem): Promise<void> {
  if (!rule.ruleId) { rule.isEnabled = !rule.isEnabled; return }
  await api.toggleRouteRule(rule.ruleId)
  rule.isEnabled = !rule.isEnabled
}

async function handleDeleteRule(rule: RouteRuleItem, index: number): Promise<void> {
  if (rule.ruleId) {
    await api.deleteRouteRule(rule.ruleId)
  }
  rules.value.splice(index, 1)
  message.success('已删除')
}

onMounted(loadModels)
</script>

<template>
  <div class="page-container">
    <PageHeader title="路由规则管理" subtitle="主入口绑定有序候选实例队列，请求时按顺序失败切换、成功即停止">
      <template #actions>
        <NSelect v-model:value="selectedModel" :options="modelOptions" placeholder="选择模型" style="width: 280px" />
        <NButton type="primary" :loading="saving" :disabled="!selectedModel" @click="handleSave">保存顺序</NButton>
      </template>
    </PageHeader>
    <NCard>
      <div v-if="!selectedModel">
        <NEmpty description="请选择一个模型" />
      </div>
      <div v-else>
        <h4 style="margin: 0 0 12px; font-size: 14px; color: var(--text-color-secondary)">已配置路由（按优先级排序，越靠前越优先）</h4>
        <NEmpty v-if="rules.length === 0" description="暂无路由规则，从下方发现站点添加" size="small" />
        <div v-for="(rule, idx) in rules" :key="rule.ruleId || `${rule.siteId}-${rule.siteModelName}`" class="rule-row">
          <div style="flex: 1">
            <NSpace align="center" :size="8">
              <NTag size="small" :bordered="false" round>{{ idx + 1 }}</NTag>
              <span style="font-weight: 600">{{ rule.siteName }}</span>
              <NTag size="small" :type="rule.siteEnabled ? 'success' : 'warning'" :bordered="false">{{ rule.siteModelName }}</NTag>
              <NTag v-if="!rule.isEnabled" size="tiny" type="default" :bordered="false">已禁用</NTag>
              <NSelect
                :value="rule.availabilityMode || 'AllDay'"
                :options="availabilityOptions"
                size="tiny"
                style="width: 140px"
                @update:value="(v: string) => handleAvailabilityChange(rule, v)"
              />
            </NSpace>
            <!-- 时间段编辑器（仅非全天模式显示） -->
            <div v-if="rule.availabilityMode && rule.availabilityMode !== 'AllDay'" class="time-ranges">
              <div v-for="(range, rIdx) in getRanges(rule)" :key="rIdx" class="time-range-row">
                <NTimePicker :value="timeToTs(range.start)" format="HH:mm" size="small" style="width: 96px" @update:value="(v: number | null) => { if (v != null) { range.start = tsToTime(v); rule.timeRangesJson = serializeTimeRanges(getRanges(rule)) } }" />
                <span style="font-size: 12px">至</span>
                <NTimePicker :value="timeToTs(range.end)" format="HH:mm" size="small" style="width: 96px" @update:value="(v: number | null) => { if (v != null) { range.end = tsToTime(v); rule.timeRangesJson = serializeTimeRanges(getRanges(rule)) } }" />
                <NButton size="tiny" quaternary type="error" @click="removeTimeRange(rule, rIdx)">×</NButton>
              </div>
              <NButton size="tiny" quaternary @click="addTimeRange(rule)">+ 添加时段</NButton>
            </div>
          </div>
          <NSpace :size="4">
            <NButton size="tiny" quaternary :disabled="idx === 0" @click="moveRule(idx, -1)">↑</NButton>
            <NButton size="tiny" quaternary :disabled="idx === rules.length - 1" @click="moveRule(idx, 1)">↓</NButton>
            <NButton size="tiny" quaternary @click="handleToggleRule(rule)">{{ rule.isEnabled ? '禁用' : '启用' }}</NButton>
            <NPopconfirm @positive-click="handleDeleteRule(rule, idx)">
              <template #trigger><NButton size="tiny" quaternary type="error">删除</NButton></template>
              移除该路由？
            </NPopconfirm>
          </NSpace>
        </div>

        <h4 style="margin: 24px 0 12px; font-size: 14px; color: var(--text-color-secondary)">可发现的站点</h4>
        <NEmpty v-if="discoveredSites.length === 0" description="没有更多可用站点" size="small" />
        <div v-for="site in discoveredSites" :key="`${site.siteId}-${site.remoteModelName}`" class="discover-row">
          <NSpace align="center" :size="8" style="flex: 1">
            <span>{{ site.siteName }}</span>
            <NTag size="small" :bordered="false">{{ site.remoteModelName }}</NTag>
            <NTag v-if="!site.siteEnabled" size="tiny" type="warning" :bordered="false">站点已禁用</NTag>
          </NSpace>
          <NButton size="small" quaternary type="primary" @click="addFromDiscovered(site)">添加</NButton>
        </div>
      </div>
    </NCard>
  </div>
</template>

<style scoped>
.rule-row, .discover-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 12px;
  border-radius: 6px;
  margin-bottom: 4px;
}
.rule-row { background: rgba(108, 158, 255, 0.06); flex-direction: column; align-items: stretch; }
.rule-row > div:first-child { display: flex; align-items: center; }
.rule-row:hover { background: rgba(108, 158, 255, 0.12); }
.discover-row:hover { background: rgba(0, 0, 0, 0.03); }
[data-theme='dark'] .discover-row:hover { background: rgba(255, 255, 255, 0.05); }
.time-ranges { margin-top: 6px; padding-left: 28px; }
.time-range-row { display: flex; align-items: center; gap: 6px; margin-bottom: 4px; }
</style>
