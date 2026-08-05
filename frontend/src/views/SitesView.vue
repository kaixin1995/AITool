<script setup lang="ts">
import { computed, h, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import {
  NCard, NButton, NSpace, NDataTable, NTag, NModal, NForm, NFormItem, NInput,
  NSwitch, NPopconfirm, NSelect, NCheckbox, NProgress, useMessage, type DataTableColumns
} from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as sitesApi from '@/api/sites'
import type { ModelSelectionItem, SiteFetchResult, SiteListItem, SitePayload } from '@/api/sites'
import {
  buildSelectedSitesExportJson,
  parseSitesImportText,
  updateSitesSelection,
  type SiteExportItem,
  type SiteImportPreviewItem
} from './sitesState'

const message = useMessage()
const route = useRoute()
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

watch(
  () => route.query.action,
  (action) => {
    if (action === 'create') openCreate()
  },
  { immediate: true }
)

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

// 导入/导出：保留旧页面的预览、选择、复制、下载和 JSON 文件导入流程。
const importVisible = ref(false)
const importJson = ref('')
const importPreviewItems = ref<SiteImportPreviewItem[]>([])
const importing = ref(false)
const exportVisible = ref(false)
const exportLoading = ref(false)
const exportItems = ref<SiteExportItem[]>([])
const exportSelectedIds = ref<string[]>([])

const selectedImportCount = computed(() => importPreviewItems.value.filter((item) => item.selected).length)
const exportJson = computed(() => buildSelectedSitesExportJson(exportItems.value, exportSelectedIds.value))
const allExportSelected = computed(() => exportItems.value.length > 0 && exportSelectedIds.value.length === exportItems.value.length)

async function copyText(text: string): Promise<boolean> {
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text)
      return true
    } catch {
      // HTTPS 剪贴板权限不可用时继续使用兼容复制路径。
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
    return document.execCommand('copy')
  } finally {
    document.body.removeChild(textarea)
  }
}

async function handleExport(): Promise<void> {
  exportVisible.value = true
  exportLoading.value = true
  try {
    const items = await sitesApi.exportSites() as SiteExportItem[]
    exportItems.value = items
    exportSelectedIds.value = items.map((item) => item.id)
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    exportLoading.value = false
  }
}

function toggleAllExportSites(value: boolean): void {
  exportSelectedIds.value = value ? exportItems.value.map((item) => item.id) : []
}

async function copyExportJson(): Promise<void> {
  if (await copyText(exportJson.value)) message.success('JSON 已复制')
  else message.error('复制失败，请手动复制')
}

function downloadExportJson(): void {
  const blob = new Blob([exportJson.value], { type: 'application/json' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = `sites_export_${new Date().toISOString().slice(0, 10)}.json`
  a.click()
  URL.revokeObjectURL(url)
  message.success(`已导出 ${exportSelectedIds.value.length} 个站点`)
}

function openImport(): void {
  importJson.value = ''
  importPreviewItems.value = []
  importVisible.value = true
}

function parseImportPreview(): void {
  const result = parseSitesImportText(importJson.value)
  if (result.error) {
    message.error(result.error)
    return
  }
  importPreviewItems.value = result.items
  message.success(`已解析 ${result.items.length} 条站点`)
}

function clearImportPreview(): void {
  importJson.value = ''
  importPreviewItems.value = []
}

function updateImportSelected(index: number, selected: boolean): void {
  importPreviewItems.value = updateSitesSelection(importPreviewItems.value, index, selected) as SiteImportPreviewItem[]
}

function toggleAllImportSites(value: boolean): void {
  importPreviewItems.value = importPreviewItems.value.map((item) => ({ ...item, selected: value }))
}

function handleImportFile(event: Event): void {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  const reader = new FileReader()
  reader.onload = () => {
    importJson.value = String(reader.result || '')
    parseImportPreview()
  }
  reader.readAsText(file)
  input.value = ''
}

async function handleImport(): Promise<void> {
  if (importPreviewItems.value.length === 0) parseImportPreview()
  const items = importPreviewItems.value
    .filter((item) => item.selected)
    .map(({ selected: _selected, protocolType: _protocolType, ...item }) => item)
  if (items.length === 0) {
    message.warning('请至少选择一条数据')
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

// 远端模型目录拉取/导入：恢复历史站点页的一键拉取、单站拉取、筛选和别名编辑流程。
const catalogVisible = ref(false)
const catalogLoading = ref(false)
const catalogImporting = ref(false)
const catalogSearch = ref('')
const catalogTaskId = ref('')
const catalogProgress = ref({ total: 0, completed: 0 })
const catalogSites = ref<SiteFetchResult[]>([])
const catalogSelections = ref<ModelSelectionItem[]>([])
let catalogTimer: number | undefined

const catalogProgressPercent = computed(() => {
  if (catalogProgress.value.total === 0) return 0
  return Math.round((catalogProgress.value.completed / catalogProgress.value.total) * 100)
})

const filteredCatalogSites = computed(() => {
  const keyword = catalogSearch.value.trim().toLowerCase()
  if (!keyword) return catalogSites.value
  return catalogSites.value
    .map((site) => ({
      ...site,
      models: site.models.filter((model) =>
        `${model.remoteModelName} ${model.existingDisplayName ?? ''} ${site.siteName}`.toLowerCase().includes(keyword)
      )
    }))
    .filter((site) => site.models.length > 0 || site.status === 'fail')
})

const selectedCatalogCount = computed(() => catalogSelections.value.filter((item) => item.selected).length)
const selectableCatalogCount = computed(() => catalogSelections.value.length)
const allCatalogSelected = computed(() => selectableCatalogCount.value > 0 && selectedCatalogCount.value === selectableCatalogCount.value)

function selectionFor(siteId: string, remoteModelName: string): ModelSelectionItem | undefined {
  return catalogSelections.value.find((item) => item.siteId === siteId && item.remoteModelName === remoteModelName)
}

function applyCatalogSites(results: SiteFetchResult[]): void {
  catalogSites.value = results
  catalogSelections.value = results.flatMap((site) => site.models.map((model) => ({
    siteId: site.siteId,
    remoteModelName: model.remoteModelName,
    displayName: model.existingDisplayName || model.remoteModelName,
    selected: !model.existingMappingId || model.isEnabled
  })))
}

function openCatalog(results: SiteFetchResult[]): void {
  applyCatalogSites(results)
  catalogSearch.value = ''
  catalogVisible.value = true
}

async function handleFetchModels(row: SiteListItem): Promise<void> {
  catalogLoading.value = true
  catalogTaskId.value = ''
  catalogProgress.value = { total: 1, completed: 0 }
  try {
    const result = await sitesApi.fetchSiteModels(row.id)
    if (!Array.isArray(result)) {
      openCatalog([{ siteId: row.id, siteName: row.name, status: 'fail', error: result.message, models: [] }])
      return
    }
    openCatalog([{ siteId: row.id, siteName: row.name, status: 'success', models: result }])
  } finally {
    catalogLoading.value = false
    catalogProgress.value = { total: 0, completed: 0 }
  }
}

async function handleFetchAllModels(): Promise<void> {
  catalogLoading.value = true
  catalogVisible.value = true
  catalogSites.value = []
  catalogSelections.value = []
  try {
    const result = await sitesApi.fetchAllSiteModels()
    if (!result.taskId) {
      message.warning(result.message || '没有可拉取的站点')
      catalogLoading.value = false
      return
    }
    catalogTaskId.value = result.taskId
    await pollCatalogProgress()
    catalogTimer = window.setInterval(() => { void pollCatalogProgress() }, 1200)
  } catch (e) {
    message.error((e as Error).message)
    catalogLoading.value = false
  }
}

async function pollCatalogProgress(): Promise<void> {
  if (!catalogTaskId.value) return
  const progress = await sitesApi.getFetchAllProgress(catalogTaskId.value)
  catalogProgress.value = { total: progress.totalSites, completed: progress.completedSites }
  applyCatalogSites(progress.sites)
  if (progress.isCompleted) {
    catalogLoading.value = false
    if (catalogTimer) {
      window.clearInterval(catalogTimer)
      catalogTimer = undefined
    }
  }
}

function toggleAllCatalog(value: boolean): void {
  catalogSelections.value.forEach((item) => { item.selected = value })
}

function updateCatalogSelected(siteId: string, remoteModelName: string, selected: boolean): void {
  const item = selectionFor(siteId, remoteModelName)
  if (item) item.selected = selected
}

function updateCatalogDisplayName(siteId: string, remoteModelName: string, displayName: string): void {
  const item = selectionFor(siteId, remoteModelName)
  if (item) item.displayName = displayName
}

async function copyCatalogError(text: string | null | undefined): Promise<void> {
  if (!text) return
  await navigator.clipboard.writeText(text)
  message.success('错误信息已复制')
}

async function handleImportSelectedModels(): Promise<void> {
  if (selectedCatalogCount.value === 0) {
    message.warning('请至少选择一个模型')
    return
  }
  catalogImporting.value = true
  try {
    const result = await sitesApi.importSelectedModels(catalogSelections.value)
    message.success(`已导入 ${result.importedCount} 个模型`)
    catalogVisible.value = false
  } finally {
    catalogImporting.value = false
  }
}

function handleCatalogClosed(): void {
  if (catalogTimer) {
    window.clearInterval(catalogTimer)
    catalogTimer = undefined
  }
}

function formatDateTime(value: string): string {
  if (!value) return '-'
  return new Date(value).toLocaleString('zh-CN', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', hour12: false
  })
}

const columns = computed<DataTableColumns<SiteListItem>>(() => [
  { type: 'selection', width: 44 },
  { title: '名称', key: 'name', width: 150, ellipsis: { tooltip: true }, render: (row) => h('strong', row.name) },
  { title: '根地址', key: 'baseUrl', minWidth: 320, ellipsis: { tooltip: true }, render: (row) => h('code', { class: 'site-url' }, row.baseUrl) },
  {
    title: '协议',
    key: 'protocol',
    width: 170,
    render: (row) => {
      return h(NSpace, { size: 4, wrap: false }, () => [
        row.supportsOpenAi ? h(NTag, { size: 'small', type: 'success', bordered: false }, () => 'OpenAI') : null,
        row.supportsAnthropic ? h(NTag, { size: 'small', type: 'info', bordered: false }, () => 'Anthropic') : null,
        !row.supportsOpenAi && !row.supportsAnthropic ? h(NTag, { size: 'small', type: 'warning', bordered: false }, () => row.protocolType || 'Responses') : null
      ])
    }
  },
  {
    title: '状态',
    key: 'isEnabled',
    width: 80,
    render: (row) =>
      h(NTag, { size: 'small', type: row.isEnabled ? 'success' : 'default', bordered: false }, () =>
        row.isEnabled ? '启用' : '禁用'
      )
  },
  { title: '创建时间', key: 'createdAt', width: 150, render: (row) => formatDateTime(row.createdAt) },
  {
    title: '操作',
    key: 'actions',
    width: 286,
    fixed: 'right',
    render: (row) =>
      h(NSpace, { size: 6, wrap: false, class: 'site-actions' }, () => [
        h(NButton, { size: 'small', secondary: true, disabled: !row.isEnabled, loading: catalogLoading.value, onClick: () => handleFetchModels(row) }, () => '拉取'),
        h(NButton, { size: 'small', secondary: true, onClick: () => openEdit(row) }, () => '编辑'),
        h(NButton, { size: 'small', secondary: true, onClick: () => handleToggle(row) }, () =>
          row.isEnabled ? '禁用' : '启用'
        ),
        h(
          NPopconfirm,
          { onPositiveClick: () => handleDelete(row) },
          {
            trigger: () => h(NButton, { size: 'small', secondary: true, type: 'error' }, () => '删除'),
            default: () => `确认删除站点「${row.name}」？关联映射和路由规则会一并清理。`
          }
        )
      ])
  }
])

onMounted(loadSites)
onBeforeUnmount(handleCatalogClosed)
</script>

<template>
  <div class="page-container sites-page">
    <PageHeader title="站点管理" subtitle="管理所有代理站点及其配置">
      <template #actions>
        <NButton type="success" secondary :loading="catalogLoading" @click="handleFetchAllModels">一键拉取全部</NButton>
        <NButton secondary type="success" @click="handleExport">导出</NButton>
        <NButton secondary type="primary" @click="openImport">导入</NButton>
        <NButton type="primary" @click="openCreate">＋ 新增站点</NButton>
      </template>
    </PageHeader>

    <NCard>
      <div class="site-bulk-toolbar">
        <NPopconfirm @positive-click="handleBulkDelete">
          <template #trigger>
            <NButton size="small" type="error" secondary :disabled="checkedRowKeys.length === 0">批量删除（{{ checkedRowKeys.length }}）</NButton>
          </template>
          确认批量删除选中的 {{ checkedRowKeys.length }} 个站点？关联映射和路由规则会一并清理。
        </NPopconfirm>
      </div>
      <NDataTable
        v-model:checked-row-keys="checkedRowKeys"
        :columns="columns"
        :data="sites"
        :loading="loading"
        :row-key="(row: SiteListItem) => row.id"
        :pagination="{ pageSize: 20 }"
        :scroll-x="1180"
        size="small"
        striped
      />
    </NCard>

    <NModal
      v-model:show="showModal"
      :title="modalTitle"
      preset="card"
      style="width: 560px; max-width: 92vw"
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
          <NSpace vertical :size="6">
            <NSpace>
              <NSwitch v-model:value="form.supportsOpenAi" /> OpenAI
              <NSwitch v-model:value="form.supportsAnthropic" /> Anthropic
            </NSpace>
            <span class="site-form-tip">如果两个都不勾选，则按仅支持 Responses 的站点处理。</span>
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

    <!-- 导出站点 -->
    <NModal v-model:show="exportVisible" title="导出站点" preset="card" style="width: 860px; max-width: 94vw" :mask-closable="false">
      <NSpace vertical size="large">
        <p class="site-modal-tip">共 {{ exportItems.length }} 个站点，导出包含站点名称、地址、接口路径模式、密钥和支持协议。</p>
        <div v-if="exportItems.length > 0" class="site-preview-table-wrap">
          <table class="site-preview-table">
            <thead>
              <tr>
                <th><NCheckbox :checked="allExportSelected" @update:checked="toggleAllExportSites" /></th>
                <th>站点名称</th>
                <th>站点地址</th>
                <th>接口路径</th>
                <th>密钥</th>
                <th>支持协议</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="site in exportItems" :key="site.id">
                <td><NCheckbox :checked="exportSelectedIds.includes(site.id)" @update:checked="(value) => exportSelectedIds = value ? [...exportSelectedIds, site.id] : exportSelectedIds.filter(id => id !== site.id)" /></td>
                <td><strong>{{ site.name }}</strong></td>
                <td><code>{{ site.baseUrl }}</code></td>
                <td>{{ site.endpointPathMode === 'versioned-base' ? '不补 /v1' : '自动补 /v1' }}</td>
                <td><code>{{ site.apiKey.length > 8 ? site.apiKey.slice(0, 8) + '****' : '****' }}</code></td>
                <td>
                  <NSpace size="small">
                    <NTag v-if="site.supportsOpenAi" size="small" type="success" :bordered="false">OpenAI</NTag>
                    <NTag v-if="site.supportsAnthropic" size="small" type="info" :bordered="false">Anthropic</NTag>
                    <NTag v-if="!site.supportsOpenAi && !site.supportsAnthropic" size="small" type="warning" :bordered="false">Responses</NTag>
                  </NSpace>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <NInput v-if="exportItems.length > 0" :value="exportJson" type="textarea" readonly :autosize="{ minRows: 8, maxRows: 14 }" class="site-json-preview" />
        <div v-else-if="!exportLoading" class="site-empty">暂无站点数据可导出</div>
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="exportVisible = false">关闭</NButton>
          <NButton :disabled="exportSelectedIds.length === 0" @click="copyExportJson">复制 JSON</NButton>
          <NButton type="primary" :loading="exportLoading" :disabled="exportSelectedIds.length === 0" @click="downloadExportJson">下载文件</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 导入站点 -->
    <NModal v-model:show="importVisible" title="导入站点" preset="card" style="width: 900px; max-width: 94vw" :mask-closable="false">
      <NSpace vertical size="large">
        <div>
          <div class="site-form-title">粘贴数据</div>
          <p class="site-modal-tip">支持 TSV：站点名称[TAB]站点地址[TAB]密钥，也支持直接粘贴 JSON 数组。TSV 默认使用“自动补 /v1”。</p>
          <NInput
            v-model:value="importJson"
            type="textarea"
            placeholder="site_name&#9;site_url&#9;Key&#10;示例站点&#9;https://api.example.com&#9;sk-xxxx"
            :autosize="{ minRows: 8, maxRows: 14 }"
            class="site-json-preview"
          />
          <NSpace class="site-import-actions">
            <NButton secondary type="primary" @click="parseImportPreview">解析预览</NButton>
            <NButton secondary @click="clearImportPreview">清空</NButton>
          </NSpace>
        </div>
        <div>
          <div class="site-form-title">上传 JSON 文件</div>
          <p class="site-modal-tip">选择从导出页下载的 JSON 文件，自动解析并导入。</p>
          <input type="file" accept=".json,application/json" class="site-file-input" @change="handleImportFile">
        </div>
        <div v-if="importPreviewItems.length > 0">
          <div class="site-form-title">解析预览 <NTag size="small" type="info" :bordered="false">{{ importPreviewItems.length }} 条</NTag></div>
          <div class="site-preview-table-wrap">
            <table class="site-preview-table">
              <thead>
                <tr>
                  <th><NCheckbox :checked="selectedImportCount === importPreviewItems.length" @update:checked="toggleAllImportSites" /></th>
                  <th>站点名称</th>
                  <th>站点地址</th>
                  <th>接口路径</th>
                  <th>访问密钥</th>
                  <th>协议类型</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(site, index) in importPreviewItems" :key="`${site.name}-${index}`">
                  <td><NCheckbox :checked="site.selected" @update:checked="(value) => updateImportSelected(index, value)" /></td>
                  <td><strong>{{ site.name }}</strong></td>
                  <td><code>{{ site.baseUrl }}</code></td>
                  <td>{{ site.endpointPathMode === 'versioned-base' ? '不补 /v1' : '自动补 /v1' }}</td>
                  <td><code>{{ site.apiKey.slice(0, 8) }}****</code></td>
                  <td><NTag size="small" type="info" :bordered="false">{{ site.protocolType }}</NTag></td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="importVisible = false">取消</NButton>
          <NButton type="primary" :loading="importing" :disabled="selectedImportCount === 0" @click="handleImport">确认导入</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 模型目录拉取与导入 -->
    <NModal v-model:show="catalogVisible" title="模型目录" preset="card" style="width: 980px; max-width: 94vw" @after-leave="handleCatalogClosed">
      <NSpace vertical size="large">
        <div v-if="catalogLoading || catalogProgress.total > 0" class="catalog-progress">
          <div class="catalog-progress-title">拉取进度：{{ catalogProgress.completed }} / {{ catalogProgress.total }}</div>
          <NProgress type="line" :percentage="catalogProgressPercent" :show-indicator="false" />
        </div>
        <div class="catalog-toolbar">
          <NInput v-model:value="catalogSearch" clearable placeholder="搜索模型、别名或站点" />
          <NSpace align="center" :wrap="false">
            <NCheckbox :checked="allCatalogSelected" @update:checked="toggleAllCatalog">全选</NCheckbox>
            <NTag size="small" :bordered="false">已选 {{ selectedCatalogCount }} / {{ selectableCatalogCount }}</NTag>
          </NSpace>
        </div>
        <div class="catalog-site-list">
          <NCard v-for="site in filteredCatalogSites" :key="site.siteId" class="catalog-site-card" size="small">
            <template #header>
              <NSpace align="center" :wrap="false">
                <span>{{ site.siteName }}</span>
                <NTag size="small" :type="site.status === 'success' ? 'success' : site.status === 'fail' ? 'error' : 'warning'" :bordered="false">{{ site.status }}</NTag>
                <span class="catalog-count">{{ site.models.length }} 个模型</span>
              </NSpace>
            </template>
            <template v-if="site.status === 'fail'">
              <div class="catalog-error">
                <span>{{ site.error || '拉取失败' }}</span>
                <NButton size="tiny" tertiary @click="copyCatalogError(site.error)">复制错误</NButton>
              </div>
            </template>
            <div v-else class="catalog-model-list">
              <div v-for="model in site.models" :key="model.remoteModelName" class="catalog-model-row">
                <NCheckbox
                  :checked="selectionFor(site.siteId, model.remoteModelName)?.selected"
                  @update:checked="(value) => updateCatalogSelected(site.siteId, model.remoteModelName, value)"
                />
                <code class="catalog-remote-name">{{ model.remoteModelName }}</code>
                <NInput
                  :value="selectionFor(site.siteId, model.remoteModelName)?.displayName"
                  size="small"
                  placeholder="显示名称 / 别名"
                  @update:value="(value) => updateCatalogDisplayName(site.siteId, model.remoteModelName, value)"
                />
                <NTag v-if="model.existingMappingId" size="small" :type="model.isEnabled ? 'success' : 'default'" :bordered="false">{{ model.isEnabled ? '已导入' : '已禁用' }}</NTag>
              </div>
            </div>
          </NCard>
          <NEmpty v-if="!catalogLoading && filteredCatalogSites.length === 0" description="暂无可导入模型" />
        </div>
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="catalogVisible = false">取消</NButton>
          <NButton type="primary" :disabled="selectedCatalogCount === 0" :loading="catalogImporting" @click="handleImportSelectedModels">导入选中</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.sites-page {
  min-width: 0;
}

.sites-page :deep(.n-card__content) {
  min-width: 0;
  overflow: hidden;
}

.site-url {
  display: inline-block;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: bottom;
  white-space: nowrap;
}

.site-bulk-toolbar {
  display: flex;
  justify-content: flex-end;
  margin-bottom: 12px;
}

.site-form-tip {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.site-actions {
  white-space: nowrap;
}

.site-actions :deep(.n-button) {
  flex-shrink: 0;
}

.site-form-title {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
  font-weight: 600;
}

.site-modal-tip {
  margin: 0 0 12px;
  color: var(--text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}

.site-import-actions {
  margin-top: 12px;
}

.site-file-input {
  width: 100%;
  padding: 8px;
  border: 1px solid var(--border-color-global);
  border-radius: 6px;
  background: var(--bg-input);
  color: var(--text-primary);
}

.site-preview-table-wrap {
  max-height: 320px;
  overflow: auto;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
}

.site-preview-table {
  width: 100%;
  min-width: 760px;
  border-collapse: collapse;
  font-size: 13px;
}

.site-preview-table th,
.site-preview-table td {
  padding: 9px 10px;
  border-bottom: 1px solid var(--border-color-global);
  text-align: left;
  vertical-align: middle;
}

.site-preview-table th {
  background: var(--bg-input);
  color: var(--text-color-secondary);
  font-weight: 600;
}

.site-preview-table code {
  word-break: break-all;
}

.site-json-preview :deep(textarea) {
  font-family: ui-monospace, SFMono-Regular, Consolas, 'Liberation Mono', monospace;
  font-size: 12.5px;
}

.site-empty {
  padding: 28px;
  border: 1px dashed var(--border-color-global);
  border-radius: 8px;
  color: var(--text-color-secondary);
  text-align: center;
}

.catalog-progress-title,
.catalog-count {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.catalog-toolbar {
  display: grid;
  grid-template-columns: minmax(220px, 1fr) auto;
  gap: 12px;
  align-items: center;
}

.catalog-site-list {
  display: grid;
  gap: 12px;
  max-height: 58vh;
  overflow: auto;
}

.catalog-site-card :deep(.n-card__content) {
  padding-top: 0;
}

.catalog-error {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  color: var(--error-color);
  word-break: break-all;
}

.catalog-model-list {
  display: grid;
  gap: 8px;
}

.catalog-model-row {
  display: grid;
  grid-template-columns: auto minmax(180px, 1fr) minmax(180px, 1fr) auto;
  gap: 8px;
  align-items: center;
  min-width: 0;
}

.catalog-remote-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

@media (max-width: 768px) {
  .catalog-toolbar,
  .catalog-model-row {
    grid-template-columns: 1fr;
  }
}
</style>
