<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import { NCard, NButton, NSpace, NDataTable, NTag, NModal, NForm, NFormItem, NInput, NPopconfirm, useMessage, type DataTableColumns } from 'naive-ui'
import * as api from '@/api/compatibility'
import type { CompatibilityProfileListItem } from '@/api/compatibility'

const message = useMessage()
const loading = ref(false)
const items = ref<CompatibilityProfileListItem[]>([])
const showModal = ref(false)
const editingId = ref<string | null>(null)
const form = reactive({ name: '', description: '', rulesJson: '[]' })
const saving = ref(false)

const isEdit = computed(() => !!editingId.value)

async function load(): Promise<void> {
  loading.value = true
  try { items.value = await api.listProfiles() } finally { loading.value = false }
}

function openCreate(): void {
  editingId.value = null
  Object.assign(form, { name: '', description: '', rulesJson: '[]' })
  showModal.value = true
}

async function openEdit(row: CompatibilityProfileListItem): Promise<void> {
  editingId.value = row.id
  const detail = await api.getProfile(row.id)
  Object.assign(form, { name: detail.name, description: detail.description, rulesJson: detail.rulesJson })
  showModal.value = true
}

async function handleSave(): Promise<void> {
  if (!form.name.trim()) { message.warning('名称不能为空'); return }
  try { JSON.parse(form.rulesJson) } catch { message.error('规则 JSON 格式无效'); return }
  saving.value = true
  try {
    if (editingId.value) { await api.updateProfile(editingId.value, form); message.success('已更新') }
    else { await api.createProfile(form); message.success('已创建') }
    showModal.value = false
    await load()
  } finally { saving.value = false }
}

async function handleToggle(row: CompatibilityProfileListItem): Promise<void> {
  await api.toggleProfile(row.id); row.isEnabled = !row.isEnabled
}
async function handleDelete(row: CompatibilityProfileListItem): Promise<void> {
  await api.deleteProfile(row.id); message.success('已删除'); await load()
}

const columns = computed<DataTableColumns<CompatibilityProfileListItem>>(() => [
  { title: '名称', key: 'name', minWidth: 140 },
  { title: '描述', key: 'description', minWidth: 200, ellipsis: { tooltip: true } },
  { title: '规则数', key: 'ruleCount', width: 80 },
  { title: '状态', key: 'isEnabled', width: 80, render: (r) => h(NTag, { size: 'small', type: r.isEnabled ? 'success' : 'default', bordered: false }, () => r.isEnabled ? '启用' : '禁用') },
  { title: '操作', key: 'actions', width: 180, fixed: 'right', render: (row) => h(NSpace, { size: 8 }, () => [
    h(NButton, { size: 'small', quaternary: true, onClick: () => openEdit(row) }, () => '编辑'),
    h(NButton, { size: 'small', quaternary: true, onClick: () => handleToggle(row) }, () => row.isEnabled ? '禁用' : '启用'),
    h(NPopconfirm, { onPositiveClick: () => handleDelete(row) }, { trigger: () => h(NButton, { size: 'small', quaternary: true, type: 'error' }, () => '删除'), default: () => `确认删除「${row.name}」？` })
  ]) }
])

onMounted(load)
</script>

<template>
  <div class="page-container">
    <NCard>
      <template #header>
        <NSpace justify="space-between" align="center">
          <span>兼容规则集</span>
          <NButton type="primary" @click="openCreate">新建规则集</NButton>
        </NSpace>
      </template>
      <NDataTable :columns="columns" :data="items" :loading="loading" :row-key="(r: CompatibilityProfileListItem) => r.id" striped />
    </NCard>

    <NModal v-model:show="showModal" :title="isEdit ? '编辑规则集' : '新建规则集'" preset="card" style="width: 640px" :mask-closable="false">
      <NForm label-placement="top">
        <NFormItem label="名称"><NInput v-model:value="form.name" /></NFormItem>
        <NFormItem label="描述"><NInput v-model:value="form.description" type="textarea" :autosize="{ minRows: 2 }" /></NFormItem>
        <NFormItem label="规则 JSON">
          <NInput v-model:value="form.rulesJson" type="textarea" :autosize="{ minRows: 8, maxRows: 20 }" style="font-family: monospace" />
        </NFormItem>
      </NForm>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="showModal = false">取消</NButton>
          <NButton type="primary" :loading="saving" @click="handleSave">保存</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>
