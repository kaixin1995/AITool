<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import * as echarts from 'echarts'
import { NCard, NSpace, NSelect, NDatePicker, NButton, NStatistic, NEmpty, NSpin } from 'naive-ui'
import * as api from '@/api/analytics'
import type { AnalyticsDashboard } from '@/api/analytics'

const loading = ref(false)
const dashboard = ref<AnalyticsDashboard | null>(null)
const rangeType = ref('week')
const startTime = ref<number | null>(null)
const endTime = ref<number | null>(null)

const trendChartEl = ref<HTMLElement | null>(null)
const modelChartEl = ref<HTMLElement | null>(null)
const trendChart = shallowRef<echarts.ECharts | null>(null)
const modelChart = shallowRef<echarts.ECharts | null>(null)

const rangeOptions = [
  { label: '今天', value: 'day' }, { label: '本周', value: 'week' },
  { label: '本月', value: 'month' }, { label: '自定义', value: 'custom' }, { label: '全部', value: 'all' }
]

async function load(): Promise<void> {
  loading.value = true
  try {
    const params: Record<string, unknown> = { rangeType: rangeType.value }
    if (rangeType.value === 'custom') {
      if (startTime.value) params.startTime = new Date(startTime.value).toISOString()
      if (endTime.value) params.endTime = new Date(endTime.value).toISOString()
    }
    dashboard.value = await api.getAnalyticsDashboard(params)
    renderCharts()
  } finally { loading.value = false }
}

function renderCharts(): void {
  const d = dashboard.value
  if (!d) return
  if (trendChartEl.value && d.trends) {
    if (!trendChart.value) trendChart.value = echarts.init(trendChartEl.value)
    trendChart.value.setOption({
      tooltip: { trigger: 'axis' },
      xAxis: { type: 'category', data: d.trends.map((t) => t.date) },
      yAxis: { type: 'value' },
      series: [{ name: '请求数', type: 'line', smooth: true, data: d.trends.map((t) => t.count), areaStyle: {}, itemStyle: { color: '#6C9EFF' } }]
    })
  }
  if (modelChartEl.value && d.modelDistribution) {
    if (!modelChart.value) modelChart.value = echarts.init(modelChartEl.value)
    modelChart.value.setOption({
      tooltip: { trigger: 'item' },
      series: [{ type: 'pie', radius: ['40%', '70%'], data: d.modelDistribution.map((m) => ({ name: m.model, value: m.count })), itemStyle: { borderRadius: 6 } }]
    })
  }
}

function handleResize(): void {
  trendChart.value?.resize()
  modelChart.value?.resize()
}

onMounted(async () => {
  await load()
  window.addEventListener('resize', handleResize)
})
onUnmounted(() => {
  window.removeEventListener('resize', handleResize)
  trendChart.value?.dispose()
  modelChart.value?.dispose()
})

watch([rangeType, startTime, endTime], () => {
  if (rangeType.value !== 'custom' || (startTime.value && endTime.value)) load()
})
</script>

<template>
  <div class="page-container">
    <NSpin :show="loading">
      <NCard>
        <template #header>
          <NSpace justify="space-between" align="center">
            <span>统计分析</span>
            <NSpace :size="12">
              <NSelect v-model:value="rangeType" :options="rangeOptions" style="width: 120px" />
              <template v-if="rangeType === 'custom'">
                <NDatePicker v-model:value="startTime" type="datetime" placeholder="开始" />
                <NDatePicker v-model:value="endTime" type="datetime" placeholder="结束" />
              </template>
              <NButton type="primary" @click="load">查询</NButton>
            </NSpace>
          </NSpace>
        </template>

        <div v-if="dashboard?.summary" style="display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 16px; margin-bottom: 24px">
          <NCard size="small"><NStatistic label="总请求" :value="dashboard.summary.totalRequests" /></NCard>
          <NCard size="small"><NStatistic label="成功" :value="dashboard.summary.successCount" /></NCard>
          <NCard size="small"><NStatistic label="失败" :value="dashboard.summary.failCount" /></NCard>
          <NCard size="small"><NStatistic label="输入 Token" :value="dashboard.summary.totalInputTokens" /></NCard>
          <NCard size="small"><NStatistic label="输出 Token" :value="dashboard.summary.totalOutputTokens" /></NCard>
          <NCard size="small"><NStatistic label="缓存 Token" :value="dashboard.summary.totalCachedTokens" /></NCard>
        </div>

        <div style="display: grid; grid-template-columns: 2fr 1fr; gap: 16px">
          <NCard title="请求趋势" size="small">
            <div ref="trendChartEl" style="height: 300px" />
            <NEmpty v-if="!dashboard?.trends?.length" description="暂无趋势数据" style="padding: 80px 0" />
          </NCard>
          <NCard title="模型分布" size="small">
            <div ref="modelChartEl" style="height: 300px" />
            <NEmpty v-if="!dashboard?.modelDistribution?.length" description="暂无分布数据" style="padding: 80px 0" />
          </NCard>
        </div>
      </NCard>
    </NSpin>
  </div>
</template>
