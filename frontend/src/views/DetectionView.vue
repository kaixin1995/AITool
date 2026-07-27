<script setup lang="ts">
import { computed, h, onMounted, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NEmpty, NPopconfirm, useMessage, type DataTableColumns } from 'naive-ui'
import * as api from '@/api/detection'
import type { DetectionModelGroup, DetectionSiteStatus } from '@/api/detection'

const message = useMessage()
const loading = ref(false)
const groups = ref<DetectionModelGroup[]>([])
const expandedKeys = ref<string[]>([])

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.getDetectionMatrix()
    groups.value = resp.modelGroups
    expandedKeys.value = resp.modelGroups.map((g) => g.modelLibraryItemId)
  } finally { loading.value = false }
}

async function handleProbe(mappingId: string): Promise<void> {
  try {
    const result = await api.probeMapping(mappingId)
    message.success(`探测完成：${result.status}（${result.durationMs}ms）`)
    await load()
  } catch (e) {
    message.error((e as Error).message)
  }
}

async function handleProbeModel(modelId: string): Promise<void> {
  try {
    const resp = await api.probeModel(modelId)
    message.info(`已提交批量探测任务 ${resp.taskId}`)
    setTimeout(load, 3000)
  } catch (e) { message.error((e as Error).message) }
}

function statusTag(status: string) {
  const type = status === 'success' ? 'success' : status === 'fail' ? 'error' : 'default'
  return h(NTag, { size: 'small', type, bordered: false }, () => status || '未知')
}

onMounted(load)
</script>

<template>
  <div class="page-container">
    <NCard>
      <template #header>
        <NSpace justify="space-between" align="center">
          <span>模型检测（{{ groups.length }} 个模型）</span>
          <NButton @click="load">刷新</NButton>
        </NSpace>
      </template>
      <NEmpty v-if="!loading && groups.length === 0" description="暂无模型映射" />
      <NSpace v-else vertical :size="12">
        <NCard v-for="g in groups" :key="g.modelLibraryItemId" size="small">
          <template #header>
            <NSpace align="center" :size="8">
              <span style="font-weight: 600">{{ g.displayName }}</span>
              <NTag size="tiny" :bordered="false">{{ g.sites.length }} 站点</NTag>
              <NButton size="tiny" quaternary type="primary" @click="handleProbeModel(g.modelLibraryItemId)">全部探测</NButton>
            </NSpace>
          </template>
          <NSpace vertical :size="6">
            <div v-for="s in g.sites" :key="s.mappingId" class="detection-row">
              <NSpace align="center" :size="8" style="flex: 1">
                <span style="min-width: 140px">{{ s.siteName }}</span>
                <NTag size="small" :bordered="false">{{ s.remoteModelName }}</NTag>
                <component :is="statusTag(s.lastStatus)" />
                <span v-if="s.lastDurationMs" style="font-size: 12px; color: #888">{{ s.lastDurationMs }}ms</span>
                <span v-if="s.lastCheckedAt" style="font-size: 12px; color: #888">{{ new Date(s.lastCheckedAt).toLocaleString('zh-CN') }}</span>
              </NSpace>
              <NButton size="tiny" quaternary @click="handleProbe(s.mappingId)">探测</NButton>
            </div>
          </NSpace>
        </NCard>
      </NSpace>
    </NCard>
  </div>
</template>

<style scoped>
.detection-row { display: flex; align-items: center; justify-content: space-between; padding: 4px 0; }
</style>
