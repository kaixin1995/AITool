<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { echarts, type ECharts } from '@/composables/useEcharts'
import { NCard, NSpace, NSelect, NDatePicker, NButton, NStatistic, NEmpty, NSpin } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/analytics'
import type { AnalyticsDashboard, AnalyticsFilterOptions } from '@/api/analytics'

const loading = ref(false)
const dashboard = ref<AnalyticsDashboard | null>(null)
const filterOptions = ref<AnalyticsFilterOptions | null>(null)

// 筛选条件
const rangeType = ref('week')
const protocolType = ref('all')
const modelName = ref('all')
const siteId = ref<string | null>(null)
const accessKeyId = ref<string | null>(null)
const startTime = ref<number | null>(null)
const endTime = ref<number | null>(null)

const rangeOptions = [
  { label: '今天', value: 'day' }, { label: '本周', value: 'week' },
  { label: '本月', value: 'month' }, { label: '自定义', value: 'custom' }, { label: '全部', value: 'all' }
]
const protocolOptions = [
  { label: '全部协议', value: 'all' }, { label: 'OpenAI', value: 'openai' }, { label: 'Anthropic', value: 'anthropic' }
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

async function load(): Promise<void> {
  loading.value = true
  try {
    // analytics 后端是异步查询队列：首次请求返回 202 {status:"pending",retryAfterMs}，
    // 需要等待后重试拿真实结果。最多重试 5 次。
    let result = await api.getAnalyticsDashboard(buildParams())
    for (let i = 0; i < 5 && result && (result as { status?: string }).status === 'pending'; i++) {
      const retryAfter = (result as { retryAfterMs?: number }).retryAfterMs ?? 1500
      await new Promise((r) => setTimeout(r, retryAfter))
      result = await api.getAnalyticsDashboard(buildParams())
    }
    dashboard.value = result
    await nextTick()
    renderCharts()
  } finally {
    loading.value = false
  }
}

const PRIMARY = '#6C9EFF'
const SUCCESS = '#34D399'
const WARNING = '#FBBF24'
const DANGER = '#F87171'
const CACHED = '#A5B4FC'

function initChart(key: ChartKey): ECharts | null {
  const el = chartEls.value[key]
  if (!el) return null
  if (!charts.value[key]) charts.value[key] = echarts.init(el)
  return charts.value[key]
}

function renderCharts(): void {
  const d = dashboard.value
  if (!d) return

  // 1. 请求量趋势（折线）
  const c1 = initChart('requestTrend')
  if (c1 && d.requestTrend) {
    c1.setOption({
      tooltip: { trigger: 'axis' },
      grid: { left: 40, right: 20, top: 20, bottom: 30 },
      xAxis: { type: 'category', data: d.requestTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', minInterval: 1 },
      series: [{ name: '请求数', type: 'line', smooth: true, data: d.requestTrend.map((t) => t.requestCount), areaStyle: { opacity: 0.15 }, itemStyle: { color: PRIMARY } }]
    }, true)
  }

  // 2. 结果趋势（成功/失败堆叠柱）
  const c2 = initChart('resultTrend')
  if (c2 && d.resultTrend) {
    c2.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      legend: { data: ['成功', '失败'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 40, right: 20, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.resultTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        { name: '成功', type: 'bar', stack: 'total', data: d.resultTrend.map((t) => t.successCount), itemStyle: { color: SUCCESS } },
        { name: '失败', type: 'bar', stack: 'total', data: d.resultTrend.map((t) => t.failCount), itemStyle: { color: DANGER } }
      ]
    }, true)
  }

  // 3. Token 用量趋势（输入/缓存/输出折线）
  const c3 = initChart('tokenTrend')
  if (c3 && d.tokenTrend) {
    c3.setOption({
      tooltip: { trigger: 'axis' },
      legend: { data: ['输入', '缓存', '输出'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 50, right: 20, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.tokenTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value' },
      series: [
        { name: '输入', type: 'line', smooth: true, data: d.tokenTrend.map((t) => t.inputTokens), itemStyle: { color: PRIMARY } },
        { name: '缓存', type: 'line', smooth: true, data: d.tokenTrend.map((t) => t.cachedTokens), itemStyle: { color: CACHED } },
        { name: '输出', type: 'line', smooth: true, data: d.tokenTrend.map((t) => t.outputTokens), itemStyle: { color: SUCCESS } }
      ]
    }, true)
  }

  // 4. 耗时趋势（总耗时/首Token延迟折线）
  const c4 = initChart('durationTrend')
  if (c4 && d.durationTrend) {
    c4.setOption({
      tooltip: { trigger: 'axis', valueFormatter: (v: number | string) => `${v} ms` },
      legend: { data: ['总耗时', '首Token'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 50, right: 20, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.durationTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: { type: 'value', axisLabel: { formatter: '{value} ms' } },
      series: [
        { name: '总耗时', type: 'line', smooth: true, data: d.durationTrend.map((t) => t.averageTotalDurationMs), itemStyle: { color: PRIMARY } },
        { name: '首Token', type: 'line', smooth: true, data: d.durationTrend.map((t) => t.averageFirstTokenLatencyMs), itemStyle: { color: WARNING } }
      ]
    }, true)
  }

  // 5. 回退趋势（柱状 + 回退率折线，双 Y 轴）
  const c5 = initChart('fallbackTrend')
  if (c5 && d.fallbackTrend) {
    c5.setOption({
      tooltip: { trigger: 'axis' },
      legend: { data: ['回退次数', '回退率'], top: 0, textStyle: { fontSize: 11 } },
      grid: { left: 40, right: 50, top: 30, bottom: 30 },
      xAxis: { type: 'category', data: d.fallbackTrend.map((t) => t.label), axisLabel: { fontSize: 10 } },
      yAxis: [
        { type: 'value', name: '次数', position: 'left', minInterval: 1 },
        { type: 'value', name: '%', position: 'right', max: 100, axisLabel: { formatter: '{value}%' } }
      ],
      series: [
        { name: '回退次数', type: 'bar', data: d.fallbackTrend.map((t) => t.fallbackCount), itemStyle: { color: WARNING } },
        { name: '回退率', type: 'line', yAxisIndex: 1, smooth: true, data: d.fallbackTrend.map((t) => t.fallbackRate), itemStyle: { color: DANGER } }
      ]
    }, true)
  }

  // 6. 缓存命中率（模型维度柱状）
  const c6 = initChart('cacheRatio')
  if (c6 && d.modelCacheRatioDistribution) {
    c6.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      grid: { left: 50, right: 20, top: 20, bottom: 60 },
      xAxis: { type: 'category', data: d.modelCacheRatioDistribution.map((t) => t.label), axisLabel: { fontSize: 10, rotate: 30, interval: 0 } },
      yAxis: { type: 'value', max: 100, axisLabel: { formatter: '{value}%' } },
      series: [{ name: '缓存命中率', type: 'bar', data: d.modelCacheRatioDistribution.map((t) => t.cacheHitRate), itemStyle: { color: CACHED }, barMaxWidth: 32 }]
    }, true)
  }

  // 7. 站点请求分布（横向柱状 Top10）
  const c7 = initChart('siteDist')
  if (c7 && d.siteDistribution) {
    const data = [...d.siteDistribution].sort((a, b) => b.requestCount - a.requestCount).slice(0, 10).reverse()
    c7.setOption({
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
      grid: { left: 10, right: 30, top: 20, bottom: 20, containLabel: true },
      xAxis: { type: 'value', minInterval: 1 },
      yAxis: { type: 'category', data: data.map((t) => t.label), axisLabel: { fontSize: 10 } },
      series: [{ name: '请求数', type: 'bar', data: data.map((t) => t.requestCount), itemStyle: { color: PRIMARY }, barMaxWidth: 20 }]
    }, true)
  }

  // 8. 模型调用分布（饼图）
  const c8 = initChart('modelDist')
  if (c8 && d.modelDistribution) {
    c8.setOption({
      tooltip: { trigger: 'item', formatter: '{b}: {c} ({d}%)' },
      legend: { type: 'scroll', orient: 'vertical', right: 0, top: 'middle', textStyle: { fontSize: 10 } },
      series: [{
        type: 'pie', radius: ['40%', '70%'], center: ['40%', '50%'],
        data: d.modelDistribution.map((m) => ({ name: m.label, value: m.requestCount })),
        label: { show: false }, itemStyle: { borderRadius: 6, borderColor: '#fff', borderWidth: 2 }
      }]
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
  window.removeEventListener('resize', handleResize)
  ;(Object.keys(charts.value) as ChartKey[]).forEach((k) => charts.value[k]?.dispose())
})

// 筛选条件变化时自动查询（自定义范围需两个时间都填）
watch([rangeType, protocolType, modelName, siteId, accessKeyId], () => load())
watch([startTime, endTime], () => {
  if (rangeType.value !== 'custom' || (startTime.value && endTime.value)) load()
})
</script>

<template>
  <div class="page-container">
    <PageHeader title="可视化分析" subtitle="请求量、Token 用量、成功率、回退、缓存命中等趋势分析" />
    <NSpin :show="loading">
      <NCard>
        <template #header>
          <NSpace align="center" wrap :size="12">
            <NSelect v-model:value="rangeType" :options="rangeOptions" placeholder="时间范围" style="width: 120px" />
            <template v-if="rangeType === 'custom'">
              <NDatePicker v-model:value="startTime" type="datetime" placeholder="开始时间" />
              <NDatePicker v-model:value="endTime" type="datetime" placeholder="结束时间" />
            </template>
              <NSelect
                v-if="filterOptions"
                v-model:value="protocolType"
                :options="protocolOptions"
                placeholder="协议"
                style="width: 130px"
              />
              <NSelect
                v-if="filterOptions && filterOptions.sites.length"
                v-model:value="siteId"
                :options="filterOptions.sites.map((s) => ({ label: s.siteName, value: s.siteId }))"
                placeholder="站点"
                clearable
                style="width: 160px"
              />
              <NSelect
                v-if="filterOptions && filterOptions.models.length"
                v-model:value="modelName"
                :options="[{ label: '全部模型', value: 'all' }, ...filterOptions.models.map((m) => ({ label: m.modelName, value: m.modelName }))]"
                placeholder="模型"
                filterable
                style="width: 180px"
              />
              <NSelect
                v-if="filterOptions && filterOptions.accessKeys.length"
                v-model:value="accessKeyId"
                :options="filterOptions.accessKeys.map((k) => ({ label: k.accessKeyLabel, value: k.accessKeyId }))"
                placeholder="密钥"
                clearable
                style="width: 160px"
              />
              <NButton type="primary" @click="load">查询</NButton>
          </NSpace>
        </template>

        <NEmpty v-if="!loading && !dashboard" description="暂无统计数据" style="padding: 80px 0" />

        <template v-if="dashboard?.summary">
          <!-- 汇总 KPI 卡片 -->
          <div class="kpi-grid">
            <NCard size="small"><NStatistic label="总请求" :value="dashboard.summary.totalRequests" /></NCard>
            <NCard size="small"><NStatistic label="成功" :value="dashboard.summary.successRequests" /></NCard>
            <NCard size="small"><NStatistic label="失败" :value="dashboard.summary.failedRequests" /></NCard>
            <NCard size="small"><NStatistic label="成功率" :value="`${dashboard.summary.successRate ?? 0}%`" /></NCard>
            <NCard size="small"><NStatistic label="回退触发" :value="dashboard.summary.fallbackRequestCount ?? 0" /></NCard>
            <NCard size="small"><NStatistic label="输入 Token" :value="dashboard.summary.totalInputTokens" /></NCard>
            <NCard size="small"><NStatistic label="输出 Token" :value="dashboard.summary.totalOutputTokens" /></NCard>
            <NCard size="small"><NStatistic label="缓存 Token" :value="dashboard.summary.totalCachedTokens" /></NCard>
          </div>

          <!-- 图表网格 -->
          <div class="chart-grid">
            <NCard size="small">
              <template #header><span class="chart-title">请求量趋势</span></template>
              <div :ref="(el) => setEl('requestTrend', el as HTMLElement | null)" class="chart-body" />
            </NCard>
            <NCard size="small">
              <template #header><span class="chart-title">结果趋势（成功/失败）</span></template>
              <div :ref="(el) => setEl('resultTrend', el as HTMLElement | null)" class="chart-body" />
            </NCard>
            <NCard size="small">
              <template #header><span class="chart-title">Tokens 用量趋势</span></template>
              <div :ref="(el) => setEl('tokenTrend', el as HTMLElement | null)" class="chart-body" />
            </NCard>
            <NCard size="small">
              <template #header><span class="chart-title">耗时趋势</span></template>
              <div :ref="(el) => setEl('durationTrend', el as HTMLElement | null)" class="chart-body" />
            </NCard>
            <NCard size="small">
              <template #header><span class="chart-title">回退触发趋势</span></template>
              <div :ref="(el) => setEl('fallbackTrend', el as HTMLElement | null)" class="chart-body" />
            </NCard>
            <NCard size="small">
              <template #header><span class="chart-title">缓存命中率（按模型）</span></template>
              <div :ref="(el) => setEl('cacheRatio', el as HTMLElement | null)" class="chart-body" />
            </NCard>
            <NCard size="small">
              <template #header><span class="chart-title">站点请求分布（Top10）</span></template>
              <div :ref="(el) => setEl('siteDist', el as HTMLElement | null)" class="chart-body" />
            </NCard>
            <NCard size="small">
              <template #header><span class="chart-title">模型调用分布</span></template>
              <div :ref="(el) => setEl('modelDist', el as HTMLElement | null)" class="chart-body" />
            </NCard>
          </div>
        </template>
      </NCard>
    </NSpin>
  </div>
</template>

<style scoped>
.kpi-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 12px;
  margin-bottom: 20px;
}
.chart-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(420px, 1fr));
  gap: 16px;
}
.chart-title {
  font-size: 14px;
  font-weight: 600;
}
.chart-body {
  height: 280px;
  width: 100%;
}
</style>
