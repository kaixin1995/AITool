<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { NCard, NButton, NSpace, NSelect, NTag, NStatistic, NPopconfirm, NEmpty, useMessage, type DataTableColumns } from 'naive-ui'
import * as api from '@/api/modelHealth'
import type { ModelHealthMonitoredModel, ModelHealthDashboard } from '@/api/modelHealth'
import PageHeader from '@/components/PageHeader.vue'

const message = useMessage()
const loading = ref(false)
const monitored = ref<ModelHealthMonitoredModel[]>([])
const availableModels = ref<{ id: string; displayName: string }[]>([])
const range = ref('7d')
const rangeOptions = ref<Array<{ value: string; label: string }>>([])
// 每个模型按站点维度的健康明细（原 Razor 页有 per-site 卡片，这里保留数据供渲染）。
const healthData = ref<ModelHealthDashboard['healthData']>({})

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.getModelHealthDashboard(range.value)
    monitored.value = resp.monitoredModels
    availableModels.value = resp.availableModels
    rangeOptions.value = resp.rangeOptions
    healthData.value = resp.healthData ?? {}
  } finally { loading.value = false }
}

async function handleAdd(modelId: string): Promise<void> {
  await api.addMonitor(modelId)
  message.success('已加入监控')
  await load()
}
async function handleRemove(m: ModelHealthMonitoredModel): Promise<void> {
  await api.removeMonitor(m.modelLibraryItemId)
  message.success('已移除监控')
  await load()
}

function statusColor(rate: number): 'success' | 'warning' | 'error' {
  if (rate >= 0.95) return 'success'
  if (rate >= 0.8) return 'warning'
  return 'error'
}

watch(range, load)
onMounted(load)
</script>

<template>
  <div class="page-container">
    <PageHeader title="模型健康看板" subtitle="监控指定模型在各站点的健康状态和检测历史">
      <template #actions>
        <NSelect v-model:value="range" :options="rangeOptions" style="width: 140px" />
        <NButton @click="load">刷新</NButton>
      </template>
    </PageHeader>
    <NCard>
      <NEmpty v-if="!loading && monitored.length === 0" description="暂无监控模型，从下方添加" />

      <NSpace vertical :size="12">
        <NCard v-for="m in monitored" :key="m.modelLibraryItemId" size="small">
          <template #header>
            <NSpace align="center" :size="8">
              <span style="font-weight: 600">{{ m.displayName }}</span>
              <NTag size="tiny" :type="statusColor(m.averageSuccessRate)" :bordered="false">
                {{ (m.averageSuccessRate * 100).toFixed(1) }}%
              </NTag>
              <NPopconfirm @positive-click="handleRemove(m)">
                <template #trigger><NButton size="tiny" quaternary type="error">移除监控</NButton></template>
                移除该模型的健康监控？
              </NPopconfirm>
            </NSpace>
          </template>
          <div style="display: grid; grid-template-columns: repeat(auto-fit, minmax(120px, 1fr)); gap: 12px; margin-bottom: 12px">
            <NStatistic label="站点数" :value="m.siteCount" />
            <NStatistic label="健康站点" :value="m.healthySiteCount" />
            <NStatistic label="异常站点" :value="m.unhealthySiteCount" />
            <NStatistic label="平均耗时(ms)" :value="m.averageDurationMs ?? 0" />
          </div>
          <div class="timeline">
            <span v-for="(seg, idx) in m.timelineSegments" :key="idx"
              :class="['timeline-seg', seg.status]"
              :title="`${new Date(seg.startAt).toLocaleString('zh-CN')} · 成功${seg.successCount}/失败${seg.failureCount}`"
            />
          </div>

          <!-- 按站点维度的健康明细（对齐原 Razor 页 per-site 卡片） -->
          <div v-if="healthData[m.modelLibraryItemId]?.length" class="site-detail">
            <div v-for="(site, sIdx) in healthData[m.modelLibraryItemId]" :key="sIdx" class="site-row">
              <NSpace align="center" :size="8" style="flex: 1">
                <span style="min-width: 120px; font-size: 13px">{{ site.siteName }}</span>
                <NTag size="tiny" :bordered="false">{{ site.remoteModelName }}</NTag>
                <NTag size="tiny" :type="site.lastStatus === 'success' ? 'success' : site.lastStatus === 'fail' ? 'error' : 'default'" :bordered="false">
                  {{ site.lastStatus || '未知' }}
                </NTag>
                <span style="font-size: 12px; color: var(--text-color-secondary)">成功率 {{ (site.successRate * 100).toFixed(1) }}%</span>
              </NSpace>
              <div class="timeline site-timeline">
                <span v-for="(seg, segIdx) in site.timelineSegments" :key="segIdx"
                  :class="['timeline-seg', seg.status]"
                  :title="`${new Date(seg.startAt).toLocaleString('zh-CN')}`"
                />
              </div>
            </div>
          </div>
        </NCard>
      </NSpace>

      <div v-if="availableModels.length > 0" style="margin-top: 24px">
        <h4 style="margin: 0 0 12px; color: var(--text-color-secondary)">可添加监控的模型</h4>
        <NSpace>
          <NButton v-for="am in availableModels" :key="am.id" size="small" quaternary type="primary" @click="handleAdd(am.id)">
            + {{ am.displayName }}
          </NButton>
        </NSpace>
      </div>
    </NCard>
  </div>
</template>

<style scoped>
.timeline { display: flex; gap: 2px; height: 24px; }
.timeline-seg { flex: 1; min-width: 4px; border-radius: 2px; background: #d4d4d8; }
.timeline-seg.success { background: #18a058; }
.timeline-seg.fail { background: #d03050; }
.site-detail { margin-top: 12px; display: flex; flex-direction: column; gap: 8px; }
.site-row { display: flex; align-items: center; gap: 12px; padding: 6px 0; border-top: 1px solid var(--border-color-global); flex-wrap: nowrap; }
.site-row > div:first-child { flex-shrink: 0; min-width: 200px; }
.site-timeline { height: 16px; flex: 1; min-width: 100px; }
[data-theme='dark'] .timeline-seg { background: #3a3a40; }
</style>
