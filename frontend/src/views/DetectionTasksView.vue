<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NForm, NFormItem, NInput, NInputNumber, NSelect, NPopconfirm, NEmpty, NTooltip, useMessage, type SelectOption } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/detectionTasks'
import type { DetectionTaskItem } from '@/api/detectionTasks'
import * as chatApi from '@/api/chat'
import type { DetectionTargetOption } from '@/api/detectionTasks'
import { formatDetectionDateTime } from './detectionState'

/** 「全部站点模型」哨兵值。 */
const ALL_TARGETS_VALUE = '__all__'

const props = withDefaults(defineProps<{ embedded?: boolean }>(), { embedded: false })
const message = useMessage()
const loading = ref(false)
const tasks = ref<DetectionTaskItem[]>([])
const availableTargets = ref<DetectionTargetOption[]>([])
const form = reactive({ name: '', intervalSeconds: 60, targetMappingId: ALL_TARGETS_VALUE })
const executing = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.listDetectionTasks()
    tasks.value = resp.tasks
    availableTargets.value = resp.availableTargets
  } finally { loading.value = false }
}

/** 站点模型目标下拉（对齐 chat 页）：显示「站点 / 远端模型名」，可搜索。 */
const targetOptions = computed<SelectOption[]>(() => [
  { label: '全部站点模型', value: ALL_TARGETS_VALUE },
  ...availableTargets.value.map((t) => ({ label: `${t.siteName} / ${t.remoteModelName}`, value: t.mappingId }))
])

/** 把「全部」哨兵转换为 null（后端语义：null = 检测全部）。 */
function normalizeTargetId(value: string): string | null {
  return value === ALL_TARGETS_VALUE ? null : value
}

async function handleCreate(): Promise<void> {
  if (!form.name.trim()) { message.warning('任务名称不能为空'); return }
  if (!Number.isInteger(form.intervalSeconds) || form.intervalSeconds < 10) { message.warning('执行间隔最小 10 秒'); return }
  await api.createDetectionTask({
    name: form.name,
    intervalSeconds: form.intervalSeconds,
    siteModelMappingId: normalizeTargetId(form.targetMappingId)
  })
  message.success(`任务已创建，每 ${form.intervalSeconds} 秒执行一次（含随机抖动）`)
  Object.assign(form, { name: '', intervalSeconds: 60, targetMappingId: ALL_TARGETS_VALUE })
  await load()
}

/** 任务的目标展示文案。 */
function targetLabel(task: DetectionTaskItem): string {
  if (task.siteName && task.remoteModelName) return `${task.siteName} / ${task.remoteModelName}`
  if (task.modelName) return `${task.modelName}（按模型，旧任务）`
  return '全部站点模型'
}

async function handleToggle(row: DetectionTaskItem): Promise<void> {
  await api.toggleDetectionTask(row.id)
  // 后端按启用状态和名称排序，切换后重新加载以保持列表顺序一致。
  await load()
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

function historyDuration(startedAt: string, finishedAt: string | null): string {
  if (!startedAt || !finishedAt) return '-'
  return `${((new Date(finishedAt).getTime() - new Date(startedAt).getTime()) / 1000).toFixed(1)}s`
}

function historyStatusType(status: string): 'success' | 'error' | 'warning' {
  if (status === 'completed') return 'success'
  if (status === 'failed') return 'error'
  return 'warning'
}

onMounted(load)
</script>

<template>
  <div :class="[embedded ? 'detection-tasks-embedded' : 'page-container detection-tasks-page']">
    <PageHeader v-if="!embedded" title="检测任务管理" subtitle="配置定时检测任务，自动监控模型可用性" />

    <NCard class="create-task-card">
      <NForm label-placement="top">
        <div class="task-form-grid">
          <NFormItem label="任务名称"><NInput v-model:value="form.name" placeholder="如：全量站点模型检测" /></NFormItem>
          <NFormItem>
            <template #label>
              <span class="interval-label">执行间隔（秒）
                <NTooltip trigger="hover">
                  <template #trigger><span class="tip-icon">?</span></template>
                  最小 10 秒。调度时自动附加 ±20% 随机抖动（至少 ±3 秒），避免固定周期请求特征被上游识别。
                </NTooltip>
              </span>
            </template>
            <NInputNumber v-model:value="form.intervalSeconds" :min="10" :max="86400" :precision="0" placeholder="60" />
          </NFormItem>
          <NFormItem>
            <template #label>
              <span class="interval-label">检测目标
                <NTooltip trigger="hover">
                  <template #trigger><span class="tip-icon">?</span></template>
                  与聊天页同源：每个选项是一个站点上的模型（站点 / 模型名），可输入关键字搜索。选「全部」则检测所有站点模型。
                </NTooltip>
              </span>
            </template>
            <NSelect v-model:value="form.targetMappingId" :options="targetOptions" filterable placeholder="搜索站点 / 模型名" />
          </NFormItem>
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
                <NTag size="tiny" :bordered="false">每 {{ task.intervalSeconds }}s（含随机抖动）</NTag>
                <NTag size="tiny" :bordered="false">目标：{{ targetLabel(task) }}</NTag>
              </div>
            </div>
            <NSpace :wrap="false">
              <NTag size="small" :type="task.isEnabled ? 'success' : 'default'" :bordered="false">{{ task.isEnabled ? '启用' : '禁用' }}</NTag>
              <NButton size="tiny" quaternary @click="handleToggle(task)">{{ task.isEnabled ? '禁用' : '启用' }}</NButton>
              <NButton size="tiny" quaternary :loading="executing === task.id" @click="handleExecute(task)">立即执行</NButton>
              <NPopconfirm @positive-click="handleDelete(task)">
                <template #trigger><NButton size="tiny" quaternary type="error">删除</NButton></template>
                删除任务？
              </NPopconfirm>
            </NSpace>
          </div>
        </template>
        <div class="task-last-line">上次执行：{{ formatDetectionDateTime(task.lastExecutionStartedAt) }} <NTag v-if="task.lastExecutionStatus" size="tiny" :bordered="false">{{ task.lastExecutionStatus }}</NTag></div>
        <div class="history-table-scroll">
          <div class="history-table">
            <div class="history-head"><span>开始时间</span><span>耗时</span><span>状态</span><span>摘要</span></div>
            <div v-for="(h, idx) in task.executionHistory" :key="idx" class="history-row">
              <span>{{ formatDetectionDateTime(h.startedAt) }}</span>
              <span>{{ historyDuration(h.startedAt, h.finishedAt) }}</span>
              <NTag size="tiny" :type="historyStatusType(h.status)" :bordered="false">{{ h.status }}</NTag>
              <span class="history-summary">{{ h.summary || '-' }}</span>
            </div>
            <div v-if="task.executionHistory.length === 0" class="history-empty">暂无执行历史</div>
          </div>
        </div>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.detection-tasks-page { min-width: 0; }
.create-task-card { margin-bottom: 16px; }
.create-task-card :deep(.n-card__content) { padding: 16px; }
.task-form-grid { display: grid; grid-template-columns: 1.2fr 1fr 1.4fr auto; gap: 12px; align-items: end; }
.interval-label { display: inline-flex; align-items: center; gap: 4px; }
.tip-icon { cursor: help; color: var(--text-color-secondary); }
.task-card-list { display: grid; gap: 12px; }
.task-card { min-width: 0; }
.task-card :deep(.n-card__content) { min-width: 0; }
.task-card-header { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; }
.task-meta, .task-last-line, .history-summary { color: var(--text-color-secondary); font-size: 12px; }
.task-meta { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 6px; }
.task-last-line { display: flex; align-items: center; gap: 8px; margin-bottom: 10px; }
.history-table-scroll { width: 100%; min-width: 0; max-width: 100%; overflow-x: auto; }
.history-table { min-width: 640px; }
.history-head, .history-row { display: grid; grid-template-columns: 180px 80px 90px minmax(180px, 1fr); gap: 12px; align-items: center; padding: 12px 14px; font-size: 13px; }
.history-head { color: var(--text-color-secondary); font-weight: 600; border-bottom: 1px solid var(--border-color-global); }
.history-row { border-bottom: 1px solid rgba(148, 163, 184, 0.16); }
.history-empty { padding: 12px 0; color: var(--text-color-secondary); font-size: 13px; }
@media (max-width: 900px) { .task-form-grid { grid-template-columns: 1fr; } .task-card-header { flex-direction: column; } }
</style>
