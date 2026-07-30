<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NForm, NFormItem, NInput, NSelect, NPopconfirm, NEmpty, useMessage, type SelectOption } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/detectionTasks'
import type { DetectionTaskItem } from '@/api/detectionTasks'

const message = useMessage()
const loading = ref(false)
const tasks = ref<DetectionTaskItem[]>([])
const availableModels = ref<{ id: string; displayName: string }[]>([])
const form = reactive({ name: '', cronExpression: '*/30 * * * *', modelLibraryItemId: null as string | null })
const executing = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.listDetectionTasks()
    tasks.value = resp.tasks
    availableModels.value = resp.availableModels
  } finally { loading.value = false }
}

const modelOptions = computed<SelectOption[]>(() => [
  { label: '全部模型', value: '' },
  ...availableModels.value.map((m) => ({ label: m.displayName, value: m.id }))
])

async function handleCreate(): Promise<void> {
  if (!form.name.trim() || !form.cronExpression.trim()) { message.warning('名称和 Cron 不能为空'); return }
  await api.createDetectionTask(form)
  message.success('任务已创建')
  Object.assign(form, { name: '', cronExpression: '*/30 * * * *', modelLibraryItemId: null })
  await load()
}

async function handleToggle(row: DetectionTaskItem): Promise<void> {
  const r = await api.toggleDetectionTask(row.id)
  row.isEnabled = r.isEnabled
}
async function handleExecute(row: DetectionTaskItem): Promise<void> {
  executing.value = row.id
  try {
    await api.executeDetectionTask(row.id)
    message.success('任务已执行')
    await load()
  } finally { executing.value = null }
}
async function handleDelete(row: DetectionTaskItem): Promise<void> {
  await api.deleteDetectionTask(row.id)
  message.success('已删除')
  await load()
}

function formatDateTime(value: string | null): string {
  return value ? new Date(value).toLocaleString('zh-CN') : '—'
}

function historyDuration(startedAt: string, finishedAt: string | null): string {
  if (!startedAt || !finishedAt) return '-'
  return `${Math.round((new Date(finishedAt).getTime() - new Date(startedAt).getTime()) / 1000)}s`
}

function historyStatusType(status: string): 'success' | 'error' | 'warning' {
  if (status === 'completed') return 'success'
  if (status === 'failed') return 'error'
  return 'warning'
}

onMounted(load)
</script>

<template>
  <div class="page-container detection-tasks-page">
    <PageHeader title="检测任务管理" subtitle="配置定时检测任务，自动监控模型可用性" />

    <NCard class="create-task-card">
      <NForm label-placement="top">
        <div class="task-form-grid">
          <NFormItem label="任务名称"><NInput v-model:value="form.name" placeholder="如：全量模型半小时检测" /></NFormItem>
          <NFormItem label="Cron 表达式"><NInput v-model:value="form.cronExpression" placeholder="*/30 * * * *" /></NFormItem>
          <NFormItem label="检测模型"><NSelect v-model:value="form.modelLibraryItemId" :options="modelOptions" /></NFormItem>
          <NFormItem label="操作"><NButton type="primary" @click="handleCreate">创建任务</NButton></NFormItem>
        </div>
      </NForm>
    </NCard>

    <div class="task-card-list">
      <NEmpty v-if="!loading && tasks.length === 0" description="⏰ 暂无检测任务，请在上方创建" />
      <NCard v-for="task in tasks" :key="task.id" class="task-card" size="small">
        <template #header>
          <div class="task-card-header">
            <div>
              <strong>{{ task.name }}</strong>
              <div class="task-meta">
                <NTag size="tiny" :bordered="false">Cron：{{ task.cronExpression }}</NTag>
                <NTag size="tiny" :bordered="false">模型：{{ task.modelName || '全部' }}</NTag>
              </div>
            </div>
            <NSpace :wrap="false">
              <NTag size="small" :type="task.isEnabled ? 'success' : 'default'" :bordered="false">{{ task.isEnabled ? '启用' : '禁用' }}</NTag>
              <NButton size="tiny" quaternary :loading="executing === task.id" @click="handleExecute(task)">立即执行</NButton>
              <NButton size="tiny" quaternary @click="handleToggle(task)">{{ task.isEnabled ? '禁用' : '启用' }}</NButton>
              <NPopconfirm @positive-click="handleDelete(task)">
                <template #trigger><NButton size="tiny" quaternary type="error">删除</NButton></template>
                删除任务？
              </NPopconfirm>
            </NSpace>
          </div>
        </template>
        <div class="task-last-line">上次执行：{{ formatDateTime(task.lastExecutionStartedAt) }} <NTag v-if="task.lastExecutionStatus" size="tiny" :bordered="false">{{ task.lastExecutionStatus }}</NTag></div>
        <div class="history-table">
          <div class="history-head"><span>开始时间</span><span>耗时</span><span>状态</span><span>摘要</span></div>
          <div v-for="(h, idx) in task.executionHistory" :key="idx" class="history-row">
            <span>{{ formatDateTime(h.startedAt) }}</span>
            <span>{{ historyDuration(h.startedAt, h.finishedAt) }}</span>
            <NTag size="tiny" :type="historyStatusType(h.status)" :bordered="false">{{ h.status }}</NTag>
            <span class="history-summary">{{ h.summary || '-' }}</span>
          </div>
          <div v-if="task.executionHistory.length === 0" class="history-empty">暂无执行历史</div>
        </div>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.detection-tasks-page { min-width: 0; }
.create-task-card { margin-bottom: 16px; }
.create-task-card :deep(.n-card__content) { padding: 16px; }
.task-form-grid { display: grid; grid-template-columns: 1.2fr 1fr 1fr auto; gap: 12px; align-items: end; }
.task-card-list { display: grid; gap: 12px; }
.task-card-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
.task-meta, .task-last-line, .history-summary { color: var(--text-color-secondary); font-size: 12px; }
.task-meta { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 6px; }
.task-last-line { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; }
.history-table { min-width: 640px; overflow-x: auto; }
.history-head, .history-row { display: grid; grid-template-columns: 180px 80px 90px minmax(180px, 1fr); gap: 12px; align-items: center; padding: 7px 0; font-size: 13px; }
.history-head { color: var(--text-color-secondary); font-weight: 600; border-bottom: 1px solid var(--border-color-global); }
.history-row { border-bottom: 1px solid rgba(148, 163, 184, 0.16); }
.history-empty { padding: 12px 0; color: var(--text-color-secondary); font-size: 13px; }
@media (max-width: 900px) { .task-form-grid { grid-template-columns: 1fr; } .task-card-header { flex-direction: column; } }
</style>
