<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { darkChartOverrides, echarts, initChart as initThemedChart, type ECharts } from '@/composables/useEcharts'
import { useTheme } from '@/composables/useTheme'
import {
  NButton,
  NDataTable,
  NDatePicker,
  NEmpty,
  NSelect,
  NSpin,
  NTag,
  type DataTableColumns,
  type SelectOption
} from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import { formatCompact, formatDuration, formatPercentage } from './analyticsFormat'
import * as api from '@/api/analytics'
import type {
  AnalyticsAnalysisDimension,
  AnalyticsBreakdownPoint,
  AnalyticsBusyResult,
  AnalyticsDashboard,
  AnalyticsDashboardResponse,
  AnalyticsFallbackChainPoint,
  AnalyticsFilterOptions,
  AnalyticsLatencyPercentileValues,
  AnalyticsPendingResult
} from '@/api/analytics'
import {
  ANALYTICS_ANALYSIS_TABS,
  DEFAULT_ANALYTICS_ANALYSIS_DIMENSION,
  buildAnalyticsDefaultCustomRange,
  buildAnalyticsQuery,
  calculateAnalyticsTotalTokens,
  removeAnalyticsFilter,
  resetAnalyticsFilters,
  shouldAutoLoadAnalytics,
  sortAnalyticsBreakdown,
  toggleAnalyticsDimensionFilter,
  type AnalyticsFilterKey,
  type AnalyticsFilterState
} from './analyticsState'
import { getUsageSourceLabel, usageSourceOptions } from './usageSource'

const { isDark } = useTheme()

const loading = ref(false)
const waitingForResult = ref(false)
const dashboard = ref<AnalyticsDashboard | null>(null)
let loadController: AbortController | null = null
const filterOptions = ref<AnalyticsFilterOptions | null>(null)

// 筛选条件
const rangeType = ref('week')
const bucketType = ref('auto')
const protocolType = ref('all')
const modelName = ref('all')
const source = ref<string | null>(null)
const siteId = ref<string | null>(null)
const accessKeyId = ref<string | null>(null)
const startTime = ref<number | null>(null)
const endTime = ref<number | null>(null)
const activeAnalysisDimension = ref<AnalyticsAnalysisDimension>(DEFAULT_ANALYTICS_ANALYSIS_DIMENSION)

const rangeOptions: SelectOption[] = [
  { label: '按天', value: 'day' },
  { label: '按周', value: 'week' },
  { label: '按月', value: 'month' },
  { label: '指定时间范围', value: 'custom' }
]
const bucketOptions: SelectOption[] = [
  { label: '自动', value: 'auto' },
  { label: '按小时', value: 'hour' },
  { label: '按天', value: 'day' },
  { label: '按周', value: 'week' },
  { label: '按月', value: 'month' }
]
const protocolOptions: SelectOption[] = [
  { label: '全部', value: 'all' },
  { label: 'OpenAI', value: 'OpenAI' },
  { label: 'Anthropic', value: 'Anthropic' },
  { label: 'Responses', value: 'Responses' }
]

// 8 个图表的 DOM 引用与 ECharts 实例
type ChartKey =
  | 'requestTrend'
  | 'resultTrend'
  | 'tokenTrend'
  | 'durationTrend'
  | 'fallbackTrend'
  | 'cacheRatio'
  | 'siteDist'
  | 'modelDist'
  | 'analysisOverview'
  | 'analysisMetrics'
const chartEls = ref<Record<ChartKey, HTMLElement | null>>({
  requestTrend: null, resultTrend: null, tokenTrend: null, durationTrend: null,
  fallbackTrend: null, cacheRatio: null, siteDist: null, modelDist: null,
  analysisOverview: null, analysisMetrics: null
})
const charts = shallowRef<Record<ChartKey, ECharts | null>>({
  requestTrend: null, resultTrend: null, tokenTrend: null, durationTrend: null,
  fallbackTrend: null, cacheRatio: null, siteDist: null, modelDist: null,
  analysisOverview: null, analysisMetrics: null
})
let chartResizeObserver: ResizeObserver | null = null
const chartClickHandlers = new Map<ChartKey, (params: { dataIndex?: number }) => void>()

const summary = computed(() => dashboard.value?.summary)
const totalTokens = computed(() => calculateAnalyticsTotalTokens(summary.value))
const tokenSplit = computed(() => `${formatCompact(summary.value?.totalInputTokens ?? 0)} / ${formatCompact(summary.value?.totalOutputTokens ?? 0)}`)
const filterSummary = computed(() => {
  const applied = dashboard.value?.appliedFilter
  const appliedRange = applied?.rangeType ?? rangeType.value
  const appliedBucket = applied?.bucketType ?? bucketType.value
  const appliedProtocol = applied?.protocolType ?? protocolType.value
  const appliedModel = applied?.modelName ?? modelName.value
  const appliedSource = applied?.source ?? source.value
  const appliedSiteId = applied?.siteId ?? siteId.value
  const appliedAccessKeyId = applied?.accessKeyId ?? accessKeyId.value
  const parts = [
    rangeOptions.find((option) => option.value === appliedRange)?.label ?? appliedRange,
    bucketOptions.find((option) => option.value === appliedBucket)?.label ?? appliedBucket
  ]
  if (appliedProtocol !== 'all') parts.push(appliedProtocol)
  if (appliedModel !== 'all') parts.push(appliedModel)
  if (appliedSource) parts.push(getUsageSourceLabel(appliedSource))
  if (appliedSiteId) {
    parts.push(filterOptions.value?.sites.find((site) => site.siteId === appliedSiteId)?.siteName ?? appliedSiteId)
  }
  if (appliedAccessKeyId) {
    parts.push(filterOptions.value?.accessKeys.find((key) => key.accessKeyId === appliedAccessKeyId)?.accessKeyLabel ?? appliedAccessKeyId)
  }
  return parts.filter(Boolean).join(' · ')
})

const activeAnalyticsFilters = computed<AnalyticsFilterState>(() => ({
  source: source.value ?? undefined,
  protocolType: protocolType.value !== 'all' ? protocolType.value : undefined,
  modelName: modelName.value !== 'all' ? modelName.value : undefined,
  siteId: siteId.value ?? undefined,
  accessKeyId: accessKeyId.value ?? undefined
}))

const analyticsFilterTags = computed<Array<{
  key: AnalyticsFilterKey
  label: string
  value: string
}>>(() => {
  const filters = activeAnalyticsFilters.value
  const tags: Array<{ key: AnalyticsFilterKey; label: string; value: string }> = []
  if (filters.source) tags.push({ key: 'source', label: '来源', value: getUsageSourceLabel(filters.source) })
  if (filters.protocolType) tags.push({ key: 'protocolType', label: '协议', value: filters.protocolType })
  if (filters.modelName) tags.push({ key: 'modelName', label: '模型', value: filters.modelName })
  if (filters.siteId) {
    tags.push({
      key: 'siteId',
      label: '站点',
      value: filterOptions.value?.sites.find((site) => site.siteId === filters.siteId)?.siteName ?? filters.siteId
    })
  }
  if (filters.accessKeyId) {
    tags.push({
      key: 'accessKeyId',
      label: 'Access Key',
      value: filterOptions.value?.accessKeys.find((key) => key.accessKeyId === filters.accessKeyId)?.accessKeyLabel ?? filters.accessKeyId
    })
  }
  return tags
})

function applyAnalyticsFilterState(next: AnalyticsFilterState): void {
  protocolType.value = next.protocolType ?? 'all'
  modelName.value = next.modelName ?? 'all'
  source.value = next.source ?? null
  siteId.value = next.siteId ?? null
  accessKeyId.value = next.accessKeyId ?? null
}

function removeFilterTag(key: AnalyticsFilterKey): void {
  applyAnalyticsFilterState(removeAnalyticsFilter(activeAnalyticsFilters.value, key))
}

function resetAllAnalyticsFilters(): void {
  applyAnalyticsFilterState(resetAnalyticsFilters(activeAnalyticsFilters.value))
}

function handleDimensionClick(dimension: AnalyticsAnalysisDimension | 'site' | 'model', value: string): void {
  applyAnalyticsFilterState(toggleAnalyticsDimensionFilter(activeAnalyticsFilters.value, dimension, value))
}

function disposeChart(key: ChartKey): void {
  const chart = charts.value[key]
  if (!chart) return

  const handler = chartClickHandlers.get(key)
  if (handler) chart.off('click', handler)
  chart.dispose()
  charts.value[key] = null
  chartClickHandlers.delete(key)
}

function setEl(key: ChartKey, el: HTMLElement | null) {
  const previous = chartEls.value[key]
  if (previous && previous !== el) {
    chartResizeObserver?.unobserve(previous)
    disposeChart(key)
  }
  chartEls.value[key] = el
  if (el) chartResizeObserver?.observe(el)
}

async function loadFilters(): Promise<void> {
  try {
    filterOptions.value = await api.getAnalyticsOptions()
  } catch {
    // 筛选项加载失败不阻塞主数据
  }
}

function ensureCustomRangeDefaults(): void {
  if (startTime.value && endTime.value) return
  const defaults = buildAnalyticsDefaultCustomRange()
  startTime.value ??= defaults.startTime
  endTime.value ??= defaults.endTime
}

function buildParams(): Record<string, unknown> {
  return buildAnalyticsQuery({
    rangeType: rangeType.value,
    bucketType: bucketType.value,
    protocolType: protocolType.value,
    modelName: modelName.value,
    source: source.value,
    siteId: siteId.value,
    accessKeyId: accessKeyId.value,
    startTime: startTime.value,
    endTime: endTime.value
  })
}

function isWaitingResult(
  result: AnalyticsDashboardResponse
): result is AnalyticsPendingResult | AnalyticsBusyResult {
  return 'status' in result
    && (result.status === 'pending' || result.status === 'busy')
}

function waitForRetry(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    const timer = window.setTimeout(() => {
      signal.removeEventListener('abort', handleAbort)
      resolve()
    }, ms)
    const handleAbort = () => {
      window.clearTimeout(timer)
      reject(new DOMException('查询已取消', 'AbortError'))
    }
    signal.addEventListener('abort', handleAbort, { once: true })
  })
}

async function load(maxAttempts = 5): Promise<void> {
  loadController?.abort()
  const controller = new AbortController()
  loadController = controller
  loading.value = true
  waitingForResult.value = false
  const params = buildParams()

  try {
    let result = await api.getAnalyticsDashboard(
      params,
      controller.signal
    )
    let attempts = 0
    while (isWaitingResult(result) && attempts < maxAttempts) {
      attempts += 1
      await waitForRetry(
        result.retryAfterMs ?? 1500,
        controller.signal
      )
      result = await api.getAnalyticsDashboard(
        params,
        controller.signal
      )
    }

    if (isWaitingResult(result)) {
      waitingForResult.value = true
      return
    }

    dashboard.value = result
    await nextTick()
    renderCharts()
  } catch (error) {
    // 错误已由 http 拦截器统一 toast；这里不再 rethrow（调用方均为 void load()，
    // rethrow 只会产生 unhandled rejection 污染控制台）。
    if (!controller.signal.aborted) {
      console.warn('[analytics] load failed', error)
    }
  } finally {
    if (loadController === controller) {
      loadController = null
      loading.value = false
    }
  }
}

function cancelLoad(): void {
  loadController?.abort()
  waitingForResult.value = false
}

function continueWaiting(): void {
  void load(20)
}

function getActiveBreakdownRows(): AnalyticsBreakdownPoint[] {
  const d = dashboard.value
  if (!d) return []

  const rows = activeAnalysisDimension.value === 'source'
    ? d.sourceBreakdown ?? []
    : activeAnalysisDimension.value === 'accessKey'
      ? d.accessKeyBreakdown ?? []
      : activeAnalysisDimension.value === 'protocol'
        ? d.protocolBreakdown ?? []
        : activeAnalysisDimension.value === 'failureReason'
          ? d.failureReasonBreakdown ?? []
          : d.statusCodeBreakdown ?? []

  return sortAnalyticsBreakdown(rows, 'requestCount', 'desc')
}

const activeBreakdownRows = computed(getActiveBreakdownRows)

const breakdownColumns: DataTableColumns<AnalyticsBreakdownPoint> = [
  { title: '项目', key: 'label', minWidth: 160, ellipsis: { tooltip: true } },
  { title: '请求数', key: 'requestCount', width: 100, sorter: (left, right) => left.requestCount - right.requestCount },
  { title: '成功', key: 'successCount', width: 90 },
  { title: '失败', key: 'failedCount', width: 90 },
  { title: '成功率', key: 'successRate', width: 100, render: (row) => formatPercentage(row.successRate) },
  { title: 'Tokens', key: 'totalTokens', width: 110, render: (row) => formatCompact(row.totalTokens) },
  { title: '平均耗时', key: 'averageTotalDurationMs', width: 120, render: (row) => formatDuration(row.averageTotalDurationMs) },
  { title: '回退请求', key: 'fallbackRequestCount', width: 100 }
]

function breakdownRowProps(row: AnalyticsBreakdownPoint): Record<string, unknown> {
  const dimension = activeAnalysisDimension.value
  if (dimension !== 'source' && dimension !== 'accessKey' && dimension !== 'protocol') return {}

  return {
    class: 'analytics-analysis-clickable',
    onClick: () => handleDimensionClick(dimension, row.key)
  }
}

const fallbackChainRows = computed<AnalyticsFallbackChainPoint[]>(() => {
  const rows = (dashboard.value?.fallbackChainDistribution ?? []).map((point, index) => ({
    ...point,
    key: `${point.firstSiteKey}:${point.finalSiteKey}:${index}`
  }))
  return sortAnalyticsBreakdown(rows, 'requestCount', 'desc').map(({ key: _key, ...point }) => point)
})

const fallbackChainColumns: DataTableColumns<AnalyticsFallbackChainPoint> = [
  {
    title: '回退链路',
    key: 'chain',
    minWidth: 220,
    render: (row) => `${row.firstSiteLabel} → ${row.finalSiteLabel}`
  },
  { title: '请求数', key: 'requestCount', width: 100 },
  { title: '成功数', key: 'successCount', width: 100 },
  { title: '成功率', key: 'successRate', width: 100, render: (row) => formatPercentage(row.successRate) },
  { title: '平均尝试次数', key: 'averageAttemptCount', width: 130, render: (row) => row.averageAttemptCount.toFixed(2) }
]

type AnalyticsLatencyRow = AnalyticsLatencyPercentileValues & { label: string }

const latencyRows = computed<AnalyticsLatencyRow[]>(() => {
  const percentiles = dashboard.value?.latencyPercentiles
  if (!percentiles) return []
  return [
    { label: '总耗时', ...percentiles.totalDuration },
    { label: '首字延迟', ...percentiles.firstTokenLatency }
  ]
})

const latencyColumns: DataTableColumns<AnalyticsLatencyRow> = [
  { title: '指标', key: 'label', minWidth: 140 },
  { title: 'P50', key: 'p50', width: 110, render: (row) => formatDuration(row.p50) },
  { title: 'P95', key: 'p95', width: 110, render: (row) => formatDuration(row.p95) },
  { title: 'P99', key: 'p99', width: 110, render: (row) => formatDuration(row.p99) },
  { title: '样本数', key: 'sampleCount', width: 110 }
]

const analysisTitle = computed(() =>
  ANALYTICS_ANALYSIS_TABS.find((tab) => tab.key === activeAnalysisDimension.value)?.label ?? '细分分析'
)

const analysisSubtitle = computed(() => {
  switch (activeAnalysisDimension.value) {
    case 'source': return '对比不同请求来源的调用规模、成功失败构成、Token 消耗与平均耗时。'
    case 'accessKey': return '观察不同访问密钥的使用量和稳定性，名称保持脱敏展示。'
    case 'protocol': return '对比不同协议入口的请求量、成功率、Token 消耗与响应耗时。'
    case 'failureReason': return '按失败分类定位主要异常类型，优先关注数量高且成功率低的项目。'
    case 'statusCode': return '从 HTTP 状态码分布观察上游响应和无响应异常。'
    case 'fallbackChain': return '用链路图观察请求从首个站点到最终站点的回退路径。'
    case 'latencyPercentiles': return '用 P50、P95、P99 对比典型延迟和长尾延迟，避免平均值掩盖问题。'
  }
})

const activeAnalysisHasData = computed(() =>
  activeAnalysisDimension.value === 'fallbackChain'
    ? fallbackChainRows.value.length > 0
    : activeAnalysisDimension.value === 'latencyPercentiles'
      ? latencyRows.value.length > 0
      : activeBreakdownRows.value.length > 0
)

type AnalysisMetricCard = { label: string; value: string; tone?: 'success' | 'danger' | 'warning' }

const analysisMetricCards = computed<AnalysisMetricCard[]>(() => {
  if (activeAnalysisDimension.value === 'latencyPercentiles') {
    const total = dashboard.value?.latencyPercentiles?.totalDuration
    const firstToken = dashboard.value?.latencyPercentiles?.firstTokenLatency
    return [
      { label: '总耗时 P50', value: formatDuration(total?.p50) },
      { label: '总耗时 P95', value: formatDuration(total?.p95), tone: 'warning' },
      { label: '总耗时 P99', value: formatDuration(total?.p99), tone: 'danger' },
      { label: '首字延迟 P95', value: formatDuration(firstToken?.p95) }
    ]
  }

  if (activeAnalysisDimension.value === 'fallbackChain') {
    const rows = fallbackChainRows.value
    const requestCount = rows.reduce((sum, row) => sum + row.requestCount, 0)
    const successCount = rows.reduce((sum, row) => sum + row.successCount, 0)
    const averageAttemptCount = requestCount === 0
      ? 0
      : rows.reduce((sum, row) => sum + row.averageAttemptCount * row.requestCount, 0) / requestCount
    return [
      { label: '链路数量', value: formatCompact(rows.length) },
      { label: '回退请求', value: formatCompact(requestCount), tone: 'warning' },
      { label: '最终成功率', value: formatPercentage(requestCount === 0 ? 0 : successCount * 100 / requestCount), tone: 'success' },
      { label: '平均尝试次数', value: averageAttemptCount.toFixed(2) }
    ]
  }

  const rows = activeBreakdownRows.value
  const requestCount = rows.reduce((sum, row) => sum + row.requestCount, 0)
  const successCount = rows.reduce((sum, row) => sum + row.successCount, 0)
  const fallbackCount = rows.reduce((sum, row) => sum + row.fallbackRequestCount, 0)
  const totalTokens = rows.reduce((sum, row) => sum + row.totalTokens, 0)
  const averageDuration = requestCount === 0
    ? 0
    : rows.reduce((sum, row) => sum + row.averageTotalDurationMs * row.requestCount, 0) / requestCount
  return [
    { label: '项目数量', value: formatCompact(rows.length) },
    { label: '请求总数', value: formatCompact(requestCount) },
    { label: '成功率', value: formatPercentage(requestCount === 0 ? 0 : successCount * 100 / requestCount), tone: 'success' },
    { label: '回退请求', value: formatCompact(fallbackCount), tone: 'warning' },
    { label: 'Token 总量', value: formatCompact(totalTokens) },
    { label: '加权平均耗时', value: formatDuration(averageDuration) }
  ]
})

const PRIMARY = '#3b82f6'
const SUCCESS = '#10b981'
const WARNING = '#f59e0b'
const DANGER = '#ef4444'
const CACHED = '#6366f1'
const CYAN = '#06b6d4'

type TooltipItem = {
  axisValueLabel?: string
  dataIndex: number
  marker: string
  seriesName: string
  value: number | string
}

function asTooltipItems(value: unknown): TooltipItem[] {
  return Array.isArray(value) ? value as TooltipItem[] : []
}

// 与旧统计页一致：坐标轴类图表在提示层统一展示时间桶、指标名和格式化后的数值。
function formatAxisTooltip(value: unknown, valueFormatter: (item: TooltipItem) => string): string {
  const items = asTooltipItems(value)
  if (items.length === 0) return ''

  return [
    items[0].axisValueLabel,
    ...items.map((item) => `${item.marker}${item.seriesName}：${valueFormatter(item)}`)
  ].join('<br/>')
}

function initChart(key: ChartKey): ECharts | null {
  const el = chartEls.value[key]
  if (!el) return null
  // 用主题感知的初始化（暗色模式注册了 aitool-dark 主题）
  if (!charts.value[key]) charts.value[key] = initThemedChart(el)
  return charts.value[key]
}

function bindChartClick(
  key: ChartKey,
  chart: ECharts,
  handler: (params: { dataIndex?: number }) => void
): void {
  const previous = chartClickHandlers.get(key)
  if (previous) chart.off('click', previous)
  chart.on('click', handler)
  chartClickHandlers.set(key, handler)
}

function renderCharts(): void {
  const d = dashboard.value
  if (!d) return

  const tokenAxisLabel = { formatter: (value: number) => formatCompact(value) }
  const durationAxisLabel = { formatter: (value: number) => formatDuration(value) }

  const c1 = initChart('requestTrend')
  if (c1 && d.requestTrend) {
    c1.setOption({
      tooltip: {
        trigger: 'axis',
        formatter: (items: unknown) => formatAxisTooltip(items, (item) => formatCompact(Number(item.value)))
      },
      grid: { left: 40, right: 20, top: 20, bottom: 30 },
      xAxis: { type: 'category', data: d.requestTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', minInterval: 1, axisLabel: tokenAxisLabel },
      series: [{ name: '请求数', type: 'line', smooth: true, data: d.requestTrend.map((t) => t.requestCount), areaStyle: { opacity: 0.15 }, itemStyle: { color: PRIMARY } }]
    }, true)
  }

  const c2 = initChart('resultTrend')
  if (c2 && d.resultTrend) {
    c2.setOption({
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (items: unknown) => formatAxisTooltip(items, (item) => item.seriesName === '成功率'
          ? formatPercentage(Number(item.value))
          : formatCompact(Number(item.value)))
      },
      legend: { data: ['成功', '失败', '成功率'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 40, right: 50, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.resultTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: [
        { type: 'value', minInterval: 1, axisLabel: tokenAxisLabel },
        { type: 'value', name: '%', position: 'right', min: 0, max: 100, axisLabel: { formatter: '{value}%' } }
      ],
      series: [
        { name: '成功', type: 'bar', data: d.resultTrend.map((t) => t.successCount), itemStyle: { color: SUCCESS } },
        { name: '失败', type: 'bar', data: d.resultTrend.map((t) => t.failCount), itemStyle: { color: DANGER } },
        { name: '成功率', type: 'line', yAxisIndex: 1, smooth: true, data: d.resultTrend.map((t) => t.successRate ?? 0), itemStyle: { color: CACHED } }
      ]
    }, true)
  }

  const c3 = initChart('tokenTrend')
  if (c3 && d.tokenTrend) {
    const data = d.tokenTrend
    c3.setOption({
      tooltip: {
        trigger: 'axis',
        formatter: (items: unknown) => {
          const values = asTooltipItems(items)
          const index = values[0]?.dataIndex ?? 0
          const total = data[index]?.totalTokens ?? 0
          return `${formatAxisTooltip(values, (item) => formatCompact(Number(item.value)))}<br/>总量：${formatCompact(total)}`
        }
      },
      legend: { data: ['输入', '输出', '缓存'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 50, right: 20, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: data.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', axisLabel: tokenAxisLabel },
      series: [
        { name: '输入', type: 'line', smooth: true, data: data.map((t) => t.inputTokens), itemStyle: { color: PRIMARY } },
        { name: '输出', type: 'line', smooth: true, data: data.map((t) => t.outputTokens), itemStyle: { color: SUCCESS } },
        { name: '缓存', type: 'line', smooth: true, data: data.map((t) => t.cachedTokens), itemStyle: { color: CYAN } }
      ]
    }, true)
  }

  const c4 = initChart('durationTrend')
  if (c4 && d.durationTrend) {
    c4.setOption({
      tooltip: {
        trigger: 'axis',
        formatter: (items: unknown) => formatAxisTooltip(items, (item) => formatDuration(Number(item.value)))
      },
      legend: { data: ['总耗时', '首字耗时'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 50, right: 20, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.durationTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', axisLabel: durationAxisLabel },
      series: [
        { name: '总耗时', type: 'line', smooth: true, data: d.durationTrend.map((t) => t.averageTotalDurationMs), areaStyle: { opacity: 0.15 }, itemStyle: { color: WARNING } },
        { name: '首字耗时', type: 'line', smooth: true, data: d.durationTrend.map((t) => t.averageFirstTokenLatencyMs), itemStyle: { color: CYAN } }
      ]
    }, true)
  }

  const c5 = initChart('fallbackTrend')
  if (c5 && d.fallbackTrend) {
    c5.setOption({
      tooltip: {
        trigger: 'axis',
        formatter: (items: unknown) => formatAxisTooltip(items, (item) => item.seriesName === '回退率'
          ? formatPercentage(Number(item.value))
          : formatCompact(Number(item.value)))
      },
      legend: { data: ['回退次数', '回退率'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 40, right: 50, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.fallbackTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: [
        { type: 'value', name: '次数', position: 'left', minInterval: 1, axisLabel: tokenAxisLabel },
        { type: 'value', name: '%', position: 'right', max: 100, axisLabel: { formatter: '{value}%' } }
      ],
      series: [
        { name: '回退次数', type: 'bar', data: d.fallbackTrend.map((t) => t.fallbackCount), itemStyle: { color: WARNING } },
        { name: '回退率', type: 'line', yAxisIndex: 1, smooth: true, data: d.fallbackTrend.map((t) => t.fallbackRate), itemStyle: { color: DANGER } }
      ]
    }, true)
  }

  const c6 = initChart('cacheRatio')
  if (c6 && d.modelCacheRatioDistribution) {
    const data = d.modelCacheRatioDistribution
    c6.setOption({
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (items: unknown) => {
          const values = asTooltipItems(items)
          const index = values[0]?.dataIndex ?? 0
          const point = data[index]
          const details = point
            ? `<br/>总输入：${formatCompact(point.totalInputScope)}<br/>缓存：${formatCompact(point.cachedTokens)}<br/>未命中输入：${formatCompact(point.inputTokens)}`
            : ''
          return `${formatAxisTooltip(values, (item) => item.seriesName === '缓存命中率'
            ? formatPercentage(Number(item.value))
            : formatCompact(Number(item.value)))}${details}`
        }
      },
      legend: { data: ['缓存命中率', '缓存命中 Token'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 50, right: 58, top: 30, bottom: 60 },
      xAxis: { type: 'category', data: data.map((t) => t.label), axisLabel: { fontSize: 10, rotate: 30, interval: 0 } },
      yAxis: [
        { type: 'value', max: 100, axisLabel: { formatter: '{value}%' } },
        { type: 'value', position: 'right', axisLabel: tokenAxisLabel }
      ],
      series: [
        { name: '缓存命中率', type: 'bar', data: data.map((t) => t.cacheHitRate), itemStyle: { color: CYAN }, barMaxWidth: 32 },
        { name: '缓存命中 Token', type: 'line', yAxisIndex: 1, smooth: true, data: data.map((t) => t.cachedTokens), itemStyle: { color: CACHED } }
      ]
    }, true)
  }

  const c7 = initChart('siteDist')
  if (c7 && d.siteDistribution) {
    const data = d.siteDistribution
    c7.setOption({
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (items: unknown) => {
          const values = asTooltipItems(items)
          const point = data[values[0]?.dataIndex ?? 0]
          const details = point
            ? `<br/>成功：${formatCompact(point.successCount)}<br/>失败：${formatCompact(point.failedCount)}<br/>平均耗时：${formatDuration(point.averageTotalDurationMs)}`
            : ''
          return `${formatAxisTooltip(values, (item) => formatCompact(Number(item.value)))}${details}`
        }
      },
      grid: { left: 44, right: 20, top: 20, bottom: 62 },
      xAxis: { type: 'category', data: data.map((t) => t.label), axisLabel: { fontSize: 10, rotate: 30, interval: 0 } },
      yAxis: { type: 'value', minInterval: 1, axisLabel: tokenAxisLabel },
      series: [{ name: '请求数', type: 'bar', data: data.map((t) => t.requestCount), itemStyle: { color: CACHED }, barMaxWidth: 32 }]
    }, true)
    bindChartClick('siteDist', c7, (params) => {
      const point = data[params.dataIndex ?? -1]
      if (point?.key) handleDimensionClick('site', point.key)
    })
  }

  const c8 = initChart('modelDist')
  if (c8 && d.modelDistribution) {
    const data = d.modelDistribution
    c8.setOption({
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (items: unknown) => {
          const values = asTooltipItems(items)
          const point = data[values[0]?.dataIndex ?? 0]
          const details = point
            ? `<br/>缓存：${formatCompact(point.cachedTokens)}<br/>未命中输入：${formatCompact(point.inputTokens)}<br/>输出：${formatCompact(point.outputTokens)}<br/>调用次数：${formatCompact(point.requestCount)}<br/>成功：${formatCompact(point.successCount)}<br/>失败：${formatCompact(point.failedCount)}`
            : ''
          return `${formatAxisTooltip(values, (item) => formatCompact(Number(item.value)))}${details}`
        }
      },
      grid: { left: 50, right: 20, top: 20, bottom: 62 },
      xAxis: { type: 'category', data: data.map((t) => t.label), axisLabel: { fontSize: 10, rotate: 30, interval: 0 } },
      yAxis: { type: 'value', axisLabel: tokenAxisLabel },
      series: [{ name: 'Token 用量', type: 'bar', data: data.map((t) => t.totalTokens ?? 0), itemStyle: { color: SUCCESS }, barMaxWidth: 32 }]
    }, true)
    bindChartClick('modelDist', c8, (params) => {
      const point = data[params.dataIndex ?? -1]
      if (point?.key) handleDimensionClick('model', point.key)
    })
  }

  if (isDark.value) {
    const overrides = darkChartOverrides()
    ;(Object.keys(charts.value) as ChartKey[]).forEach((key) => {
      charts.value[key]?.setOption(overrides, false)
    })
  }

  renderAnalysisCharts()
}

function renderAnalysisCharts(): void {
  if (!dashboard.value || !activeAnalysisHasData.value) return

  const overviewChart = initChart('analysisOverview')
  const metricsChart = initChart('analysisMetrics')
  if (!overviewChart || !metricsChart) return

  if (activeAnalysisDimension.value === 'fallbackChain') {
    const data = fallbackChainRows.value
    const nodes = new Map<string, { name: string; label: string }>()
    const links = data.map((row) => {
      const source = `first:${row.firstSiteKey}`
      const target = `final:${row.finalSiteKey}`
      nodes.set(source, { name: source, label: row.firstSiteLabel })
      nodes.set(target, { name: target, label: row.finalSiteLabel })
      return { source, target, value: row.requestCount }
    })

    overviewChart.setOption({
      tooltip: {
        trigger: 'item',
        formatter: (params: { data?: { label?: string; source?: string; target?: string; value?: number } }) => {
          const item = params.data
          if (!item) return ''
          if (item.source && item.target) {
            const row = data.find((point) => `first:${point.firstSiteKey}` === item.source && `final:${point.finalSiteKey}` === item.target)
            return row
              ? `${row.firstSiteLabel} → ${row.finalSiteLabel}<br/>请求数：${formatCompact(row.requestCount)}<br/>成功率：${formatPercentage(row.successRate)}`
              : ''
          }
          return item.label ?? ''
        }
      },
      series: [{
        type: 'sankey',
        left: 12,
        right: 12,
        top: 12,
        bottom: 12,
        nodeWidth: 16,
        nodeGap: 18,
        draggable: false,
        emphasis: { focus: 'adjacency' },
        data: Array.from(nodes.values()),
        links
      }]
    }, true)

    metricsChart.setOption({
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (items: unknown) => formatAxisTooltip(items, (item) => item.seriesName === '成功率'
          ? formatPercentage(Number(item.value))
          : formatCompact(Number(item.value)))
      },
      legend: { data: ['请求数', '成功率'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 42, right: 48, top: 32, bottom: 62 },
      xAxis: { type: 'category', data: data.map((row) => `${row.firstSiteLabel} → ${row.finalSiteLabel}`), axisLabel: { fontSize: 10, rotate: 28, interval: 0 } },
      yAxis: [
        { type: 'value', minInterval: 1 },
        { type: 'value', max: 100, axisLabel: { formatter: '{value}%' } }
      ],
      series: [
        { name: '请求数', type: 'bar', data: data.map((row) => row.requestCount), itemStyle: { color: WARNING }, barMaxWidth: 32 },
        { name: '成功率', type: 'line', yAxisIndex: 1, smooth: true, data: data.map((row) => row.successRate), itemStyle: { color: SUCCESS } }
      ]
    }, true)
    return
  }

  if (activeAnalysisDimension.value === 'latencyPercentiles') {
    const data = latencyRows.value
    overviewChart.setOption({
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (items: unknown) => formatAxisTooltip(items, (item) => formatDuration(Number(item.value)))
      },
      legend: { data: ['P50', 'P95', 'P99'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 52, right: 20, top: 32, bottom: 42 },
      xAxis: { type: 'category', data: data.map((row) => row.label) },
      yAxis: { type: 'value', axisLabel: { formatter: (value: number) => formatDuration(value) } },
      series: [
        { name: 'P50', type: 'bar', data: data.map((row) => row.p50), itemStyle: { color: PRIMARY }, barMaxWidth: 28 },
        { name: 'P95', type: 'bar', data: data.map((row) => row.p95), itemStyle: { color: WARNING }, barMaxWidth: 28 },
        { name: 'P99', type: 'bar', data: data.map((row) => row.p99), itemStyle: { color: DANGER }, barMaxWidth: 28 }
      ]
    }, true)
    metricsChart.setOption({
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'shadow' },
        formatter: (items: unknown) => formatAxisTooltip(items, (item) => formatCompact(Number(item.value)))
      },
      grid: { left: 52, right: 20, top: 20, bottom: 42 },
      xAxis: { type: 'category', data: data.map((row) => row.label) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [{ name: '样本数', type: 'bar', data: data.map((row) => row.sampleCount), itemStyle: { color: CYAN }, barMaxWidth: 40 }]
    }, true)
    return
  }

  const data = activeBreakdownRows.value
  const labels = data.map((row) => row.label)
  overviewChart.setOption({
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      formatter: (items: unknown) => {
        const values = asTooltipItems(items)
        const row = data[values[0]?.dataIndex ?? -1]
        return row
          ? `${row.label}<br/>成功：${formatCompact(row.successCount)}<br/>失败：${formatCompact(row.failedCount)}<br/>成功率：${formatPercentage(row.successRate)}`
          : ''
      }
    },
    legend: { data: ['成功', '失败'], top: 0, textStyle: { fontSize: 11 } },
    grid: { left: 110, right: 20, top: 32, bottom: 24 },
    xAxis: { type: 'value', minInterval: 1 },
    yAxis: { type: 'category', data: labels, inverse: true, axisLabel: { width: 96, overflow: 'truncate' } },
    series: [
      { name: '成功', type: 'bar', stack: 'request', data: data.map((row) => row.successCount), itemStyle: { color: SUCCESS }, barMaxWidth: 26 },
      { name: '失败', type: 'bar', stack: 'request', data: data.map((row) => row.failedCount), itemStyle: { color: DANGER }, barMaxWidth: 26 }
    ]
  }, true)

  metricsChart.setOption({
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      formatter: (items: unknown) => formatAxisTooltip(items, (item) => item.seriesName === '平均耗时'
        ? formatDuration(Number(item.value))
        : formatCompact(Number(item.value)))
    },
    legend: { data: ['Token 总量', '平均耗时'], top: 0, textStyle: { fontSize: 11 } },
    grid: { left: 52, right: 58, top: 32, bottom: 62 },
    xAxis: { type: 'category', data: labels, axisLabel: { fontSize: 10, rotate: 28, interval: 0 } },
    yAxis: [
      { type: 'value', axisLabel: { formatter: (value: number) => formatCompact(value) } },
      { type: 'value', position: 'right', axisLabel: { formatter: (value: number) => formatDuration(value) } }
    ],
    series: [
      { name: 'Token 总量', type: 'bar', data: data.map((row) => row.totalTokens), itemStyle: { color: CACHED }, barMaxWidth: 30 },
      { name: '平均耗时', type: 'line', yAxisIndex: 1, smooth: true, data: data.map((row) => row.averageTotalDurationMs), itemStyle: { color: WARNING } }
    ]
  }, true)

  const handleBreakdownClick = (params: { dataIndex?: number }) => {
    const row = data[params.dataIndex ?? -1]
    if (row) handleDimensionClick(activeAnalysisDimension.value, row.key)
  }
  bindChartClick('analysisOverview', overviewChart, handleBreakdownClick)
  bindChartClick('analysisMetrics', metricsChart, handleBreakdownClick)
}

function handleResize(): void {
  (Object.keys(charts.value) as ChartKey[]).forEach((k) => charts.value[k]?.resize())
}

onMounted(() => {
  chartResizeObserver = new ResizeObserver(handleResize)
  ;(Object.keys(chartEls.value) as ChartKey[]).forEach((key) => {
    const el = chartEls.value[key]
    if (el) chartResizeObserver?.observe(el)
  })
  window.addEventListener('resize', handleResize)
  void Promise.all([loadFilters(), load()])
})
onUnmounted(() => {
  loadController?.abort()
  chartResizeObserver?.disconnect()
  chartResizeObserver = null
  window.removeEventListener('resize', handleResize)
  ;(Object.keys(charts.value) as ChartKey[]).forEach((key) => disposeChart(key))
})

// 切换到指定时间范围时等待用户确认，避免使用尚未填写的时间提前查询。
watch(rangeType, (value) => {
  if (value === 'custom') {
    ensureCustomRangeDefaults()
    return
  }
  if (shouldAutoLoadAnalytics(value)) void load()
})
watch([bucketType, protocolType, modelName, source, siteId, accessKeyId], () => { void load() })
watch(isDark, () => {
  if (dashboard.value?.summary.totalRequests) renderCharts()
})
watch(activeAnalysisDimension, async () => {
  await nextTick()
  renderAnalysisCharts()
})
</script>

<template>
  <div class="page-container analytics-page">
    <PageHeader title="可视化分析" subtitle="聚合请求量、成功率、tokens 用量、耗时与路由分布，面向日常观察和排障">
      <template #actions>
        <NButton
          v-if="loading"
          type="warning"
          secondary
          @click="cancelLoad"
        >
          取消查询
        </NButton>
        <template v-else-if="waitingForResult">
          <NButton secondary @click="cancelLoad">取消等待</NButton>
          <NButton type="primary" @click="continueWaiting">继续等待</NButton>
        </template>
        <NButton v-else type="primary" @click="load()">刷新数据</NButton>
      </template>
    </PageHeader>

    <NSpin :show="loading">
      <section class="analytics-filter card mb-4">
        <div class="analytics-filter-body">
          <div class="analytics-filter-grid">
            <label class="analytics-filter-field">
              <span class="form-label">时间范围</span>
              <NSelect v-model:value="rangeType" :options="rangeOptions" />
            </label>
            <label class="analytics-filter-field">
              <span class="form-label">统计粒度</span>
              <NSelect v-model:value="bucketType" :options="bucketOptions" />
            </label>
            <label class="analytics-filter-field">
              <span class="form-label">协议类型</span>
              <NSelect v-model:value="protocolType" :options="protocolOptions" />
            </label>
            <label class="analytics-filter-field">
              <span class="form-label">调用模型</span>
              <NSelect
                v-model:value="modelName"
                :options="[{ label: '全部模型', value: 'all' }, ...(filterOptions?.models ?? []).map((m) => ({ label: m.modelName, value: m.modelName }))]"
                filterable
                tag
              />
            </label>
            <label class="analytics-filter-field">
              <span class="form-label">来源</span>
              <NSelect v-model:value="source" :options="usageSourceOptions" clearable />
            </label>
            <label class="analytics-filter-field">
              <span class="form-label">站点</span>
              <NSelect
                v-model:value="siteId"
                :options="(filterOptions?.sites ?? []).map((s) => ({ label: s.siteName, value: s.siteId }))"
                placeholder="全部站点"
                clearable
              />
            </label>
            <label class="analytics-filter-field">
              <span class="form-label">访问密钥</span>
              <NSelect
                v-model:value="accessKeyId"
                :options="(filterOptions?.accessKeys ?? []).map((k) => ({ label: k.accessKeyLabel, value: k.accessKeyId }))"
                placeholder="全部访问密钥"
                clearable
              />
            </label>
            <div class="analytics-filter-meta">{{ filterSummary }}</div>
          </div>
          <div v-if="analyticsFilterTags.length > 0" class="analytics-active-filters">
            <span class="analytics-active-filters-label">当前筛选</span>
            <NTag
              v-for="tag in analyticsFilterTags"
              :key="tag.key"
              size="small"
              closable
              :bordered="false"
              @close="removeFilterTag(tag.key)"
            >
              {{ tag.label }}：{{ tag.value }}
            </NTag>
            <NButton text size="small" @click="resetAllAnalyticsFilters">全部重置</NButton>
          </div>
          <div v-if="rangeType === 'custom'" class="analytics-custom-range-row">
            <label class="analytics-filter-field analytics-custom-range-field">
              <span class="form-label">开始时间</span>
              <NDatePicker v-model:value="startTime" type="datetime" placeholder="开始时间" clearable />
            </label>
            <label class="analytics-filter-field analytics-custom-range-field">
              <span class="form-label">结束时间</span>
              <NDatePicker v-model:value="endTime" type="datetime" placeholder="结束时间" clearable />
            </label>
            <NButton secondary type="primary" class="analytics-apply-range" @click="load()">应用时间范围</NButton>
            <div class="analytics-range-tip">选择开始和结束时间后，点击“应用时间范围”确认生效。</div>
          </div>
          <div v-if="loading" class="analytics-query-status">
            查询正在后台计算，可随时取消。
          </div>
          <div v-else-if="waitingForResult" class="analytics-query-status pending">
            查询仍在计算中，可继续等待或取消本次等待。
          </div>
        </div>
      </section>

      <NEmpty
        v-if="!loading && !waitingForResult && (!dashboard || summary?.totalRequests === 0)"
        description="当前筛选条件下暂无可视化数据"
        class="analytics-empty"
      />

      <template v-if="summary && summary.totalRequests > 0">
        <section class="analytics-kpi-grid mb-4">
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">总请求数</span>
            <strong class="analytics-kpi-value">{{ formatCompact(summary.totalRequests) }}</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">成功率</span>
            <strong class="analytics-kpi-value success">{{ formatPercentage(summary.successRate) }}</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">失败率</span>
            <strong class="analytics-kpi-value danger">{{ formatPercentage(summary.failureRate) }}</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">总 Tokens</span>
            <strong class="analytics-kpi-value">{{ formatCompact(totalTokens) }}</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">平均总耗时</span>
            <strong class="analytics-kpi-value">{{ formatDuration(summary.averageTotalDurationMs) }}</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">平均首字耗时</span>
            <strong class="analytics-kpi-value">{{ formatDuration(summary.averageFirstTokenLatencyMs) }}</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">回退触发数</span>
            <strong class="analytics-kpi-value warning">{{ formatCompact(summary.fallbackRequestCount ?? 0) }}</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">输入（含缓存） / 输出 Tokens</span>
            <strong class="analytics-kpi-value compact">{{ tokenSplit }}</strong>
          </article>
        </section>

        <div class="analytics-grid">
          <section class="analytics-panel analytics-panel-wide card">
            <div class="analytics-panel-header">
              <div>
                <h5 class="analytics-panel-title">请求量趋势</h5>
                <div class="analytics-panel-subtitle">按时间桶观察请求量波动</div>
              </div>
            </div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('requestTrend', el as HTMLElement | null)" class="chart-body" /></div>
          </section>

          <section class="analytics-panel card">
            <div class="analytics-panel-header"><div><h5 class="analytics-panel-title">成功 / 失败趋势</h5><div class="analytics-panel-subtitle">同时展示数量与比率</div></div></div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('resultTrend', el as HTMLElement | null)" class="chart-body" /></div>
          </section>

          <section class="analytics-panel card">
            <div class="analytics-panel-header"><div><h5 class="analytics-panel-title">Tokens 用量趋势</h5><div class="analytics-panel-subtitle">输入、输出、缓存三条趋势线，总量保留在顶部汇总卡片</div></div></div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('tokenTrend', el as HTMLElement | null)" class="chart-body" /></div>
          </section>

          <section class="analytics-panel card">
            <div class="analytics-panel-header"><div><h5 class="analytics-panel-title">缓存命中比例</h5><div class="analytics-panel-subtitle">公式：缓存命中 Token ÷ 总输入 Token（总输入 = 未命中输入 + 缓存命中）</div></div></div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('cacheRatio', el as HTMLElement | null)" class="chart-body" /></div>
          </section>

          <section class="analytics-panel card">
            <div class="analytics-panel-header"><div><h5 class="analytics-panel-title">平均耗时趋势</h5><div class="analytics-panel-subtitle">总耗时与首字耗时双指标</div></div></div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('durationTrend', el as HTMLElement | null)" class="chart-body" /></div>
          </section>

          <section class="analytics-panel card">
            <div class="analytics-panel-header"><div><h5 class="analytics-panel-title">回退触发趋势</h5><div class="analytics-panel-subtitle">观察 fallback 频率和占比</div></div></div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('fallbackTrend', el as HTMLElement | null)" class="chart-body" /></div>
          </section>

          <section class="analytics-panel card">
            <div class="analytics-panel-header"><div><h5 class="analytics-panel-title">站点请求分布</h5><div class="analytics-panel-subtitle">各站点请求量对比</div></div></div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('siteDist', el as HTMLElement | null)" class="chart-body" /></div>
          </section>

          <section class="analytics-panel card">
            <div class="analytics-panel-header"><div><h5 class="analytics-panel-title">模型调用分布</h5><div class="analytics-panel-subtitle">按调用次数排序的模型 Token 用量</div></div></div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('modelDist', el as HTMLElement | null)" class="chart-body" /></div>
          </section>
        </div>

        <section class="analytics-analysis-panel analytics-panel card">
          <div class="analytics-panel-header analytics-analysis-header">
            <div>
              <h5 class="analytics-panel-title">细分分析</h5>
              <div class="analytics-panel-subtitle">切换维度后查看独立图表，点击可筛选来源、Access Key 和协议</div>
            </div>
          </div>
          <div class="analytics-analysis-tabs" role="tablist" aria-label="细分分析维度">
            <button
              v-for="tab in ANALYTICS_ANALYSIS_TABS"
              :key="tab.key"
              type="button"
              role="tab"
              :aria-selected="activeAnalysisDimension === tab.key"
              :class="['analytics-analysis-tab', { active: activeAnalysisDimension === tab.key }]"
              @click="activeAnalysisDimension = tab.key"
            >
              {{ tab.label }}
            </button>
          </div>

          <div class="analytics-analysis-content">
            <div class="analytics-analysis-heading">
              <div>
                <h6>{{ analysisTitle }}</h6>
                <p>{{ analysisSubtitle }}</p>
              </div>
              <span class="analytics-analysis-range">{{ filterSummary }}</span>
            </div>

            <div v-if="activeAnalysisHasData" class="analytics-analysis-metrics">
              <article v-for="metric in analysisMetricCards" :key="metric.label" class="analytics-analysis-metric">
                <span>{{ metric.label }}</span>
                <strong :class="metric.tone">{{ metric.value }}</strong>
              </article>
            </div>

            <div v-if="activeAnalysisHasData" class="analytics-analysis-chart-grid">
              <div class="analytics-analysis-chart-card">
                <div class="analytics-analysis-chart-title">{{ activeAnalysisDimension === 'fallbackChain' ? '回退路径' : activeAnalysisDimension === 'latencyPercentiles' ? '分位数对比' : '成功 / 失败构成' }}</div>
                <div class="analytics-analysis-chart-body"><div :ref="(el) => setEl('analysisOverview', el as HTMLElement | null)" class="chart-body" /></div>
              </div>
              <div class="analytics-analysis-chart-card">
                <div class="analytics-analysis-chart-title">{{ activeAnalysisDimension === 'fallbackChain' ? '链路请求与成功率' : activeAnalysisDimension === 'latencyPercentiles' ? '样本数量' : 'Token 与平均耗时' }}</div>
                <div class="analytics-analysis-chart-body"><div :ref="(el) => setEl('analysisMetrics', el as HTMLElement | null)" class="chart-body" /></div>
              </div>
            </div>
            <NEmpty v-else description="当前维度暂无可视化数据" size="small" class="analytics-analysis-empty" />

            <details class="analytics-analysis-details">
              <summary>查看明细数据</summary>
              <div v-if="activeAnalysisDimension === 'fallbackChain'" class="analytics-analysis-table-wrap">
                <NDataTable
                  v-if="fallbackChainRows.length > 0"
                  :columns="fallbackChainColumns"
                  :data="fallbackChainRows"
                  :single-line="false"
                  :scroll-x="760"
                />
                <NEmpty v-else description="暂无回退链路数据" size="small" />
              </div>
              <div v-else-if="activeAnalysisDimension === 'latencyPercentiles'" class="analytics-analysis-table-wrap">
                <NDataTable
                  v-if="latencyRows.length > 0"
                  :columns="latencyColumns"
                  :data="latencyRows"
                  :single-line="false"
                  :scroll-x="620"
                />
                <NEmpty v-else description="暂无延迟分位数数据" size="small" />
              </div>
              <div v-else class="analytics-analysis-table-wrap">
                <NDataTable
                  v-if="activeBreakdownRows.length > 0"
                  :columns="breakdownColumns"
                  :data="activeBreakdownRows"
                  :row-props="breakdownRowProps"
                  :single-line="false"
                  :scroll-x="900"
                />
                <NEmpty v-else description="暂无细分数据" size="small" />
              </div>
            </details>
          </div>
        </section>
      </template>
    </NSpin>
  </div>
</template>

<style scoped>
.analytics-page {
  min-width: 0;
}

.analytics-filter {
  margin-bottom: 24px;
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  background: var(--bg-card);
}

.analytics-filter-body {
  padding: 18px 20px;
}

.analytics-filter-grid {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: 16px;
  align-items: end;
}

.analytics-filter-field {
  display: flex;
  min-width: 0;
  flex-direction: column;
  gap: 8px;
}

.form-label {
  margin: 0;
  color: var(--text-primary);
  font-size: 13px;
  font-weight: 600;
}

.analytics-active-filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 14px;
}

.analytics-active-filters-label {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.analytics-filter-meta {
  display: flex;
  grid-column: 1 / span 1;
  align-items: center;
  min-height: 20px;
  color: var(--text-color-secondary);
  font-size: 13px;
  line-height: 1.5;
}

.analytics-custom-range-row {
  display: grid;
  grid-template-columns: repeat(2, minmax(220px, 1fr)) minmax(160px, auto) minmax(240px, 1.2fr);
  gap: 16px;
  align-items: end;
  margin-top: 16px;
}

.analytics-range-tip {
  color: var(--text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}

.analytics-query-status {
  margin-top: 14px;
  padding: 9px 12px;
  border-radius: 8px;
  background: rgba(59, 130, 246, 0.08);
  color: #2563eb;
  font-size: 13px;
}

.analytics-query-status.pending {
  background: rgba(245, 158, 11, 0.1);
  color: #b45309;
}

.analytics-kpi-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 16px;
}

.analytics-kpi-card {
  min-width: 0;
  padding: 18px 20px;
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  background: var(--bg-card);
  box-shadow: 0 8px 20px rgba(15, 23, 42, 0.04);
}

.analytics-kpi-label {
  display: block;
  margin-bottom: 8px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.analytics-kpi-value {
  display: block;
  color: var(--text-primary);
  font-size: 28px;
  font-weight: 700;
  line-height: 1.1;
}

.analytics-kpi-value.compact {
  overflow: hidden;
  font-size: 22px;
  letter-spacing: -0.02em;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.analytics-kpi-value.success { color: #099268; }
.analytics-kpi-value.danger { color: #e03131; }
.analytics-kpi-value.warning { color: #d97706; }

.analytics-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 18px;
}

.analytics-panel {
  overflow: hidden;
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  background: var(--bg-card);
}

.analytics-panel-wide {
  grid-column: span 2;
}

.analytics-panel-header {
  padding: 18px 20px 0;
}

.analytics-panel-title {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
}

.analytics-panel-subtitle {
  margin-top: 4px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.analytics-chart-body {
  padding: 12px 16px 16px;
  height: 320px;
}

.chart-body {
  width: 100%;
  height: 100%;
}

.analytics-analysis-panel {
  margin-top: 18px;
}

.analytics-analysis-header {
  padding-bottom: 14px;
}

.analytics-analysis-tabs {
  display: flex;
  gap: 4px;
  overflow-x: auto;
  padding: 0 20px 12px;
  border-bottom: 1px solid var(--border-color-global);
  scrollbar-width: thin;
}

.analytics-analysis-tab {
  flex: 0 0 auto;
  padding: 8px 12px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text-color-secondary);
  cursor: pointer;
  font: inherit;
  font-size: 13px;
  white-space: nowrap;
}

.analytics-analysis-tab:hover,
.analytics-analysis-tab.active {
  background: rgba(59, 130, 246, 0.1);
  color: var(--primary-color, #3b82f6);
}

.analytics-analysis-content {
  padding: 20px;
}

.analytics-analysis-heading {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 16px;
}

.analytics-analysis-heading h6 {
  margin: 0;
  color: var(--text-primary);
  font-size: 18px;
  font-weight: 700;
}

.analytics-analysis-heading p {
  max-width: 760px;
  margin: 6px 0 0;
  color: var(--text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}

.analytics-analysis-range {
  flex: 0 0 auto;
  color: var(--text-color-secondary);
  font-size: 12px;
  white-space: nowrap;
}

.analytics-analysis-metrics {
  display: grid;
  grid-template-columns: repeat(6, minmax(0, 1fr));
  gap: 10px;
  margin-bottom: 16px;
}

.analytics-analysis-metric {
  min-width: 0;
  padding: 12px 14px;
  border: 1px solid var(--border-color-global);
  border-radius: 12px;
  background: var(--bg-color-secondary, rgba(148, 163, 184, 0.06));
}

.analytics-analysis-metric span {
  display: block;
  overflow: hidden;
  margin-bottom: 6px;
  color: var(--text-color-secondary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.analytics-analysis-metric strong {
  display: block;
  overflow: hidden;
  color: var(--text-primary);
  font-size: 20px;
  line-height: 1.2;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.analytics-analysis-metric strong.success { color: #099268; }
.analytics-analysis-metric strong.danger { color: #e03131; }
.analytics-analysis-metric strong.warning { color: #d97706; }

.analytics-analysis-chart-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 14px;
}

.analytics-analysis-chart-card {
  min-width: 0;
  border: 1px solid var(--border-color-global);
  border-radius: 14px;
  background: var(--bg-color-secondary, rgba(148, 163, 184, 0.04));
}

.analytics-analysis-chart-title {
  padding: 14px 16px 0;
  color: var(--text-primary);
  font-size: 14px;
  font-weight: 700;
}

.analytics-analysis-chart-body {
  height: 330px;
  padding: 8px 12px 12px;
}

.analytics-analysis-empty {
  padding: 48px 0;
}

.analytics-analysis-details {
  margin-top: 16px;
  border-top: 1px solid var(--border-color-global);
}

.analytics-analysis-details summary {
  padding-top: 14px;
  color: var(--text-color-secondary);
  cursor: pointer;
  font-size: 13px;
  user-select: none;
}

.analytics-analysis-table-wrap {
  min-width: 0;
  overflow-x: auto;
  padding: 12px 16px 16px;
}

.analytics-analysis-table-wrap :deep(.n-data-table) {
  min-width: 620px;
}

.analytics-analysis-table-wrap :deep(.analytics-analysis-clickable) {
  cursor: pointer;
}

.analytics-analysis-table-wrap :deep(.analytics-analysis-clickable:hover td) {
  background: rgba(59, 130, 246, 0.08);
}

.analytics-empty {
  padding: 80px 0;
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  background: var(--bg-card);
}

@media (max-width: 1280px) {
  .analytics-filter-grid {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }
}

@media (max-width: 1200px) {
  .analytics-kpi-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .analytics-analysis-metrics {
    grid-template-columns: repeat(3, minmax(0, 1fr));
  }

  .analytics-kpi-value.compact {
    font-size: 20px;
  }
}

@media (max-width: 991px) {
  .analytics-grid {
    grid-template-columns: 1fr;
  }

  .analytics-panel-wide {
    grid-column: auto;
  }

  .analytics-analysis-chart-grid {
    grid-template-columns: 1fr;
  }

  .analytics-custom-range-row {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 767px) {
  .analytics-active-filters {
    flex-wrap: nowrap;
    overflow-x: auto;
    white-space: nowrap;
  }

  .analytics-analysis-content {
    padding: 16px;
  }

  .analytics-analysis-heading {
    flex-direction: column;
    gap: 6px;
  }

  .analytics-analysis-range {
    white-space: normal;
  }

  .analytics-analysis-metrics {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .analytics-analysis-chart-body {
    height: 280px;
  }

  .analytics-filter-grid,
  .analytics-kpi-grid {
    grid-template-columns: 1fr;
  }

  .analytics-chart-body {
    height: 280px;
  }
}
</style>
