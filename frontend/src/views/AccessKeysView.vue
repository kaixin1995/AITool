<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import { NCard, NButton, NSpace, NDataTable, NTag, NModal, NForm, NFormItem, NInput, NPopconfirm, NAlert, useMessage, type DataTableColumns } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/accessKeys'
import type { AccessKeyItem } from '@/api/accessKeys'

const message = useMessage()
const loading = ref(false)
const items = ref<AccessKeyItem[]>([])
const showModal = ref(false)
const newKeyName = ref('')
const newKeyPlain = ref('')
const saving = ref(false)

async function load(): Promise<void> {
  loading.value = true
  try { items.value = await api.listAccessKeys() } finally { loading.value = false }
}

async function handleCreate(): Promise<void> {
  if (!newKeyName.value.trim()) { message.warning('请输入密钥名称'); return }
  saving.value = true
  try {
    const result = await api.createAccessKey(newKeyName.value.trim())
    newKeyPlain.value = result.plainKey
    message.success('密钥已创建，请立即复制（仅展示一次）')
    await load()
  } finally { saving.value = false }
}

async function handleToggle(row: AccessKeyItem): Promise<void> {
  await api.toggleAccessKey(row.id)
  row.isEnabled = !row.isEnabled
}
async function handleDelete(row: AccessKeyItem): Promise<void> {
  await api.deleteAccessKey(row.id)
  message.success('已删除')
  await load()
}

const columns = computed<DataTableColumns<AccessKeyItem>>(() => [
  { title: '名称', key: 'keyName', minWidth: 140 },
  { title: '密钥', key: 'maskedValue', minWidth: 160 },
  { title: '允许路由', key: 'allowedRouteNames', minWidth: 120, render: (r) => {
    const val = r.allowedRouteNames
    // 后端返回 string[] 或 JSON 字符串；空数组/空串=允许全部
    const arr = Array.isArray(val) ? val : (typeof val === 'string' && val.trim() ? (() => { try { return JSON.parse(val) } catch { return [] } })() : [])
    return Array.isArray(arr) && arr.length > 0 ? h(NTag, { size: 'small', bordered: false }, () => arr.join(', ')) : '全部'
  } },
  { title: '状态', key: 'isEnabled', width: 80, render: (r) => h(NTag, { size: 'small', type: r.isEnabled ? 'success' : 'default', bordered: false }, () => r.isEnabled ? '启用' : '禁用') },
  { title: '操作', key: 'actions', width: 140, fixed: 'right', render: (row) => h(NSpace, { size: 8 }, () => [
    h(NButton, { size: 'small', quaternary: true, onClick: () => handleToggle(row) }, () => row.isEnabled ? '禁用' : '启用'),
    h(NPopconfirm, { onPositiveClick: () => handleDelete(row) }, { trigger: () => h(NButton, { size: 'small', quaternary: true, type: 'error' }, () => '删除'), default: () => `确认删除「${row.keyName}」？` })
  ]) }
])

onMounted(load)
</script>

<template>
  <div class="page-container">
    <PageHeader title="访问密钥管理" subtitle="管理用于访问代理服务的密钥，可限定每个密钥只能访问指定路由">
      <template #actions>
        <NButton type="primary" @click="showModal = true; newKeyName = ''; newKeyPlain = ''">新建密钥</NButton>
      </template>
    </PageHeader>
    <NCard>
      <NDataTable :columns="columns" :data="items" :loading="loading" :row-key="(r: AccessKeyItem) => r.id" :pagination="{ pageSize: 20 }" striped />
    </NCard>

    <NModal v-model:show="showModal" title="新建访问密钥" preset="card" style="width: 480px; max-width: 92vw" :mask-closable="false">
      <NAlert v-if="newKeyPlain" type="success" :show-icon="true" style="margin-bottom: 16px">
        密钥已创建（仅展示一次，请立即复制）：
        <NInput :value="newKeyPlain" readonly type="textarea" :autosize="{ minRows: 2 }" style="margin-top: 8px" />
      </NAlert>
      <NForm v-else label-placement="top">
        <NFormItem label="密钥名称">
          <NInput v-model:value="newKeyName" placeholder="如：生产环境密钥" @keyup.enter="handleCreate" />
        </NFormItem>
      </NForm>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="showModal = false">关闭</NButton>
          <NButton v-if="!newKeyPlain" type="primary" :loading="saving" @click="handleCreate">创建</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>
