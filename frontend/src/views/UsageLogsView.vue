<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import {
  NButton,
  NCard,
  NDatePicker,
  NDrawer,
  NDrawerContent,
  NEmpty,
  NGi,
  NGrid,
  NInput,
  NSelect,
  NStatistic,
  NSwitch,
  NTag,
  useMessage,
  type SelectOption
} from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import SourceIcon from '@/components/SourceIcon.vue'
import { isRequestCanceled } from '@/api/http'
import * as api from '@/api/usageLogs'
import type { UsageLogItem, UsageLogRequestDetail, UsageLogSummary } from '@/api/usageLogs'
import { getModelPricing } from '@/api/models'
import { useCurrency } from '@/composables/useCurrency'
import {
  buildUsageLogsDefaultCustomRange,
  buildVisibleUsageLogPages,
  canAutoLoadUsageLogs,
  formatUsageLogModel
} from './usageLogsState'
import { usageSourceOptions as sourceOptions } from './usageSource'

const message = useMessage()
const { currency, symbol, formatCost, formatTotalCost, setUsdToCny } = useCurrency()
const loading = ref(false)
const detailLoading = ref(false)
const filtersExpanded = ref(false)
const autoRefresh = ref(true)
const items = ref<UsageLogItem[]>([])
const totalCount = ref(0)
const totalPages = ref(0)
const pageJumpInput = ref<number | null>(null)
const filters = ref<api.UsageLogFilters>({ sites: [], accessKeys: [] })
const summary = ref<UsageLogSummary>({ totalRequests: 0, failedRequests: 0, successRate: 0, totalTokens: 0, maxDurationMs: 0 })
const detailVisible = ref(false)
const requestDetail = ref<UsageLogRequestDetail | null>(null)
let refreshTimer: number | undefined
let searchTimer: number | undefined
let incrementalRefreshInFlight = false
let loadAbortController: AbortController | undefined
let incrementalAbortController: AbortController | undefined
// load() 的请求序号：仅最后一次触发的加载允许写回状态。
let loadRequestSeq = 0
// 明细请求可被后续点击取消，且序号校验避免慢请求覆盖当前选中的链路。
let detailRequestSeq = 0
let detailAbortController: AbortController | undefined

const query = reactive({
  page: 1,
  pageSize: 20,
  rangeType: 'day',
  siteId: null as string | null,
  accessKeyId: null as string | null,
  source: null as string | null,
  status: null as string | null,
  isStreaming: '' as string,
  startTime: null as number | null,
  endTime: null as number | null,
  modelKeyword: ''
})

const siteOptions = computed(() => filters.value.sites.map((s) => ({ label: s.name, value: s.id })))
const keyOptions = computed(() => filters.value.accessKeys.map((k) => ({ label: k.name, value: k.id })))
const statusOptions: SelectOption[] = [
  { label: '全部', value: '' },
  { label: '成功', value: 'success' },
  { label: '失败', value: 'fail' }
]
const streamOptions: SelectOption[] = [
  { label: '全部', value: '' },
  { label: '流式', value: 'true' },
  { label: '非流式', value: 'false' }
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
  // 最多两位小数，清理小数尾零（240.00→240，5.50→5.5，240.25→240.25）。
  const formatted = number.toFixed(2)
    .replace(/(\.\d*?)0+$/, '$1')
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

function getStatusMeta(row: UsageLogItem): { label: string; type: 'success' | 'error' } {
  const success = row.status === 'success' || row.status === 'ok'
  const label = row.fallbackTriggered && success ? '回退后成功' : row.isStreamInterrupted && !success ? '流中断' : success ? '成功' : '失败'
  return { label, type: success ? 'success' : 'error' }
}

function statusTag(row: UsageLogItem) {
  const meta = getStatusMeta(row)
  return h(NTag, { size: 'small', type: meta.type, bordered: false }, () => meta.label)
}

function latencyBadges(row: UsageLogItem) {
  return h('div', { class: 'latency-chips' }, [
    h('span', { class: 'usage-log-chip usage-log-chip-total' }, formatDuration(row.totalDurationMs)),
    h('span', { class: 'usage-log-chip usage-log-chip-first' }, row.isStreaming ? formatDuration(row.firstTokenLatencyMs) : '-'),
    h('span', { class: ['usage-log-chip usage-log-chip-stream', row.isStreamInterrupted ? 'usage-log-chip-stream-interrupted' : ''] }, row.isStreaming ? '流' : '非流')
  ])
}

function ensureCustomRangeDefaults(): void {
  if (query.startTime && query.endTime) return
  const defaults = buildUsageLogsDefaultCustomRange()
  query.startTime ??= defaults.startTime
  query.endTime ??= defaults.endTime
}

function buildParams(page = query.page): Record<string, unknown> {
  const params: Record<string, unknown> = { page, pageSize: query.pageSize, rangeType: query.rangeType }
  if (query.siteId) params.siteId = query.siteId
  if (query.accessKeyId) params.accessKeyId = query.accessKeyId
  if (query.source) params.source = query.source
  if (query.status) params.status = query.status
  if (query.isStreaming === 'true') params.isStreaming = true
  else if (query.isStreaming === 'false') params.isStreaming = false
  if (query.modelKeyword.trim()) params.modelKeyword = query.modelKeyword.trim()
  if (query.rangeType === 'custom' && query.startTime) params.startTime = new Date(query.startTime).toISOString()
  if (query.rangeType === 'custom' && query.endTime) params.endTime = new Date(query.endTime).toISOString()
  return params
}

async function loadFilters(): Promise<void> {
  filters.value = await api.getUsageLogFilters()
}

async function load(page = query.page): Promise<void> {
  // 请求序号守卫：快速切换筛选/翻页时并发多个 load，慢的旧响应后返回
  // 会把表格覆盖成过期筛选条件的结果（与 refreshIncrementally 的 requestKey 守卫同思路）。
  const requestSeq = ++loadRequestSeq
  loadAbortController?.abort()
  incrementalAbortController?.abort()
  const abortController = new AbortController()
  loadAbortController = abortController
  loading.value = true
  try {
    query.page = page
    const params = buildParams(page)
    const [listResp, summaryResp] = await Promise.all([
      api.listUsageLogs(params, abortController.signal),
      api.getUsageLogSummary(params, abortController.signal)
    ])
    if (requestSeq !== loadRequestSeq) return
    items.value = listResp.items ?? []
    query.page = listResp.page
    query.pageSize = listResp.pageSize
    totalCount.value = listResp.totalCount
    totalPages.value = listResp.totalPages
    summary.value = summaryResp
  } catch (e) {
    if (requestSeq === loadRequestSeq && !isRequestCanceled(e)) {
      message.error((e as Error).message)
    }
  } finally {
    if (requestSeq === loadRequestSeq) {
      loading.value = false
      if (loadAbortController === abortController) loadAbortController = undefined
    }
  }
}

function usageLogItemSignature(item: UsageLogItem): string {
  return JSON.stringify(item)
}

function usageLogSummarySignature(value: UsageLogSummary): string {
  return JSON.stringify(value)
}

// 自动刷新只更新发生变化的记录，避免无变化时触发表格整体重绘。
async function refreshIncrementally(): Promise<void> {
  if (incrementalRefreshInFlight || loading.value) return
  incrementalRefreshInFlight = true
  const abortController = new AbortController()
  incrementalAbortController = abortController
  const page = query.page
  const params = buildParams(page)
  const requestKey = JSON.stringify(params)

  try {
    const [listResp, summaryResp] = await Promise.all([
      api.listUsageLogs(params, abortController.signal),
      api.getUsageLogSummary(params, abortController.signal)
    ])

    // 筛选或分页在请求期间发生变化时，丢弃过期的自动刷新结果。
    if (requestKey !== JSON.stringify(buildParams(query.page))) return

    const nextItems = listResp.items ?? []
    const currentItemsById = new Map(items.value.map((item) => [item.id, item]))
    const itemsChanged = nextItems.length !== items.value.length
      || nextItems.some((item, index) => {
        const current = items.value[index]
        return !current || current.id !== item.id || usageLogItemSignature(current) !== usageLogItemSignature(item)
      })

    if (itemsChanged) {
      items.value = nextItems.map((item) => {
        const current = currentItemsById.get(item.id)
        return current && usageLogItemSignature(current) === usageLogItemSignature(item) ? current : item
      })
    }

    if (query.page !== listResp.page) query.page = listResp.page
    if (query.pageSize !== listResp.pageSize) query.pageSize = listResp.pageSize
    if (totalCount.value !== listResp.totalCount) totalCount.value = listResp.totalCount
    if (totalPages.value !== listResp.totalPages) totalPages.value = listResp.totalPages
    if (usageLogSummarySignature(summary.value) !== usageLogSummarySignature(summaryResp)) {
      summary.value = summaryResp
    }
  } catch (e) {
    if (!isRequestCanceled(e)) message.error((e as Error).message)
  } finally {
    incrementalRefreshInFlight = false
    if (incrementalAbortController === abortController) incrementalAbortController = undefined
  }
}

function clearSearchTimer(): void {
  if (searchTimer === undefined) return
  window.clearTimeout(searchTimer)
  searchTimer = undefined
}

function handleSearch(): void {
  clearSearchTimer()
  void load(1)
}

// 筛选项选择后立即查询；自定义时间需先补齐默认范围，避免中间态请求。
watch(
  () => [query.rangeType, query.siteId, query.accessKeyId, query.source, query.status, query.isStreaming, query.startTime, query.endTime],
  () => {
    if (query.rangeType === 'custom' && (!query.startTime || !query.endTime)) {
      ensureCustomRangeDefaults()
      return
    }
    if (!canAutoLoadUsageLogs(query.rangeType, query.startTime, query.endTime)) return
    void load(1)
  }
)

// 模型关键词输入完成后再查询，避免连续输入触发重复请求。
watch(
  () => query.modelKeyword,
  () => {
    clearSearchTimer()
    searchTimer = window.setTimeout(() => {
      searchTimer = undefined
      void load(1)
    }, 300)
  }
)

async function openRequestDetail(requestId: string): Promise<void> {
  if (!requestId) {
    message.warning('当前记录缺少 requestId，无法查看链路')
    return
  }
  detailRequestSeq++
  detailAbortController?.abort()
  const requestSeq = detailRequestSeq
  const abortController = new AbortController()
  detailAbortController = abortController
  detailVisible.value = true
  detailLoading.value = true
  requestDetail.value = null
  try {
    const detail = await api.getUsageLogRequestDetail(requestId, abortController.signal)
    if (requestSeq === detailRequestSeq) {
      requestDetail.value = detail
    }
  } catch (e) {
    if (requestSeq === detailRequestSeq && !isRequestCanceled(e)) {
      message.error((e as Error).message)
    }
  } finally {
    if (requestSeq === detailRequestSeq) {
      detailLoading.value = false
      detailAbortController = undefined
    }
  }
}

function configureAutoRefresh(): void {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
    refreshTimer = undefined
  }
  if (autoRefresh.value) {
    refreshTimer = window.setInterval(() => {
      // 后台标签页不轮询（与其他页面一致），避免不可见时持续打两个接口。
      if (document.visibilityState !== 'visible') return
      void refreshIncrementally()
    }, 5000)
  }
}

const detailAccessKeyName = computed(() => {
  const names = requestDetail.value?.attempts.map((attempt) => attempt.accessKeyName).filter(Boolean) ?? []
  return [...new Set(names)].join(' / ') || '-'
})

const pageNumbers = computed(() => buildVisibleUsageLogPages(query.page, totalPages.value))

const paginationSummary = computed(() => {
  if (totalCount.value === 0) return '共 0 条'
  const start = (query.page - 1) * query.pageSize + 1
  const end = Math.min(query.page * query.pageSize, totalCount.value)
  return `显示第 ${formatNumber(start)} - ${formatNumber(end)} 条，共 ${formatNumber(totalCount.value)} 条`
})

const paginationInfo = computed(() => {
  if (totalCount.value === 0) return '第 0 / 0 页'
  return `第 ${query.page} / ${Math.max(1, totalPages.value)} 页`
})

function goPage(page: number): void {
  const target = Math.min(Math.max(1, page), Math.max(1, totalPages.value))
  void load(target)
}

function jumpToPage(): void {
  if (!pageJumpInput.value) return
  goPage(pageJumpInput.value)
}

onMounted(async () => {
  await loadFilters()
  await load()
  configureAutoRefresh()
  // 汇率来自本地价格表（model-pricing.json 的 usdToCny），失败时保持默认值。
  getModelPricing().then((catalog) => setUsdToCny(catalog?.usdToCny)).catch(() => undefined)
})

onUnmounted(() => {
  if (refreshTimer) window.clearInterval(refreshTimer)
  clearSearchTimer()
  detailRequestSeq++
  detailAbortController?.abort()
  loadRequestSeq++
  loadAbortController?.abort()
  incrementalAbortController?.abort()
})
</script>

<template>
  <div class="page-container usage-logs-page">
    <h2 class="usage-logs-sr-title">调用日志</h2>
    <PageHeader title="调用日志" subtitle="查看代理服务和对话测试的调用记录" />

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
          <NButton class="usage-logs-help-button" circle quaternary size="small" title="自动刷新固定每 5 秒执行一次。">?</NButton>
        </div>
      </div>
      <div v-show="filtersExpanded" class="usage-logs-filter-body">
        <div class="usage-logs-filter-grid">
          <label class="filter-field">
            <span>时间范围</span>
            <NSelect v-model:value="query.rangeType" :options="rangeOptions" size="small" />
          </label>
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
          <label class="filter-field">
            <span>流式模式</span>
            <NSelect v-model:value="query.isStreaming" :options="streamOptions" placeholder="全部" clearable size="small" />
          </label>
          <label class="filter-field filter-field-search">
            <span>模型搜索</span>
            <div class="search-action-row">
              <NInput v-model:value="query.modelKeyword" size="small" placeholder="模型模糊搜索" clearable @keyup.enter="handleSearch" />
              <NButton class="usage-logs-refresh-button" type="primary" size="small" @click="handleSearch">刷新</NButton>
            </div>
          </label>
        </div>
        <div v-if="query.rangeType === 'custom'" class="usage-logs-custom-range-row">
          <label class="filter-field">
            <span>开始时间</span>
            <NDatePicker v-model:value="query.startTime" type="datetime" size="small" clearable />
          </label>
          <label class="filter-field">
            <span>结束时间</span>
            <NDatePicker v-model:value="query.endTime" type="datetime" size="small" clearable />
          </label>
        </div>
      </div>
    </NCard>

    <NGrid :cols="5" :x-gap="12" :y-gap="12" responsive="screen" item-responsive class="usage-logs-summary-row" :class="{ 'is-collapsed': !filtersExpanded }">
      <NGi span="5 m:2 l:1"><NCard size="small" class="usage-logs-stat-card"><NStatistic label="总请求" :value="formatNumber(summary.totalRequests)" /></NCard></NGi>
      <NGi span="5 m:2 l:1"><NCard size="small" class="usage-logs-stat-card"><NStatistic label="成功率" :value="formatPercent(summary.successRate)" /></NCard></NGi>
      <NGi span="5 m:2 l:1"><NCard size="small" class="usage-logs-stat-card"><NStatistic label="总 Tokens" :value="formatMetricNumber(summary.totalTokens)" /></NCard></NGi>
      <NGi span="5 m:2 l:1"><NCard size="small" class="usage-logs-stat-card"><NStatistic label="失败请求" :value="formatNumber(summary.failedRequests)" /></NCard></NGi>
      <NGi span="5 m:2 l:1">
        <NCard size="small" class="usage-logs-cost-card">
          <div class="n-statistic__label">总消耗（{{ currency }}）</div>
          <div class="n-statistic__value usage-logs-cost-value">{{ formatTotalCost(summary.totalCostUsd) }}</div>
        </NCard>
      </NGi>
    </NGrid>

    <div class="table-wrapper usage-logs-table-wrapper">
      <table class="table usage-logs-table">
        <thead>
          <tr>
            <th>时间</th>
            <th>来源</th>
            <th>模型</th>
            <th>目标站点</th>
            <th>访问密钥</th>
            <th>状态</th>
            <th>用时/首字</th>
            <th>输入</th>
            <th>缓存</th>
            <th>输出</th>
            <th>总Token数</th>
            <th>成本({{ symbol }})</th>
            <th>操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-if="loading">
            <td colspan="13">
              <div class="table-empty">
                <div class="table-empty-text">加载中...</div>
              </div>
            </td>
          </tr>
          <tr v-else-if="items.length === 0">
            <td colspan="13">
              <div class="table-empty">
                <div class="table-empty-icon">📋</div>
                <div class="table-empty-text">暂无调用日志</div>
              </div>
            </td>
          </tr>
          <template v-else>
            <tr v-for="item in items" :key="item.id">
              <td>{{ formatDateTime(item.requestedAt) }}</td>
              <td><SourceIcon :source="item.source" /></td>
              <td><code>{{ formatUsageLogModel(item.requestModel, item.attemptedModel, item.source) }}</code></td>
              <td>{{ item.siteName || '-' }}</td>
              <td>{{ item.accessKeyName || '-' }}</td>
              <td><NTag size="small" :type="getStatusMeta(item).type" :bordered="false">{{ getStatusMeta(item).label }}</NTag></td>
              <td><div class="latency-chips">
                <span class="usage-log-chip usage-log-chip-total">{{ formatDuration(item.totalDurationMs) }}</span>
                <span class="usage-log-chip usage-log-chip-first">{{ item.isStreaming ? formatDuration(item.firstTokenLatencyMs) : '-' }}</span>
                <span :class="['usage-log-chip usage-log-chip-stream', item.isStreamInterrupted ? 'usage-log-chip-stream-interrupted' : '']">{{ item.isStreaming ? '流' : '非流' }}</span>
              </div></td>
              <td>{{ formatNumber(item.inputTokens) }}</td>
              <td>{{ formatNumber(item.cachedTokens) }}</td>
              <td>{{ formatNumber(item.outputTokens) }}</td>
              <td><strong>{{ formatNumber(item.totalTokens) }}</strong></td>
              <td><strong :class="{ 'usage-logs-cost-unpriced': item.costUsd === null || item.costUsd === undefined }">{{ formatCost(item.costUsd) }}</strong></td>
              <td>
                <NButton size="small" secondary type="primary" @click="openRequestDetail(item.requestId)">查看链路</NButton>
              </td>
            </tr>
          </template>
        </tbody>
      </table>
    </div>

    <div class="usage-logs-pagination-wrap">
      <div class="usage-logs-pagination-summary">{{ paginationSummary }}</div>
      <div class="usage-logs-pagination">
        <button type="button" class="usage-page-button" :disabled="query.page <= 1 || loading" @click="goPage(1)">首页</button>
        <button type="button" class="usage-page-button" :disabled="query.page <= 1 || loading" @click="goPage(query.page - 1)">上一页</button>
        <div class="usage-page-numbers">
          <button
            v-for="pageNumber in pageNumbers"
            :key="pageNumber"
            type="button"
            class="usage-page-number"
            :class="{ active: pageNumber === query.page }"
            :disabled="loading || pageNumber === query.page"
            @click="goPage(pageNumber)"
          >
            {{ pageNumber }}
          </button>
        </div>
        <span class="usage-pagination-info">{{ paginationInfo }}</span>
        <button type="button" class="usage-page-button" :disabled="query.page >= totalPages || loading" @click="goPage(query.page + 1)">下一页</button>
        <button type="button" class="usage-page-button" :disabled="query.page >= totalPages || loading" @click="goPage(totalPages)">末页</button>
        <div class="usage-page-jump-group">
          <span>跳转</span>
          <input v-model.number="pageJumpInput" type="number" min="1" step="1" placeholder="页码" @keyup.enter="jumpToPage" />
          <button type="button" class="usage-page-button" :disabled="loading" @click="jumpToPage">前往</button>
        </div>
      </div>
    </div>

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
              <div class="detail-line">思考等级：{{ attempt.reasoningEffort || '-' }}</div>
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

.usage-logs-sr-title {
  position: absolute;
  width: 1px;
  height: 1px;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
}

.usage-logs-filter-card {
  margin-bottom: 16px;
  overflow: hidden;
}

.usage-logs-filter-card :deep(.n-card__content) {
  padding: 0;
}

.usage-logs-filter-header {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
  background: var(--bg-card);
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
  padding: 14px 18px;
  color: var(--text-primary);
  font-size: 14px;
  font-weight: 600;
  cursor: pointer;
}

.usage-logs-filter-header-actions {
  display: inline-flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding: 0 18px 0 0;
  white-space: nowrap;
}

.auto-refresh-label {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.usage-logs-help-button {
  width: 20px;
  min-width: 20px;
  height: 20px;
  padding: 0;
  font-size: 11px;
}

.usage-logs-filter-body {
  padding: 16px 18px;
  border-top: 1px solid var(--border-color-global);
}

.usage-logs-filter-grid {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr)) minmax(180px, 1.4fr);
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

.filter-field-search {
  min-width: 0;
}

.search-action-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.search-action-row :deep(.n-input) {
  flex: 1 1 auto;
  min-width: 0;
}

.usage-logs-refresh-button {
  flex-shrink: 0;
}

.usage-logs-custom-range-row {
  display: grid;
  grid-template-columns: repeat(2, minmax(200px, 280px));
  gap: 12px;
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px dashed var(--border-color-global);
}

.usage-logs-summary-row {
  margin-bottom: 16px;
}

.usage-logs-summary-row.is-collapsed {
  display: none;
}

/* 一行五卡的紧凑数值：长数字不撑破卡片 */
.usage-logs-stat-card :deep(.n-statistic__value),
.usage-logs-cost-value {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.usage-logs-cost-card :deep(.n-card__content) {
  padding-bottom: 10px;
}

.usage-logs-cost-value {
  font-size: 24px;
  font-weight: 600;
  margin-top: 2px;
}

.usage-logs-cost-unpriced {
  opacity: 0.45;
  font-weight: 400;
}

.usage-logs-table-wrapper {
  overflow: auto;
  border: 1px solid var(--border-color-global);
  border-radius: 10px;
  background: var(--bg-card);
}

.usage-logs-table {
  width: 100%;
  min-width: 1320px;
  margin: 0;
  border-collapse: collapse;
}

.usage-logs-table th,
.usage-logs-table td {
  padding: 14px 16px;
  border-bottom: 1px solid var(--border-color-global);
  text-align: left;
  vertical-align: middle;
  white-space: nowrap;
}

.usage-logs-table th {
  background: var(--bg-page);
  color: var(--text-color-secondary);
  font-size: 13px;
  font-weight: 600;
}

.usage-logs-table tbody tr:last-child td {
  border-bottom: 0;
}

.usage-logs-pagination-wrap {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 16px;
  flex-wrap: wrap;
}

.usage-logs-pagination-summary,
.usage-pagination-info {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.usage-logs-pagination {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.usage-page-numbers {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.usage-page-button,
.usage-page-number,
.usage-page-size {
  box-sizing: border-box;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  height: 31px;
  min-width: 32px;
  padding: 4px 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 4px;
  background: var(--bg-card);
  color: var(--text-primary);
  cursor: pointer;
  font-size: 13px;
  line-height: 1.5;
  vertical-align: middle;
}

.usage-page-button:disabled,
.usage-page-number:disabled,
.usage-page-size:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.usage-page-number.active {
  border-color: #0d6efd;
  background: #0d6efd;
  color: #fff;
}

.usage-pagination-info {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.usage-page-jump-group {
  display: inline-flex;
  align-items: center;
  gap: 0;
}

.usage-page-jump-group span,
.usage-page-jump-group input {
  box-sizing: border-box;
  height: 31px;
  border: 1px solid var(--border-color-global);
  background: var(--bg-card);
  line-height: 1.5;
}

.usage-page-jump-group span {
  display: inline-flex;
  align-items: center;
  padding: 4px 9px;
  border-radius: 4px 0 0 4px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.usage-page-jump-group input {
  width: 64px;
  min-width: 0;
  margin-left: -1px;
  padding: 4px 8px;
  color: var(--text-primary);
  font-size: 13px;
  outline: none;
}

.usage-page-jump-group .usage-page-button {
  min-width: 46px;
  margin-left: -1px;
  border-radius: 0 4px 4px 0;
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

@media (max-width: 1400px) {
  .usage-logs-filter-grid {
    grid-template-columns: repeat(4, minmax(0, 1fr));
  }
  .filter-field-search {
    grid-column: span 2;
  }
}

@media (max-width: 991px) {
  .usage-logs-filter-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
  .filter-field-search {
    grid-column: span 2;
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

  .filter-field-search {
    grid-column: span 1;
  }

  .usage-logs-custom-range-row {
    grid-template-columns: 1fr;
  }

  .usage-logs-pagination-wrap {
    align-items: flex-start;
    flex-direction: column;
  }

  .usage-logs-pagination {
    width: 100%;
    justify-content: center;
    gap: 4px;
  }

  .usage-page-numbers {
    gap: 4px;
  }

  .usage-page-jump-group {
    flex: 0 0 100%;
    justify-content: center;
    margin-top: 4px;
  }
}

.usage-logs-cost-card {
  :deep(.n-card__content) {
    padding-bottom: 10px;
  }
}

.usage-logs-cost-value {
  font-size: 24px;
  font-weight: 600;
  margin-top: 2px;
}

.usage-logs-cost-unpriced {
  opacity: 0.45;
  font-weight: 400;
}
</style>
