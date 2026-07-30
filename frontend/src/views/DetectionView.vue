<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { NCard, NButton, NTag, NEmpty, NInput, NProgress, useMessage } from 'naive-ui'
import * as api from '@/api/detection'
import type { DetectionModelGroup, ProbeProgress } from '@/api/detection'
import PageHeader from '@/components/PageHeader.vue'
import { applyDetectionProbeResult, formatDetectionDateTime, shouldRetryDetectionProgress } from './detectionState'

interface DetectionProgressState extends ProbeProgress {
  successCount: number
  failedCount: number
}

const message = useMessage()
const loading = ref(false)
const groups = ref<DetectionModelGroup[]>([])
const modelKeyword = ref('')
const probeProgress = ref<DetectionProgressState | null>(null)
const probingMappingIds = ref<Set<string>>(new Set())
const probingModelId = ref<string | null>(null)
const probingAll = ref(false)
let progressTimer: number | undefined

const filteredGroups = computed(() => {
  const keyword = modelKeyword.value.trim().toLowerCase()
  if (!keyword) return groups.value
  return groups.value.filter(group =>
    `${group.displayName} ${group.modelName}`.toLowerCase().includes(keyword)
  )
})

const progressPercent = computed(() => {
  if (!probeProgress.value || probeProgress.value.total === 0) return 0
  return Math.round(
    (probeProgress.value.completed / probeProgress.value.total) * 100
  )
})

const isBatchRunning = computed(() => probingAll.value || probingModelId.value !== null)
const hasSingleProbe = computed(() => probingMappingIds.value.size > 0)

async function load(): Promise<void> {
  loading.value = true
  try {
    const response = await api.getDetectionMatrix()
    groups.value = response.modelGroups
  } finally {
    loading.value = false
  }
}

function setMappingProbing(mappingId: string, probing: boolean): void {
  const next = new Set(probingMappingIds.value)
  if (probing) next.add(mappingId)
  else next.delete(mappingId)
  probingMappingIds.value = next
}

async function handleProbe(mappingId: string): Promise<void> {
  if (probingMappingIds.value.has(mappingId) || isBatchRunning.value) return

  setMappingProbing(mappingId, true)
  try {
    const result = await api.probeMapping(mappingId)
    applyDetectionProbeResult(groups.value, result)
    if (result.status === 'success') {
      message.success(`检测成功（${result.durationMs ?? '-'} ms）`)
    } else {
      message.error(result.error || '检测失败')
    }
  } catch (error) {
    message.error((error as Error).message)
  } finally {
    setMappingProbing(mappingId, false)
  }
}

async function handleProbeModel(modelId: string): Promise<void> {
  if (isBatchRunning.value || hasSingleProbe.value) return

  probingModelId.value = modelId
  try {
    const response = await api.probeModel(modelId)
    message.info(`已提交批量检测任务 ${response.taskId}`)
    startProgressPolling(response.taskId)
  } catch (error) {
    probingModelId.value = null
    message.error((error as Error).message)
  }
}

async function handleProbeAll(): Promise<void> {
  if (isBatchRunning.value || hasSingleProbe.value) return

  probingAll.value = true
  try {
    const response = await api.probeAll()
    message.info(`已提交全量检测任务 ${response.taskId}`)
    startProgressPolling(response.taskId)
  } catch (error) {
    probingAll.value = false
    message.error((error as Error).message)
  }
}

function clearProgressTimer(): void {
  if (progressTimer !== undefined) {
    window.clearTimeout(progressTimer)
    progressTimer = undefined
  }
}

function scheduleProgressPoll(taskId: string, delay: number): void {
  clearProgressTimer()
  progressTimer = window.setTimeout(() => {
    void pollProgress(taskId)
  }, delay)
}

function startProgressPolling(taskId: string): void {
  clearProgressTimer()
  probeProgress.value = {
    taskId,
    total: 0,
    completed: 0,
    isCompleted: false,
    newResults: [],
    successCount: 0,
    failedCount: 0
  }
  void pollProgress(taskId)
}

async function pollProgress(taskId: string): Promise<void> {
  if (probeProgress.value?.taskId !== taskId) return

  try {
    const progress = await api.getProbeProgress(taskId)
    if (probeProgress.value?.taskId !== taskId) return

    let successCount = probeProgress.value.successCount
    let failedCount = probeProgress.value.failedCount
    for (const result of progress.newResults ?? []) {
      applyDetectionProbeResult(groups.value, result)
      if (result.status === 'success') successCount += 1
      else failedCount += 1
    }

    probeProgress.value = {
      ...progress,
      successCount,
      failedCount
    }

    if (!progress.isCompleted) {
      scheduleProgressPoll(taskId, 1200)
      return
    }

    clearProgressTimer()
    probingAll.value = false
    probingModelId.value = null
    const summary = `检测完成：${successCount} 成功，${failedCount} 失败`
    if (failedCount > 0) message.warning(summary)
    else message.success(summary)

    // 任务完成后重新同步矩阵，包含本页未显示映射产生的最终状态。
    try {
      await load()
    } catch {
      // 请求层已经统一提示刷新错误，检测任务本身仍视为完成。
    }
  } catch (error) {
    if (probeProgress.value?.taskId !== taskId) return
    if (shouldRetryDetectionProgress(error)) {
      scheduleProgressPoll(taskId, 2000)
      return
    }

    clearProgressTimer()
    probeProgress.value = null
    probingAll.value = false
    probingModelId.value = null
    message.error('检测任务已过期，请重新发起检测')
  }
}

function statusType(status: string): 'success' | 'error' | 'default' {
  if (status === 'success') return 'success'
  if (status === 'fail') return 'error'
  return 'default'
}

onMounted(() => {
  void load()
})

onBeforeUnmount(() => {
  clearProgressTimer()
  probeProgress.value = null
})
</script>

<template>
  <div class="page-container detection-page">
    <PageHeader title="模型检测" subtitle="按模型分组查看各站点的可用性和响应状态">
      <template #actions>
        <NTag v-if="groups.length" round :bordered="false" size="small">
          {{ filteredGroups.length }} / {{ groups.length }} 个模型
        </NTag>
        <NButton
          size="small"
          type="primary"
          :loading="probingAll"
          :disabled="groups.length === 0 || isBatchRunning || hasSingleProbe"
          @click="handleProbeAll"
        >
          全部检测
        </NButton>
        <NButton size="small" :loading="loading" @click="load">刷新</NButton>
      </template>
    </PageHeader>

    <NCard size="small" class="detection-search-card">
      <label class="detection-search-field">
        <span class="form-label">搜索模型</span>
        <NInput
          v-model:value="modelKeyword"
          clearable
          placeholder="输入模型名称过滤..."
        />
      </label>
    </NCard>

    <NCard
      v-if="probeProgress"
      class="detection-progress-card"
      :class="{
        completed: probeProgress.isCompleted,
        failed: probeProgress.isCompleted && probeProgress.failedCount > 0
      }"
      size="small"
    >
      <div class="progress-header">
        <strong>
          {{ probeProgress.isCompleted ? '检测完成' : '批量检测进度' }}：
          {{ probeProgress.completed }} / {{ probeProgress.total }}
        </strong>
        <span v-if="probeProgress.isCompleted" class="progress-summary">
          {{ probeProgress.successCount }} 成功，{{ probeProgress.failedCount }} 失败
        </span>
      </div>
      <NProgress
        type="line"
        :percentage="progressPercent"
        :status="probeProgress.failedCount > 0 ? 'error' : 'success'"
        :show-indicator="false"
      />
    </NCard>

    <NCard v-if="!loading && groups.length === 0" class="detection-empty-card">
      <NEmpty description="🔍 暂无映射数据，请先配置站点和模型" />
    </NCard>
    <NCard v-else-if="!loading && filteredGroups.length === 0" class="detection-empty-card">
      <NEmpty description="没有匹配的模型" />
    </NCard>

    <div v-else class="detection-groups">
      <NCard
        v-for="group in filteredGroups"
        :key="group.modelLibraryItemId"
        size="small"
        class="model-group-card"
      >
        <template #header>
          <div class="model-group-header">
            <div class="model-group-title">
              <strong>{{ group.displayName }}</strong>
              <div class="model-group-meta">
                <code>{{ group.modelName }}</code>
                <span>{{ group.sites.length }} 个站点</span>
              </div>
            </div>
            <NButton
              size="small"
              secondary
              type="primary"
              :loading="probingModelId === group.modelLibraryItemId"
              :disabled="isBatchRunning || hasSingleProbe"
              @click="handleProbeModel(group.modelLibraryItemId)"
            >
              {{ probingModelId === group.modelLibraryItemId ? '检测中...' : '检测该模型' }}
            </NButton>
          </div>
        </template>

        <div class="detection-table-scroll">
          <div class="detection-table">
            <div class="detection-table-head">
              <span>站点名称</span>
              <span>远程模型名</span>
              <span>最近状态</span>
              <span>最近检测时间</span>
              <span>耗时</span>
              <span>操作</span>
            </div>
            <div
              v-for="site in group.sites"
              :key="site.mappingId"
              class="detection-row"
            >
              <span class="site-name text-ellipsis" :title="site.siteName">
                {{ site.siteName }}
              </span>
              <code class="remote-model-name text-ellipsis" :title="site.remoteModelName">
                {{ site.remoteModelName }}
              </code>
              <NTag
                size="small"
                :type="statusType(site.lastStatus)"
                :bordered="false"
              >
                {{ site.lastStatus || '未知' }}
              </NTag>
              <span class="muted" :title="formatDetectionDateTime(site.lastCheckedAt)">
                {{ formatDetectionDateTime(site.lastCheckedAt) }}
              </span>
              <span class="muted">
                {{ site.lastDurationMs != null ? `${site.lastDurationMs} ms` : '-' }}
              </span>
              <NButton
                size="small"
                type="primary"
                :loading="probingMappingIds.has(site.mappingId)"
                :disabled="isBatchRunning || probingMappingIds.has(site.mappingId)"
                @click="handleProbe(site.mappingId)"
              >
                {{ probingMappingIds.has(site.mappingId) ? '检测中...' : '检测' }}
              </NButton>
            </div>
          </div>
        </div>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.detection-search-card,
.detection-progress-card,
.detection-empty-card {
  margin-bottom: 20px;
}

.detection-search-card :deep(.n-card__content) {
  padding: 20px;
}

.detection-search-field {
  display: grid;
  width: min(420px, 100%);
  gap: 8px;
}

.progress-header {
  display: flex;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 10px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.detection-progress-card.completed {
  border-color: rgba(16, 185, 129, 0.4);
  background: rgba(16, 185, 129, 0.05);
}

.detection-progress-card.completed.failed {
  border-color: rgba(245, 158, 11, 0.4);
  background: rgba(245, 158, 11, 0.06);
}

.progress-summary {
  white-space: nowrap;
}

.detection-groups {
  display: grid;
  min-width: 0;
  gap: 20px;
}

.model-group-card {
  min-width: 0;
  overflow: hidden;
}

.model-group-card :deep(.n-card-header) {
  padding: 16px 20px;
  border-bottom: 1px solid var(--border-color-global);
  background: var(--bg-page);
}

.model-group-card :deep(.n-card__content) {
  padding: 0;
}

.model-group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}

.model-group-title {
  display: grid;
  min-width: 0;
  gap: 5px;
}

.model-group-title > strong {
  color: var(--text-primary);
  font-size: 15px;
  font-weight: 600;
}

.model-group-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--text-color-secondary);
  font-size: 12px;
}

.model-group-meta code,
.remote-model-name {
  border-radius: 4px;
  background: rgba(59, 130, 246, 0.08);
  color: #2563eb;
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
}

.model-group-meta code {
  padding: 2px 6px;
}

.detection-table-scroll {
  min-width: 0;
  overflow-x: auto;
}

.detection-table {
  min-width: 820px;
}

.detection-table-head,
.detection-row {
  display: grid;
  grid-template-columns: minmax(130px, 1.1fr) minmax(160px, 1.35fr) 100px 180px 90px 84px;
  gap: 12px;
  align-items: center;
}

.detection-table-head {
  padding: 11px 20px;
  border-bottom: 1px solid var(--border-color-global);
  background: var(--bg-page);
  color: var(--text-color-secondary);
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.4px;
}

.detection-row {
  padding: 13px 20px;
  border-bottom: 1px solid rgba(148, 163, 184, 0.16);
  transition: background-color 0.15s ease;
}

.detection-row:hover {
  background: rgba(148, 163, 184, 0.06);
}

.detection-row:last-child {
  border-bottom: 0;
}

.site-name {
  color: var(--text-primary);
  font-weight: 600;
}

.remote-model-name {
  display: block;
  width: fit-content;
  max-width: 100%;
  padding: 3px 7px;
}

.text-ellipsis {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.muted {
  overflow: hidden;
  color: var(--text-color-secondary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

@media (max-width: 575.98px) {
  .model-group-header,
  .progress-header {
    align-items: flex-start;
    flex-direction: column;
  }

  .progress-summary {
    white-space: normal;
  }
}
</style>
