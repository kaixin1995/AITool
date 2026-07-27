<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import { NCard, NButton, NSpace, NDataTable, NTag, NModal, NForm, NFormItem, NInput, NSelect, NPopconfirm, NCollapse, NCollapseItem, useMessage, type DataTableColumns, type SelectOption } from 'naive-ui'
import * as api from '@/api/detectionTasks'
import type { DetectionTaskItem } from '@/api/detectionTasks'

const message = useMessage()
const loading = ref(false)
const tasks = ref<DetectionTaskItem[]>([])
const availableModels = ref<{ id: string; displayName: string }[]>([])
const showModal = ref(false)
const form = reactive({ name: '', cronExpression: '0 * * * *', modelLibraryItemId: null as string | null })
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
  showModal.value = false
  Object.assign(form, { name: '', cronExpression: '0 * * * *', modelLibraryItemId: null })
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

const columns = computed<DataTableColumns<DetectionTaskItem>>(() => [
  { title: '名称', key: 'name', minWidth: 120 },
  { title: 'Cron', key: 'cronExpression', width: 140 },
  { title: '模型', key: 'modelName', width: 120, render: (r) => r.modelName || '全部' },
  { title: '状态', key: 'isEnabled', width: 80, render: (r) => h(NTag, { size: 'small', type: r.isEnabled ? 'success' : 'default', bordered: false }, () => r.isEnabled ? '启用' : '禁用') },
  { title: '上次执行', key: 'last', minWidth: 160, render: (r) => r.lastExecutionStartedAt ? `${new Date(r.lastExecutionStartedAt).toLocaleString('zh-CN')} (${r.lastExecutionStatus})` : '—' },
  { title: '操作', key: 'actions', width: 200, fixed: 'right', render: (row) => h(NSpace, { size: 4 }, () => [
    h(NButton, { size: 'tiny', quaternary: true, loading: executing.value === row.id, onClick: () => handleExecute(row) }, () => '执行'),
    h(NButton, { size: 'tiny', quaternary: true, onClick: () => handleToggle(row) }, () => row.isEnabled ? '禁用' : '启用'),
    h(NPopconfirm, { onPositiveClick: () => handleDelete(row) }, { trigger: () => h(NButton, { size: 'tiny', quaternary: true, type: 'error' }, () => '删除'), default: () => '删除任务？' })
  ]) }
])

onMounted(load)
</script>

<template>
  <div class="page-container">
    <NCard>
      <template #header>
        <NSpace justify="space-between" align="center">
          <span>检测任务</span>
          <NButton type="primary" @click="showModal = true">新建任务</NButton>
        </NSpace>
      </template>
      <NDataTable :columns="columns" :data="tasks" :loading="loading" :row-key="(r: DetectionTaskItem) => r.id" striped />

      <NCollapse v-if="tasks.some((t) => t.executionHistory.length > 0)" style="margin-top: 16px">
        <NCollapseItem v-for="t in tasks.filter((x) => x.executionHistory.length > 0)" :key="t.id" :title="`${t.name} 执行历史`" :name="t.id">
          <div v-for="(h, idx) in t.executionHistory" :key="idx" class="history-row">
            <NTag size="tiny" :type="h.status === 'completed' ? 'success' : 'warning'" :bordered="false">{{ h.status }}</NTag>
            <span>{{ h.startedAt ? new Date(h.startedAt).toLocaleString('zh-CN') : '—' }}</span>
            <span style="color: var(--text-color-secondary)">{{ h.summary }}</span>
          </div>
        </NCollapseItem>
      </NCollapse>
    </NCard>

    <NModal v-model:show="showModal" title="新建检测任务" preset="card" style="width: 480px">
      <NForm label-placement="top">
        <NFormItem label="任务名称"><NInput v-model:value="form.name" /></NFormItem>
        <NFormItem label="Cron 表达式"><NInput v-model:value="form.cronExpression" placeholder="如 0 * * * *（每小时）" /></NFormItem>
        <NFormItem label="检测模型"><NSelect v-model:value="form.modelLibraryItemId" :options="modelOptions" /></NFormItem>
      </NForm>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="showModal = false">取消</NButton>
          <NButton type="primary" @click="handleCreate">创建</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.history-row { display: flex; gap: 12px; align-items: center; padding: 6px 0; font-size: 13px; }
</style>
