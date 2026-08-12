<script setup lang="ts">
import { computed, h, onMounted, ref } from 'vue'
import { NCard, NButton, NSpace, NDataTable, NTag, NModal, NForm, NFormItem, NInput, NPopconfirm, NAlert, NCheckbox, NCheckboxGroup, NEmpty, useMessage, type DataTableColumns } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/accessKeys'
import * as routesApi from '@/api/routes'
import type { AccessKeyItem } from '@/api/accessKeys'

const message = useMessage()
const loading = ref(false)
const items = ref<AccessKeyItem[]>([])
const showModal = ref(false)
const newKeyName = ref('')
const newKeyPlain = ref('')
const newKeyRoutes = ref<string[]>([])
const saving = ref(false)
const copyingKeyId = ref<string | null>(null)

// 可用路由入口列表（供权限选择）
const routeEntries = ref<string[]>([])
const routeEntriesLoading = ref(false)
const routeEntriesReady = ref(false)
const routeEntriesError = ref('')

// 编辑路由权限弹窗
const editRoutesVisible = ref(false)
const editKey = ref<AccessKeyItem | null>(null)
const editRoutes = ref<string[]>([])
const editSaving = ref(false)

async function loadRouteEntries(): Promise<void> {
  routeEntriesLoading.value = true
  routeEntriesReady.value = false
  routeEntriesError.value = ''
  try {
    const entries = await routesApi.getRouteEntries()
    routeEntries.value = entries.map((entry) => entry.entryName)
    routeEntriesReady.value = true
  } catch {
    routeEntries.value = []
    routeEntriesError.value = '路由入口加载失败。为避免误将密钥设置为允许全部路由，创建和编辑权限已暂时禁用。'
  } finally {
    routeEntriesLoading.value = false
  }
}

async function load(): Promise<void> {
  loading.value = true
  try {
    const [keys] = await Promise.all([
      api.listAccessKeys(),
      loadRouteEntries()
    ])
    items.value = keys
  } finally { loading.value = false }
}

function openCreate(): void {
  if (!routeEntriesReady.value) {
    message.error(routeEntriesError.value || '路由入口尚未加载完成')
    return
  }
  newKeyName.value = ''
  newKeyPlain.value = ''
  newKeyRoutes.value = []
  showModal.value = true
}

async function handleCreate(): Promise<void> {
  if (!routeEntriesReady.value) { message.error('路由入口未成功加载，暂不能创建密钥'); return }
  if (!newKeyName.value.trim()) { message.warning('请输入密钥名称'); return }
  saving.value = true
  try {
    const result = await api.createAccessKey(newKeyName.value.trim(), newKeyRoutes.value)
    newKeyPlain.value = result.plainKey
    message.success('密钥已创建，请立即复制（仅展示一次）')
    await load()
  } finally { saving.value = false }
}

async function copyText(text: string, successMessage = '已复制到剪贴板'): Promise<void> {
  if (!text) return
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text)
      message.success(successMessage)
      return
    } catch {
      // HTTP 或浏览器权限受限时走传统复制路径。
    }
  }

  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.style.position = 'fixed'
  textarea.style.left = '-9999px'
  textarea.style.opacity = '0'
  document.body.appendChild(textarea)
  textarea.focus()
  textarea.select()
  try {
    document.execCommand('copy')
    message.success(successMessage)
  } catch {
    message.error('复制失败，请手动复制')
  } finally {
    document.body.removeChild(textarea)
  }
}

async function handleCopyPlain(row: AccessKeyItem): Promise<void> {
  if (copyingKeyId.value) return
  copyingKeyId.value = row.id
  try {
    const { plainKey } = await api.getAccessKeyPlain(row.id)
    await copyText(plainKey, '已复制完整密钥')
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    copyingKeyId.value = null
  }
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

// 编辑路由权限
function openEditRoutes(row: AccessKeyItem): void {
  if (!routeEntriesReady.value) {
    message.error(routeEntriesError.value || '路由入口尚未加载完成')
    return
  }
  editKey.value = row
  const val = row.allowedRouteNames
  editRoutes.value = Array.isArray(val) ? val : (typeof val === 'string' && val.trim() ? (() => { try { return JSON.parse(val) as string[] } catch { return [] } })() : [])
  editRoutesVisible.value = true
}

function selectAllRoutes(): void {
  editRoutes.value = [...routeEntries.value]
}

function clearSelectedRoutes(): void {
  editRoutes.value = []
}

const editRoutesLabel = computed(() => {
  if (editRoutes.value.length === 0) return '当前: 全部'
  if (editRoutes.value.length <= 3) return `当前: ${editRoutes.value.join(', ')}`
  return `当前: 已选择 ${editRoutes.value.length} 个路由`
})

async function handleSaveRoutes(): Promise<void> {
  if (!editKey.value) return
  if (!routeEntriesReady.value) { message.error('路由入口未成功加载，暂不能保存权限'); return }
  editSaving.value = true
  try {
    await api.updateAccessKeyRoutes(editKey.value.id, editRoutes.value)
    message.success('路由权限已更新')
    editRoutesVisible.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { editSaving.value = false }
}

// 解析 allowedRouteNames（后端返回 string[] 或 JSON 字符串）
function parseRoutes(val: AccessKeyItem['allowedRouteNames']): string[] {
  if (Array.isArray(val)) return val
  if (typeof val === 'string' && val.trim()) { try { return JSON.parse(val) as string[] } catch { return [] } }
  return []
}

const columns = computed<DataTableColumns<AccessKeyItem>>(() => [
  { title: '名称', key: 'keyName', width: 120, ellipsis: { tooltip: true } },
  { title: '密钥', key: 'maskedValue', minWidth: 170, ellipsis: { tooltip: true } },
  { title: '允许路由', key: 'allowedRouteNames', minWidth: 120, render: (r) => {
    const arr = parseRoutes(r.allowedRouteNames)
    return arr.length > 0 ? h(NTag, { size: 'small', bordered: false }, () => arr.join(', ')) : '全部'
  } },
  { title: '状态', key: 'isEnabled', width: 80, render: (r) => h(NTag, { size: 'small', type: r.isEnabled ? 'success' : 'default', bordered: false }, () => r.isEnabled ? '启用' : '禁用') },
  { title: '操作', key: 'actions', width: 292, fixed: 'right', render: (row) => h(NSpace, { size: 6, wrap: false }, () => [
    h(NButton, { size: 'small', quaternary: true, loading: copyingKeyId.value === row.id, onClick: () => handleCopyPlain(row) }, () => '复制密钥'),
    h(NButton, { size: 'small', quaternary: true, disabled: !routeEntriesReady.value, onClick: () => openEditRoutes(row) }, () => '编辑路由'),
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
        <NButton type="primary" :disabled="!routeEntriesReady" @click="openCreate">新建密钥</NButton>
      </template>
    </PageHeader>
    <NAlert
      v-if="routeEntriesError"
      type="error"
      :show-icon="true"
      style="margin-bottom: 16px"
    >
      <div class="route-load-error">
        <span>{{ routeEntriesError }}</span>
        <NButton size="small" :loading="routeEntriesLoading" @click="loadRouteEntries">重试</NButton>
      </div>
    </NAlert>
    <NCard>
      <NDataTable :columns="columns" :data="items" :loading="loading" :row-key="(r: AccessKeyItem) => r.id" :pagination="{ pageSize: 20 }" striped />
    </NCard>

    <NModal v-model:show="showModal" title="新建访问密钥" preset="card" style="width: 480px; max-width: 92vw" :mask-closable="false">
      <NAlert v-if="newKeyPlain" type="success" :show-icon="true" style="margin-bottom: 16px">
        <div class="created-key-alert">
          <div class="created-key-title">密钥已创建（仅展示一次，请立即复制）：</div>
          <NInput :value="newKeyPlain" readonly type="textarea" :autosize="{ minRows: 2 }" />
          <NSpace justify="end">
            <NButton size="small" type="primary" secondary @click="copyText(newKeyPlain)">复制</NButton>
          </NSpace>
        </div>
      </NAlert>
      <NForm v-else label-placement="top">
        <NFormItem label="密钥名称">
          <NInput v-model:value="newKeyName" placeholder="如：生产环境密钥" @keyup.enter="handleCreate" />
        </NFormItem>
        <NFormItem v-if="routeEntries.length > 0" label="允许路由（不选=全部）">
          <NCheckboxGroup v-model:value="newKeyRoutes">
            <NSpace>
              <NCheckbox v-for="r in routeEntries" :key="r" :value="r" :label="r" />
            </NSpace>
          </NCheckboxGroup>
        </NFormItem>
      </NForm>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="showModal = false">关闭</NButton>
          <NButton v-if="!newKeyPlain" type="primary" :loading="saving" @click="handleCreate">创建</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 编辑路由权限弹窗 -->
    <NModal v-model:show="editRoutesVisible" :title="`编辑路由权限 - ${editKey?.keyName ?? ''}`" preset="card" style="width: 480px; max-width: 92vw">
      <NTag size="small" :bordered="false" style="margin-bottom: 12px">{{ editRoutesLabel }}</NTag>
      <p style="margin: 0 0 12px; color: var(--text-color-secondary); font-size: 13px">
        勾选该密钥允许访问的路由入口。不勾选任何项 = 允许全部路由（包括后续新增的）。
      </p>
      <NSpace size="small" style="margin-bottom: 12px">
        <NButton size="small" secondary type="primary" @click="selectAllRoutes">全选</NButton>
        <NButton size="small" secondary @click="clearSelectedRoutes">清空（=全部路由）</NButton>
      </NSpace>
      <NCheckboxGroup v-model:value="editRoutes">
        <div class="route-checkbox-grid">
          <NCheckbox v-for="r in routeEntries" :key="r" :value="r" :label="r" />
        </div>
      </NCheckboxGroup>
      <NEmpty v-if="routeEntries.length === 0" description="暂无路由入口" size="small" />
      <template #footer>
        <NSpace justify="end">
          <NButton @click="editRoutesVisible = false">取消</NButton>
          <NButton type="primary" :loading="editSaving" @click="handleSaveRoutes">保存</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.route-load-error {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.created-key-alert {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.created-key-title {
  font-weight: 600;
}

.route-checkbox-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px 12px;
  max-height: 360px;
  overflow-y: auto;
  padding: 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
}

@media (max-width: 640px) {
  .route-checkbox-grid {
    grid-template-columns: 1fr;
  }
}
</style>
