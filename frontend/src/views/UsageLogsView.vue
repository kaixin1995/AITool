<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import {
  NButton,
  NCard,
  NDataTable,
  NDatePicker,
  NDrawer,
  NDrawerContent,
  NEmpty,
  NGi,
  NGrid,
  NSelect,
  NSpace,
  NStatistic,
  NSwitch,
  NTag,
  useMessage,
  type DataTableColumns,
  type SelectOption
} from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/usageLogs'
import type { UsageLogItem, UsageLogRequestDetail, UsageLogSummary } from '@/api/usageLogs'

const message = useMessage()
const loading = ref(false)
const detailLoading = ref(false)
const filtersExpanded = ref(false)
const autoRefresh = ref(true)
const items = ref<UsageLogItem[]>([])
const totalCount = ref(0)
const filters = ref<api.UsageLogFilters>({ sites: [], accessKeys: [] })
const summary = ref<UsageLogSummary>({ totalRequests: 0, failedRequests: 0, successRate: 0, totalTokens: 0, maxDurationMs: 0 })
const detailVisible = ref(false)
const requestDetail = ref<UsageLogRequestDetail | null>(null)
let refreshTimer: number | undefined

const query = reactive({
  page: 1,
  pageSize: 20,
  rangeType: 'day',
  siteId: null as string | null,
  accessKeyId: null as string | null,
  source: null as string | null,
  status: null as string | null,
  startTime: null as number | null,
  endTime: null as number | null,
  modelKeyword: ''
})

const siteOptions = computed(() => filters.value.sites.map((s) => ({ label: s.name, value: s.id })))
const keyOptions = computed(() => filters.value.accessKeys.map((k) => ({ label: k.name, value: k.id })))
const sourceOptions: SelectOption[] = [
  { label: '全部', value: '' },
  { label: '代理', value: 'proxy' },
  { label: '对话测试', value: 'chat' },
  { label: 'Claude Code', value: 'claude-code' },
  { label: 'Codex', value: 'codex' },
  { label: 'OpenCode', value: 'open-code' },
  { label: 'ZCode', value: 'zcode' },
  { label: '手动检测', value: 'detection-manual' },
  { label: '检测任务', value: 'detection-task' }
]
const statusOptions: SelectOption[] = [
  { label: '全部', value: '' },
  { label: '成功', value: 'success' },
  { label: '失败', value: 'fail' }
]
const rangeOptions: SelectOption[] = [
  { label: '按天', value: 'day' },
  { label: '按周', value: 'week' },
  { label: '按月', value: 'month' },
  { label: '全部', value: 'all' },
  { label: '指定时间范围', value: 'custom' }
]

function formatNumber(value: number | null | undefined): string {
  const number = Number(value ?? 0)
  return Number.isFinite(number) ? number.toLocaleString('zh-CN') : '-'
}

function formatMetricNumber(value: number | null | undefined): string {
  let number = Number(value ?? 0)
  if (!Number.isFinite(number)) return '-'
  const units = ['', 'K', 'M', 'B', 'T', 'P', 'E']
  let unitIndex = 0
  while (Math.abs(number) >= 1000 && unitIndex < units.length - 1) {
    number /= 1000
    unitIndex++
  }
  if (unitIndex === 0) return formatNumber(number)
  const formatted = (number >= 100 ? number.toFixed(0) : number >= 10 ? number.toFixed(1) : number.toFixed(2))
    .replace(/0+$/, '')
    .replace(/\.$/, '')
  return `${formatted} ${units[unitIndex]}`
}

function formatPercent(value: number | null | undefined): string {
  let number = Number(value ?? 0)
  if (!Number.isFinite(number)) return '-'
  if (number <= 1) number *= 100
  return `${number.toFixed(1)}%`
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '-'
  return new Date(value).toLocaleString('zh-CN')
}

function formatDuration(value: number | null | undefined): string {
  const number = Number(value ?? 0)
  if (!Number.isFinite(number) || number <= 0) return '-'
  if (number >= 1000) {
    const seconds = number / 1000
    return `${Math.abs(seconds - Math.round(seconds)) < 0.05 ? Math.round(seconds) : seconds.toFixed(1)}s`
  }
  return `${Math.round(number)}ms`
}

function formatProtocolType(protocolType: string): string {
  return protocolType || '-'
}

function formatForwardingMode(forwardingMode: string): string {
  const normalized = forwardingMode.trim().toLowerCase()
  if (normalized === 'direct') return '直接透传'
  if (normalized === 'bridge') return '兼容中转'
  return '-'
}

function statusTag(row: UsageLogItem) {
  const success = row.status === 'success' || row.status === 'ok'
  const label = row.fallbackTriggered && success ? '回退后成功' : row.isStreamInterrupted && !success ? '流中断' : success ? '成功' : '失败'
  return h(NTag, { size: 'small', type: success ? 'success' : 'error', bordered: false }, () => label)
}

function sourceTag(source: string) {
  const normalized = source?.trim().toLowerCase()
  const labels: Record<string, string> = {
    proxy: '代理',
    chat: '对话测试',
    'claude-code': 'Claude Code',
    codex: 'Codex',
    'open-code': 'OpenCode',
    zcode: 'ZCode',
    'detection-manual': '手动检测',
    'detection-task': '检测任务'
  }
  const type = normalized === 'chat' ? 'info' : normalized?.startsWith('detection') ? 'warning' : normalized === 'proxy' ? 'default' : 'success'
  return h(NTag, { size: 'small', type, bordered: false }, () => labels[normalized] ?? source ?? '代理')
}

function latencyBadges(row: UsageLogItem) {
  return h('div', { class: 'latency-chips' }, [
    h('span', { class: 'usage-log-chip usage-log-chip-total' }, formatDuration(row.totalDurationMs)),
    h('span', { class: 'usage-log-chip usage-log-chip-first' }, row.isStreaming ? formatDuration(row.firstTokenLatencyMs) : '-'),
    h('span', { class: ['usage-log-chip usage-log-chip-stream', row.isStreamInterrupted ? 'usage-log-chip-stream-interrupted' : ''] }, row.isStreaming ? '流' : '非流')
  ])
}

function buildParams(page = query.page): Record<string, unknown> {
  const params: Record<string, unknown> = { page, pageSize: query.pageSize, rangeType: query.rangeType }
  if (query.siteId) params.siteId = query.siteId
  if (query.accessKeyId) params.accessKeyId = query.accessKeyId
  if (query.source) params.source = query.source
  if (query.status) params.status = query.status
  if (query.modelKeyword.trim()) params.modelKeyword = query.modelKeyword.trim()
  if (query.rangeType === 'custom' && query.startTime) params.startTime = new Date(query.startTime).toISOString()
  if (query.rangeType === 'custom' && query.endTime) params.endTime = new Date(query.endTime).toISOString()
  return params
}

async function loadFilters(): Promise<void> {
  filters.value = await api.getUsageLogFilters()
}

async function load(page = query.page): Promise<void> {
  loading.value = true
  try {
    query.page = page
    const params = buildParams(page)
    const [listResp, summaryResp] = await Promise.all([
      api.listUsageLogs(params),
      api.getUsageLogSummary(params)
    ])
    items.value = listResp.items ?? []
    totalCount.value = listResp.totalCount ?? 0
    summary.value = summaryResp
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    loading.value = false
  }
}

function handleSearch(): void {
  void load(1)
}

// 筛选项选择后立即查询，恢复历史页面“改条件即刷新”的使用方式。
watch(
  () => [query.rangeType, query.siteId, query.accessKeyId, query.source, query.status, query.startTime, query.endTime],
  () => { void load(1) }
)

async function openRequestDetail(requestId: string): Promise<void> {
  if (!requestId) {
    message.warning('当前记录缺少 requestId，无法查看链路')
    return
  }
  detailVisible.value = true
  detailLoading.value = true
  requestDetail.value = null
  try {
    requestDetail.value = await api.getUsageLogRequestDetail(requestId)
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    detailLoading.value = false
  }
}

function configureAutoRefresh(): void {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
    refreshTimer = undefined
  }
  if (autoRefresh.value) {
    refreshTimer = window.setInterval(() => {
      void load(query.page)
    }, 5000)
  }
}

const detailAccessKeyName = computed(() => {
  const names = requestDetail.value?.attempts.map((attempt) => attempt.accessKeyName).filter(Boolean) ?? []
  return [...new Set(names)].join(' / ') || '-'
})

const usageLogsPagination = computed(() => ({
  page: query.page,
  pageSize: query.pageSize,
  itemCount: totalCount.value,
  showSizePicker: true,
  pageSizes: [10, 20, 50, 100],
  prefix: ({ itemCount }: { itemCount?: number }) => `共 ${formatNumber(itemCount ?? 0)} 条`,
  onUpdatePage: (page: number) => { void load(page) },
  onUpdatePageSize: (size: number) => { query.pageSize = size; void load(1) }
}))

const columns = computed<DataTableColumns<UsageLogItem>>(() => [
  { title: '时间', key: 'requestedAt', width: 168, render: (r) => formatDateTime(r.requestedAt) },
  { title: '来源', key: 'source', width: 96, render: (r) => sourceTag(r.source) },
  {
    title: '模型',
    key: 'requestModel',
    minWidth: 180,
    render: (r) => h('div', { class: 'usage-log-model-cell' }, [
      h('code', r.attemptedModel || r.requestModel || '-'),
      r.siteModelName ? h('small', r.siteModelName) : null
    ])
  },
  { title: '目标站点', key: 'siteName', minWidth: 140, ellipsis: { tooltip: true }, render: (r) => r.siteName || '-' },
  { title: '状态', key: 'status', width: 100, render: (r) => statusTag(r) },
  { title: '用时/首字', key: 'totalDurationMs', width: 190, render: (r) => latencyBadges(r) },
  { title: '输入', key: 'inputTokens', width: 88, align: 'right', render: (r) => formatNumber(r.inputTokens) },
  { title: '缓存', key: 'cachedTokens', width: 88, align: 'right', render: (r) => formatNumber(r.cachedTokens) },
  { title: '输出', key: 'outputTokens', width: 88, align: 'right', render: (r) => formatNumber(r.outputTokens) },
  { title: '总Token数', key: 'totalTokens', width: 112, align: 'right', render: (r) => h('strong', formatNumber(r.totalTokens)) },
  { title: '操作', key: 'actions', width: 108, fixed: 'right', render: (r) => h(NButton, { size: 'small', secondary: true, type: 'primary', onClick: () => openRequestDetail(r.requestId) }, () => '查看链路') }
])

onMounted(async () => {
  await loadFilters()
  await load()
  configureAutoRefresh()
})

onUnmounted(() => {
  if (refreshTimer) window.clearInterval(refreshTimer)
})
</script>

<template>
  <div class="page-container usage-logs-page">
    <PageHeader title="使用日志" subtitle="查看代理服务和对话测试的调用记录" />

    <NCard class="usage-logs-filter-card" size="small">
      <div class="usage-logs-filter-header">
        <button
          type="button"
          class="usage-logs-filter-toggle"
          :aria-expanded="filtersExpanded"
          @click="filtersExpanded = !filtersExpanded"
        >
          <span>筛选与汇总</span>
          <span>{{ filtersExpanded ? '收起' : '展开' }}</span>
        </button>
        <div class="usage-logs-filter-header-actions">
          <span class="auto-refresh-label">自动刷新</span>
          <NSwitch v-model:value="autoRefresh" size="small" @update:value="configureAutoRefresh" />
          <NButton circle quaternary size="small" title="自动刷新固定每 5 秒执行一次。">?</NButton>
        </div>
      </div>
      <div v-show="filtersExpanded" class="usage-logs-filter-body">
        <div class="usage-logs-filter-grid">
          <label class="filter-field">
            <span>时间范围</span>
            <NSelect v-model:value="query.rangeType" :options="rangeOptions" size="small" />
          </label>
          <template v-if="query.rangeType === 'custom'">
            <label class="filter-field filter-field-wide">
              <span>开始时间</span>
              <NDatePicker v-model:value="query.startTime" type="datetime" size="small" />
            </label>
            <label class="filter-field filter-field-wide">
              <span>结束时间</span>
              <NDatePicker v-model:value="query.endTime" type="datetime" size="small" />
            </label>
          </template>
          <label class="filter-field">
            <span>站点</span>
            <NSelect v-model:value="query.siteId" :options="siteOptions" placeholder="全部站点" clearable size="small" />
          </label>
          <label class="filter-field">
            <span>访问密钥</span>
            <NSelect v-model:value="query.accessKeyId" :options="keyOptions" placeholder="全部密钥" clearable size="small" />
          </label>
          <label class="filter-field">
            <span>来源</span>
            <NSelect v-model:value="query.source" :options="sourceOptions" clearable size="small" />
          </label>
          <label class="filter-field">
            <span>状态</span>
            <NSelect v-model:value="query.status" :options="statusOptions" clearable size="small" />
          </label>
          <label class="filter-field filter-field-wide">
            <span>模型搜索</span>
            <div class="search-action-row">
              <input v-model="query.modelKeyword" class="model-keyword-input" placeholder="模型模糊搜索" @keyup.enter="handleSearch" />
              <NButton type="primary" size="small" @click="handleSearch">刷新</NButton>
            </div>
          </label>
        </div>
      </div>
    </NCard>

    <NGrid :cols="4" :x-gap="12" :y-gap="12" responsive="screen" item-responsive class="usage-logs-summary-row" :class="{ 'is-collapsed': !filtersExpanded }">
      <NGi span="4 m:2 l:1"><NCard size="small"><NStatistic label="总请求" :value="formatNumber(summary.totalRequests)" /></NCard></NGi>
      <NGi span="4 m:2 l:1"><NCard size="small"><NStatistic label="成功率" :value="formatPercent(summary.successRate)" /></NCard></NGi>
      <NGi span="4 m:2 l:1"><NCard size="small"><NStatistic label="总 Tokens" :value="formatMetricNumber(summary.totalTokens)" /></NCard></NGi>
      <NGi span="4 m:2 l:1"><NCard size="small"><NStatistic label="失败请求" :value="formatNumber(summary.failedRequests)" /></NCard></NGi>
    </NGrid>

    <NCard class="usage-logs-table-card">
      <NDataTable
        :columns="columns"
        :data="items"
        :loading="loading"
        :row-key="(r: UsageLogItem) => r.id"
        :scroll-x="1320"
        remote
        :pagination="usageLogsPagination"
        striped
        size="small"
      />
    </NCard>

    <NDrawer v-model:show="detailVisible" width="520" placement="right">
      <NDrawerContent title="请求链路详情" closable>
        <div v-if="detailLoading" class="detail-placeholder">加载中...</div>
        <template v-else-if="requestDetail">
          <NCard size="small" class="detail-summary-card">
            <div class="detail-line">RequestId: {{ requestDetail.requestId }}</div>
            <div class="detail-line">访问密钥：{{ detailAccessKeyName }}</div>
            <div class="detail-line">路由入口：{{ requestDetail.routeEntry || requestDetail.requestModel || '-' }}</div>
            <div class="detail-line">请求协议：{{ formatProtocolType(requestDetail.protocolType) }}</div>
            <div class="detail-line">调用方式：{{ formatForwardingMode(requestDetail.forwardingMode) }}</div>
            <div class="detail-line">思考等级：{{ requestDetail.reasoningEffort || '-' }}</div>
          </NCard>
          <div class="attempt-list">
            <NCard v-for="(attempt, index) in requestDetail.attempts" :key="attempt.id" size="small">
              <div class="attempt-heading">
                <div>
                  <strong>第 {{ index + 1 }} 次尝试</strong>
                  <div class="detail-line">模型：{{ attempt.attemptedModel || attempt.requestModel || '-' }}</div>
                </div>
                <component :is="statusTag(attempt)" />
              </div>
              <div class="detail-line">站点：{{ attempt.siteName || '-' }}</div>
              <div class="detail-line">时间：{{ formatDateTime(attempt.requestedAt) }}</div>
              <div class="detail-line">Tokens：{{ formatNumber(attempt.totalTokens) }}</div>
              <div class="detail-line">错误：{{ attempt.errorMessage || '-' }}</div>
            </NCard>
          </div>
        </template>
        <NEmpty v-else description="暂无详情" />
      </NDrawerContent>
    </NDrawer>
  </div>
</template>

<style scoped>
.usage-logs-page {
  min-width: 0;
}

.usage-logs-filter-card {
  margin-bottom: 16px;
  overflow: hidden;
}

.usage-logs-filter-header {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.usage-logs-filter-toggle {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  border: 0;
  background: transparent;
  padding: 8px 4px;
  color: var(--text-primary);
  font-size: 14px;
  font-weight: 600;
  cursor: pointer;
}

.usage-logs-filter-header-actions {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}

.auto-refresh-label {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.usage-logs-filter-body {
  border-top: 1px solid var(--border-color-global);
  margin-top: 8px;
  padding-top: 14px;
}

.usage-logs-filter-grid {
  display: grid;
  grid-template-columns: repeat(6, minmax(120px, 1fr));
  gap: 12px;
  align-items: end;
}

.filter-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.filter-field-wide {
  grid-column: span 2;
}

.search-action-row {
  display: flex;
  gap: 8px;
}

.model-keyword-input {
  min-width: 0;
  flex: 1 1 auto;
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 4px;
  background: var(--bg-card);
  color: var(--text-primary);
}

.usage-logs-summary-row {
  margin-bottom: 16px;
}

.usage-logs-summary-row.is-collapsed {
  display: none;
}

.usage-logs-table-card {
  min-width: 0;
  overflow: hidden;
}

.usage-logs-table-card :deep(.n-card__content) {
  min-width: 0;
  overflow: hidden;
}

.usage-logs-table-card :deep(.n-data-table) {
  max-width: 100%;
}

.usage-log-model-cell {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.usage-log-model-cell code {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.usage-log-model-cell small {
  color: var(--text-color-secondary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.latency-chips {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  align-items: center;
}

.usage-log-chip {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 24px;
  padding: 4px 10px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 600;
  line-height: 1;
  white-space: nowrap;
}

.usage-log-chip-total {
  background: #e8f7ea;
  color: #2e7d32;
}

.usage-log-chip-first {
  background: #fff1e0;
  color: #c77800;
}

.usage-log-chip-stream {
  background: #e7f1ff;
  color: #246bfe;
}

.usage-log-chip-stream-interrupted {
  background: #fff3cd;
  color: #996c00;
}

.detail-placeholder,
.detail-line {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.detail-summary-card {
  margin-bottom: 12px;
}

.attempt-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.attempt-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 8px;
}

@media (max-width: 1200px) {
  .usage-logs-filter-grid {
    grid-template-columns: repeat(3, minmax(160px, 1fr));
  }
}

@media (max-width: 640px) {
  .usage-logs-filter-header {
    align-items: stretch;
    flex-direction: column;
  }

  .usage-logs-filter-header-actions {
    justify-content: flex-end;
  }

  .usage-logs-filter-grid {
    grid-template-columns: 1fr;
  }

  .filter-field-wide {
    grid-column: span 1;
  }
}
</style>
