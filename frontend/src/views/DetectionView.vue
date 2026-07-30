<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NEmpty, NInput, NProgress, useMessage } from 'naive-ui'
import * as api from '@/api/detection'
import type { DetectionModelGroup, DetectionSiteStatus } from '@/api/detection'
import PageHeader from '@/components/PageHeader.vue'

const message = useMessage()
const loading = ref(false)
const groups = ref<DetectionModelGroup[]>([])
const modelKeyword = ref('')
const probeProgress = ref<{ taskId: string; total: number; completed: number; isCompleted: boolean } | null>(null)
let progressTimer: number | undefined

const filteredGroups = computed(() => {
  const keyword = modelKeyword.value.trim().toLowerCase()
  if (!keyword) return groups.value
  return groups.value.filter((g) => `${g.displayName} ${g.modelName}`.toLowerCase().includes(keyword))
})

const progressPercent = computed(() => {
  if (!probeProgress.value || probeProgress.value.total === 0) return 0
  return Math.round((probeProgress.value.completed / probeProgress.value.total) * 100)
})

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.getDetectionMatrix()
    groups.value = resp.modelGroups
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
    startProgressPolling(resp.taskId)
  } catch (e) { message.error((e as Error).message) }
}

async function handleProbeAll(): Promise<void> {
  try {
    const resp = await api.probeAll()
    message.info(`已提交全量探测任务 ${resp.taskId}`)
    startProgressPolling(resp.taskId)
  } catch (e) { message.error((e as Error).message) }
}

function startProgressPolling(taskId: string): void {
  if (progressTimer) window.clearInterval(progressTimer)
  probeProgress.value = { taskId, total: 0, completed: 0, isCompleted: false }
  void pollProgress()
  progressTimer = window.setInterval(() => { void pollProgress() }, 1200)
}

async function pollProgress(): Promise<void> {
  if (!probeProgress.value) return
  const progress = await api.getProbeProgress(probeProgress.value.taskId)
  probeProgress.value = progress
  if (progress.isCompleted) {
    if (progressTimer) window.clearInterval(progressTimer)
    progressTimer = undefined
    await load()
  }
}

// 状态 → NTag type 映射，模板里直接用 <NTag :type="statusType(...)">。
function statusType(status: string): 'success' | 'error' | 'default' {
  if (status === 'success') return 'success'
  if (status === 'fail') return 'error'
  return 'default'
}

onMounted(load)
onBeforeUnmount(() => {
  if (progressTimer) window.clearInterval(progressTimer)
})
</script>

<template>
  <div class="page-container">
    <PageHeader title="模型检测" subtitle="按模型分组查看各站点的可用性和响应状态">
      <template #actions>
        <NInput v-model:value="modelKeyword" clearable size="small" placeholder="搜索模型" style="width: 180px" />
        <NTag v-if="groups.length" round :bordered="false" size="small">{{ filteredGroups.length }} / {{ groups.length }} 个模型</NTag>
        <NButton size="small" type="primary" quaternary :disabled="groups.length === 0" @click="handleProbeAll">全部探测</NButton>
        <NButton size="small" @click="load">刷新</NButton>
      </template>
    </PageHeader>
    <NCard v-if="probeProgress" class="detection-progress-card" size="small">
      <div class="progress-title">批量探测进度：{{ probeProgress.completed }} / {{ probeProgress.total }}</div>
      <NProgress type="line" :percentage="progressPercent" :show-indicator="false" />
    </NCard>

    <NCard class="detection-matrix-card">
      <NEmpty v-if="!loading && filteredGroups.length === 0" description="暂无模型映射" />
      <NSpace v-else vertical :size="12">
        <NCard v-for="g in filteredGroups" :key="g.modelLibraryItemId" size="small" class="model-group-card">
          <template #header>
            <div class="model-group-header">
              <div class="model-group-title">
                <strong>{{ g.displayName }}</strong>
                <NTag size="tiny" :bordered="false">{{ g.modelName }}</NTag>
                <NTag size="tiny" :bordered="false">{{ g.sites.length }} 站点</NTag>
              </div>
              <NButton size="tiny" quaternary type="primary" @click="handleProbeModel(g.modelLibraryItemId)">探测此模型</NButton>
            </div>
          </template>
          <div class="detection-table">
            <div class="detection-table-head">
              <span>站点</span><span>站点模型</span><span>状态</span><span>耗时</span><span>最近检测</span><span>操作</span>
            </div>
            <div v-for="s in g.sites" :key="s.mappingId" class="detection-row">
              <span class="site-name">{{ s.siteName }}</span>
              <NTag size="small" :bordered="false">{{ s.remoteModelName }}</NTag>
              <NTag size="small" :type="statusType(s.lastStatus)" :bordered="false">{{ s.lastStatus || '未知' }}</NTag>
              <span class="muted">{{ s.lastDurationMs ? `${s.lastDurationMs}ms` : '-' }}</span>
              <span class="muted">{{ s.lastCheckedAt ? new Date(s.lastCheckedAt).toLocaleString('zh-CN') : '-' }}</span>
              <NButton size="tiny" quaternary @click="handleProbe(s.mappingId)">探测</NButton>
            </div>
          </div>
        </NCard>
      </NSpace>
    </NCard>
  </div>
</template>

<style scoped>
.detection-progress-card {
  margin-bottom: 16px;
}

.progress-title,
.muted {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.detection-matrix-card {
  min-width: 0;
  overflow: hidden;
}

.model-group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.model-group-title {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  flex-wrap: wrap;
}

.detection-table {
  min-width: 760px;
}

.detection-table-head,
.detection-row {
  display: grid;
  grid-template-columns: 1.1fr 1.4fr 100px 90px 170px 72px;
  gap: 12px;
  align-items: center;
}

.detection-table-head {
  padding: 0 0 8px;
  color: var(--text-color-secondary);
  font-size: 12px;
  font-weight: 600;
  border-bottom: 1px solid var(--border-color-global);
}

.detection-row {
  padding: 8px 0;
  border-bottom: 1px solid rgba(148, 163, 184, 0.16);
}

.site-name {
  font-weight: 600;
  color: var(--text-primary);
}

.model-group-card :deep(.n-card__content) {
  overflow-x: auto;
}
</style>
