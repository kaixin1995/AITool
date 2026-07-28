<script setup lang="ts">
import { computed, h, onMounted, ref, watch } from 'vue'
import { NCard, NButton, NSpace, NSelect, NTag, NStatistic, NPopconfirm, NEmpty, useMessage, type DataTableColumns } from 'naive-ui'
import * as api from '@/api/modelHealth'
import type { ModelHealthMonitoredModel } from '@/api/modelHealth'
import PageHeader from '@/components/PageHeader.vue'

const message = useMessage()
const loading = ref(false)
const monitored = ref<ModelHealthMonitoredModel[]>([])
const availableModels = ref<{ id: string; displayName: string }[]>([])
const range = ref('7d')
const rangeOptions = ref<Array<{ value: string; label: string }>>([])

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.getModelHealthDashboard(range.value)
    monitored.value = resp.monitoredModels
    availableModels.value = resp.availableModels
    rangeOptions.value = resp.rangeOptions
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
.timeline-seg { flex: 1; min-width: 4px; border-radius: 2px; }
.timeline-seg.success { background: #18a058; }
.timeline-seg.fail { background: #d03050; }
</style>
