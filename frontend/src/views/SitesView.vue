<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import {
  NCard, NButton, NSpace, NDataTable, NTag, NModal, NForm, NFormItem, NInput,
  NSwitch, NPopconfirm, NSelect, useMessage, type DataTableColumns
} from 'naive-ui'
import * as sitesApi from '@/api/sites'
import type { SiteListItem, SitePayload } from '@/api/sites'

const message = useMessage()
const loading = ref(false)
const sites = ref<SiteListItem[]>([])
const checkedRowKeys = ref<Array<string | number>>([])

// 创建/编辑模态框
const showModal = ref(false)
const editingId = ref<string | null>(null) // null=新建，有值=编辑
const form = reactive<SitePayload>({
  name: '',
  baseUrl: '',
  endpointPathMode: 'standard-root',
  apiKey: '',
  supportsOpenAi: true,
  supportsAnthropic: false,
  isEnabled: true
})
const saving = ref(false)

const isEditMode = computed(() => !!editingId.value)
const modalTitle = computed(() => (isEditMode.value ? '编辑站点' : '新建站点'))

const endpointModeOptions = [
  { label: '标准根地址（自动加 /v1/）', value: 'standard-root' },
  { label: '已含版本路径（直接追加）', value: 'versioned-base' }
]

async function loadSites(): Promise<void> {
  loading.value = true
  try {
    sites.value = await sitesApi.listSites()
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  Object.assign(form, {
    name: '', baseUrl: '', endpointPathMode: 'standard-root', apiKey: '',
    supportsOpenAi: true, supportsAnthropic: false, isEnabled: true
  })
  showModal.value = true
}

async function openEdit(row: SiteListItem): Promise<void> {
  editingId.value = row.id
  const detail = await sitesApi.getSite(row.id)
  Object.assign(form, {
    name: detail.name,
    baseUrl: detail.baseUrl,
    endpointPathMode: detail.endpointPathMode,
    apiKey: '', // 编辑时留空表示保留原密钥
    supportsOpenAi: detail.supportsOpenAi,
    supportsAnthropic: detail.supportsAnthropic,
    isEnabled: detail.isEnabled
  })
  showModal.value = true
}

async function handleSave(): Promise<void> {
  if (!form.name.trim() || !form.baseUrl.trim()) {
    message.warning('名称和地址不能为空')
    return
  }
  if (!isEditMode.value && !form.apiKey.trim()) {
    message.warning('密钥不能为空')
    return
  }
  saving.value = true
  try {
    if (editingId.value) {
      await sitesApi.updateSite(editingId.value, form)
      message.success('站点已更新')
    } else {
      await sitesApi.createSite(form)
      message.success('站点已创建')
    }
    showModal.value = false
    await loadSites()
  } finally {
    saving.value = false
  }
}

async function handleToggle(row: SiteListItem): Promise<void> {
  const result = await sitesApi.toggleSite(row.id)
  row.isEnabled = result.isEnabled
  message.success(`站点已${result.isEnabled ? '启用' : '禁用'}`)
}

async function handleDelete(row: SiteListItem): Promise<void> {
  await sitesApi.deleteSite(row.id)
  message.success('站点已删除')
  await loadSites()
}

async function handleBulkDelete(): Promise<void> {
  if (checkedRowKeys.value.length === 0) {
    message.warning('请先选择要删除的站点')
    return
  }
  const ids = checkedRowKeys.value.map(String)
  const result = await sitesApi.bulkDeleteSites(ids)
  message.success(`已批量删除 ${result.deletedCount} 个站点`)
  checkedRowKeys.value = []
  await loadSites()
}

// 导入/导出
const importVisible = ref(false)
const importJson = ref('')
const importing = ref(false)

async function handleExport(): Promise<void> {
  try {
    const items = await sitesApi.exportSites()
    const blob = new Blob([JSON.stringify(items, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `sites-${new Date().toISOString().slice(0, 10)}.json`
    a.click()
    URL.revokeObjectURL(url)
    message.success(`已导出 ${(items as unknown[]).length} 个站点`)
  } catch (e) {
    message.error((e as Error).message)
  }
}

function openImport(): void {
  importJson.value = ''
  importVisible.value = true
}

async function handleImport(): Promise<void> {
  let items: SitePayload[]
  try {
    items = JSON.parse(importJson.value)
    if (!Array.isArray(items)) throw new Error('JSON 必须是数组')
  } catch (e) {
    message.error('JSON 格式无效：' + (e as Error).message)
    return
  }
  importing.value = true
  try {
    const result = await sitesApi.importSites(items)
    message.success(`已导入 ${result.importedCount} 个站点`)
    importVisible.value = false
    await loadSites()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    importing.value = false
  }
}

const columns = computed<DataTableColumns<SiteListItem>>(() => [
  { type: 'selection' },
  { title: '名称', key: 'name', minWidth: 140 },
  { title: '地址', key: 'baseUrl', minWidth: 220, ellipsis: { tooltip: true } },
  {
    title: '协议',
    key: 'protocol',
    width: 140,
    render: (row) => {
      const tags: Array<'success' | 'info' | 'warning'> = []
      if (row.protocolType === 'Responses') tags.push('warning')
      else if (row.supportsAnthropic && !row.supportsOpenAi) tags.push('info')
      else tags.push('success')
      return h(NSpace, { size: 4 }, () => [
        h(NTag, { size: 'small', type: tags[0], bordered: false }, () => row.protocolType)
      ])
    }
  },
  { title: '密钥', key: 'apiKeyMasked', width: 140 },
  {
    title: '状态',
    key: 'isEnabled',
    width: 80,
    render: (row) =>
      h(NTag, { size: 'small', type: row.isEnabled ? 'success' : 'default', bordered: false }, () =>
        row.isEnabled ? '启用' : '禁用'
      )
  },
  {
    title: '操作',
    key: 'actions',
    width: 180,
    fixed: 'right',
    render: (row) =>
      h(NSpace, { size: 8 }, () => [
        h(NButton, { size: 'small', quaternary: true, onClick: () => openEdit(row) }, () => '编辑'),
        h(NButton, { size: 'small', quaternary: true, onClick: () => handleToggle(row) }, () =>
          row.isEnabled ? '禁用' : '启用'
        ),
        h(
          NPopconfirm,
          { onPositiveClick: () => handleDelete(row) },
          {
            trigger: () => h(NButton, { size: 'small', quaternary: true, type: 'error' }, () => '删除'),
            default: () => `确认删除站点「${row.name}」？关联映射和路由规则会一并清理。`
          }
        )
      ])
  }
])

onMounted(loadSites)
</script>

<template>
  <div class="page-container">
    <NCard>
      <template #header>
        <NSpace justify="space-between" align="center">
          <span>站点管理</span>
          <NSpace>
            <NButton
              v-if="checkedRowKeys.length > 0"
              type="error"
              quaternary
              @click="handleBulkDelete"
            >
              批量删除（{{ checkedRowKeys.length }}）
            </NButton>
            <NButton quaternary @click="handleExport">导出</NButton>
            <NButton quaternary @click="openImport">导入</NButton>
            <NButton type="primary" @click="openCreate">新建站点</NButton>
          </NSpace>
        </NSpace>
      </template>

      <NDataTable
        v-model:checked-row-keys="checkedRowKeys"
        :columns="columns"
        :data="sites"
        :loading="loading"
        :row-key="(row: SiteListItem) => row.id"
        :pagination="{ pageSize: 20 }"
        :scroll-x="900"
        striped
      />
    </NCard>

    <NModal
      v-model:show="showModal"
      :title="modalTitle"
      preset="card"
      style="width: 560px"
      :mask-closable="false"
    >
      <NForm label-placement="top">
        <NFormItem label="站点名称">
          <NInput v-model:value="form.name" placeholder="如：OpenAI 官方" />
        </NFormItem>
        <NFormItem label="基础地址">
          <NInput v-model:value="form.baseUrl" placeholder="https://api.openai.com" />
        </NFormItem>
        <NFormItem label="接口路径模式">
          <NSelect v-model:value="form.endpointPathMode" :options="endpointModeOptions" />
        </NFormItem>
        <NFormItem :label="isEditMode ? '密钥（留空保留原密钥）' : '密钥'">
          <NInput
            v-model:value="form.apiKey"
            type="password"
            show-password-on="click"
            placeholder="sk-..."
          />
        </NFormItem>
        <NFormItem label="协议支持">
          <NSpace>
            <NSwitch v-model:value="form.supportsOpenAi" /> OpenAI
            <NSwitch v-model:value="form.supportsAnthropic" /> Anthropic
          </NSpace>
        </NFormItem>
        <NFormItem label="启用">
          <NSwitch v-model:value="form.isEnabled" />
        </NFormItem>
      </NForm>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="showModal = false">取消</NButton>
          <NButton type="primary" :loading="saving" @click="handleSave">保存</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 导入站点 -->
    <NModal v-model:show="importVisible" title="导入站点" preset="card" style="width: 560px" :mask-closable="false">
      <NFormItem label="粘贴站点 JSON 数组" :show-feedback="false">
        <NInput
          v-model:value="importJson"
          type="textarea"
          placeholder='[{"name":"...","baseUrl":"...","apiKey":"sk-...","supportsOpenAi":true}]'
          :autosize="{ minRows: 8, maxRows: 16 }"
          style="font-family: monospace"
        />
      </NFormItem>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="importVisible = false">取消</NButton>
          <NButton type="primary" :loading="importing" @click="handleImport">导入</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>
