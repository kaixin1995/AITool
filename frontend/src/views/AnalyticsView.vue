<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { echarts, initChart as initThemedChart, type ECharts } from '@/composables/useEcharts'
import { NCard, NSelect, NDatePicker, NButton, NEmpty, NSpin, type SelectOption } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import { formatCompact, formatDuration } from '@/composables/useFormat'
import * as api from '@/api/analytics'
import type {
  AnalyticsBusyResult,
  AnalyticsDashboard,
  AnalyticsDashboardResponse,
  AnalyticsFilterOptions,
  AnalyticsPendingResult
} from '@/api/analytics'

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
const siteId = ref<string | null>(null)
const accessKeyId = ref<string | null>(null)
const startTime = ref<number | null>(null)
const endTime = ref<number | null>(null)

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
 type ChartKey = 'requestTrend' | 'resultTrend' | 'tokenTrend' | 'durationTrend' | 'fallbackTrend' | 'cacheRatio' | 'siteDist' | 'modelDist'
const chartEls = ref<Record<ChartKey, HTMLElement | null>>({
  requestTrend: null, resultTrend: null, tokenTrend: null, durationTrend: null,
  fallbackTrend: null, cacheRatio: null, siteDist: null, modelDist: null
})
const charts = shallowRef<Record<ChartKey, ECharts | null>>({
  requestTrend: null, resultTrend: null, tokenTrend: null, durationTrend: null,
  fallbackTrend: null, cacheRatio: null, siteDist: null, modelDist: null
})

const summary = computed(() => dashboard.value?.summary)
const totalTokens = computed(() => summary.value?.totalTokens ?? ((summary.value?.totalInputTokens ?? 0) + (summary.value?.totalCachedTokens ?? 0) + (summary.value?.totalOutputTokens ?? 0)))
const tokenSplit = computed(() => `${formatCompact((summary.value?.totalInputTokens ?? 0) + (summary.value?.totalCachedTokens ?? 0))} / ${formatCompact(summary.value?.totalOutputTokens ?? 0)}`)
const filterSummary = computed(() => {
  const applied = dashboard.value?.appliedFilter
  const appliedRange = applied?.rangeType ?? rangeType.value
  const appliedBucket = applied?.bucketType ?? bucketType.value
  const appliedProtocol = applied?.protocolType ?? protocolType.value
  const appliedModel = applied?.modelName ?? modelName.value
  const appliedSiteId = applied?.siteId ?? siteId.value
  const appliedAccessKeyId = applied?.accessKeyId ?? accessKeyId.value
  const parts = [
    rangeOptions.find((option) => option.value === appliedRange)?.label ?? appliedRange,
    bucketOptions.find((option) => option.value === appliedBucket)?.label ?? appliedBucket
  ]
  if (appliedProtocol !== 'all') parts.push(appliedProtocol)
  if (appliedModel !== 'all') parts.push(appliedModel)
  if (appliedSiteId) {
    parts.push(filterOptions.value?.sites.find((site) => site.siteId === appliedSiteId)?.siteName ?? appliedSiteId)
  }
  if (appliedAccessKeyId) {
    parts.push(filterOptions.value?.accessKeys.find((key) => key.accessKeyId === appliedAccessKeyId)?.accessKeyLabel ?? appliedAccessKeyId)
  }
  return parts.filter(Boolean).join(' · ')
})

function setEl(key: ChartKey, el: HTMLElement | null) {
  chartEls.value[key] = el
}

async function loadFilters(): Promise<void> {
  try {
    filterOptions.value = await api.getAnalyticsOptions()
  } catch {
    // 筛选项加载失败不阻塞主数据
  }
}

function buildParams(): Record<string, unknown> {
  const params: Record<string, unknown> = {
    rangeType: rangeType.value,
    bucketType: bucketType.value,
    protocolType: protocolType.value,
    modelName: modelName.value
  }
  if (siteId.value) params.siteId = siteId.value
  if (accessKeyId.value) params.accessKeyId = accessKeyId.value
  if (rangeType.value === 'custom') {
    if (startTime.value) params.startTime = new Date(startTime.value).toISOString()
    if (endTime.value) params.endTime = new Date(endTime.value).toISOString()
  }
  return params
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
    if (!controller.signal.aborted) throw error
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

const PRIMARY = '#3b82f6'
const SUCCESS = '#10b981'
const WARNING = '#f59e0b'
const DANGER = '#ef4444'
const CACHED = '#6366f1'

function initChart(key: ChartKey): ECharts | null {
  const el = chartEls.value[key]
  if (!el) return null
  // 用主题感知的初始化（暗色模式注册了 aitool-dark 主题）
  if (!charts.value[key]) charts.value[key] = initThemedChart(el)
  return charts.value[key]
}

function renderCharts(): void {
  const d = dashboard.value
  if (!d) return

  const tokenAxisLabel = { formatter: (value: number) => formatCompact(value) }
  const durationAxisLabel = { formatter: (value: number) => formatDuration(value) }

  const c1 = initChart('requestTrend')
  if (c1 && d.requestTrend) {
    c1.setOption({
      tooltip: { trigger: 'axis' },
      grid: { left: 40, right: 20, top: 20, bottom: 30 },
      xAxis: { type: 'category', data: d.requestTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', minInterval: 1, axisLabel: tokenAxisLabel },
      series: [{ name: '请求数', type: 'line', smooth: true, data: d.requestTrend.map((t) => t.requestCount), areaStyle: { opacity: 0.15 }, itemStyle: { color: PRIMARY } }]
    }, true)
  }

  const c2 = initChart('resultTrend')
  if (c2 && d.resultTrend) {
    c2.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { data: ['成功', '失败', '成功率'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 40, right: 50, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.resultTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: [
        { type: 'value', minInterval: 1, axisLabel: tokenAxisLabel },
        { type: 'value', name: '%', position: 'right', min: 0, max: 100, axisLabel: { formatter: '{value}%' } }
      ],
      series: [
        { name: '成功', type: 'bar', stack: 'total', data: d.resultTrend.map((t) => t.successCount), itemStyle: { color: SUCCESS } },
        { name: '失败', type: 'bar', stack: 'total', data: d.resultTrend.map((t) => t.failCount), itemStyle: { color: DANGER } },
        { name: '成功率', type: 'line', yAxisIndex: 1, smooth: true, data: d.resultTrend.map((t) => t.successRate ?? 0), itemStyle: { color: PRIMARY } }
      ]
    }, true)
  }

  const c3 = initChart('tokenTrend')
  if (c3 && d.tokenTrend) {
    c3.setOption({
      tooltip: { trigger: 'axis', valueFormatter: (v: number | string) => formatCompact(Number(v)) },
      legend: { data: ['输入', '缓存', '输出'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 50, right: 20, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.tokenTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', axisLabel: tokenAxisLabel },
      series: [
        { name: '输入', type: 'line', smooth: true, data: d.tokenTrend.map((t) => t.inputTokens), itemStyle: { color: PRIMARY } },
        { name: '缓存', type: 'line', smooth: true, data: d.tokenTrend.map((t) => t.cachedTokens), itemStyle: { color: CACHED } },
        { name: '输出', type: 'line', smooth: true, data: d.tokenTrend.map((t) => t.outputTokens), itemStyle: { color: SUCCESS } }
      ]
    }, true)
  }

  const c4 = initChart('durationTrend')
  if (c4 && d.durationTrend) {
    c4.setOption({
      tooltip: { trigger: 'axis', valueFormatter: (v: number | string) => formatDuration(Number(v)) },
      legend: { data: ['总耗时', '首字耗时'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 50, right: 20, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.durationTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', axisLabel: durationAxisLabel },
      series: [
        { name: '总耗时', type: 'line', smooth: true, data: d.durationTrend.map((t) => t.averageTotalDurationMs), itemStyle: { color: PRIMARY } },
        { name: '首字耗时', type: 'line', smooth: true, data: d.durationTrend.map((t) => t.averageFirstTokenLatencyMs), itemStyle: { color: WARNING } }
      ]
    }, true)
  }

  const c5 = initChart('fallbackTrend')
  if (c5 && d.fallbackTrend) {
    c5.setOption({
      tooltip: { trigger: 'axis' },
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
    c6.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { data: ['缓存命中率', '缓存命中 Token'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 50, right: 58, top: 30, bottom: 60 },
      xAxis: { type: 'category', data: d.modelCacheRatioDistribution.map((t) => t.label), axisLabel: { fontSize: 10, rotate: 30, interval: 0 } },
      yAxis: [
        { type: 'value', max: 100, axisLabel: { formatter: '{value}%' } },
        { type: 'value', position: 'right', axisLabel: tokenAxisLabel }
      ],
      series: [
        { name: '缓存命中率', type: 'bar', data: d.modelCacheRatioDistribution.map((t) => t.cacheHitRate), itemStyle: { color: CACHED }, barMaxWidth: 32 },
        { name: '缓存命中 Token', type: 'line', yAxisIndex: 1, smooth: true, data: d.modelCacheRatioDistribution.map((t) => t.cachedTokens), itemStyle: { color: SUCCESS } }
      ]
    }, true)
  }

  const c7 = initChart('siteDist')
  if (c7 && d.siteDistribution) {
    const data = [...d.siteDistribution].sort((a, b) => b.requestCount - a.requestCount).slice(0, 10).reverse()
    c7.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      grid: { left: 10, right: 30, top: 20, bottom: 20, containLabel: true },
      xAxis: { type: 'value', minInterval: 1, axisLabel: tokenAxisLabel },
      yAxis: { type: 'category', data: data.map((t) => t.label), axisLabel: { fontSize: 10 } },
      series: [{ name: '请求数', type: 'bar', data: data.map((t) => t.requestCount), itemStyle: { color: PRIMARY }, barMaxWidth: 20 }]
    }, true)
  }

  const c8 = initChart('modelDist')
  if (c8 && d.modelDistribution) {
    const data = [...d.modelDistribution].sort((a, b) => (b.totalTokens ?? 0) - (a.totalTokens ?? 0)).slice(0, 10).reverse()
    c8.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      grid: { left: 10, right: 30, top: 20, bottom: 20, containLabel: true },
      xAxis: { type: 'value', axisLabel: tokenAxisLabel },
      yAxis: { type: 'category', data: data.map((t) => t.label), axisLabel: { fontSize: 10 } },
      series: [{ name: 'Token 用量', type: 'bar', data: data.map((t) => t.totalTokens ?? 0), itemStyle: { color: CACHED }, barMaxWidth: 20 }]
    }, true)
  }
}

function handleResize(): void {
  (Object.keys(charts.value) as ChartKey[]).forEach((k) => charts.value[k]?.resize())
}

onMounted(async () => {
  await Promise.all([loadFilters(), load()])
  window.addEventListener('resize', handleResize)
})
onUnmounted(() => {
  loadController?.abort()
  window.removeEventListener('resize', handleResize)
  ;(Object.keys(charts.value) as ChartKey[]).forEach((k) => charts.value[k]?.dispose())
})

// 筛选条件变化时自动查询（自定义范围需两个时间都填）
watch([rangeType, bucketType, protocolType, modelName, siteId, accessKeyId], () => load())
watch([startTime, endTime], () => {
  if (rangeType.value !== 'custom' || (startTime.value && endTime.value)) load()
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

      <NEmpty v-if="!loading && !dashboard" description="暂无统计数据" class="analytics-empty" />

      <template v-if="summary">
        <section class="analytics-kpi-grid mb-4">
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">总请求数</span>
            <strong class="analytics-kpi-value">{{ formatCompact(summary.totalRequests) }}</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">成功率</span>
            <strong class="analytics-kpi-value success">{{ summary.successRate ?? 0 }}%</strong>
          </article>
          <article class="analytics-kpi-card">
            <span class="analytics-kpi-label">失败率</span>
            <strong class="analytics-kpi-value danger">{{ summary.failureRate ?? 0 }}%</strong>
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
            <div class="analytics-panel-header"><div><h5 class="analytics-panel-title">模型调用分布</h5><div class="analytics-panel-subtitle">Top 模型调用热度与用量</div></div></div>
            <div class="analytics-chart-body"><div :ref="(el) => setEl('modelDist', el as HTMLElement | null)" class="chart-body" /></div>
          </section>
        </div>
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
  background: linear-gradient(180deg, #ffffff 0%, #f8fbff 100%);
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

.analytics-empty {
  padding: 80px 0;
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  background: var(--bg-card);
}

[data-theme='dark'] .analytics-kpi-card {
  background: linear-gradient(180deg, rgba(31, 41, 55, 0.95) 0%, rgba(17, 24, 39, 0.95) 100%);
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

  .analytics-custom-range-row {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 767px) {
  .analytics-filter-grid,
  .analytics-kpi-grid {
    grid-template-columns: 1fr;
  }

  .analytics-chart-body {
    height: 280px;
  }
}
</style>
