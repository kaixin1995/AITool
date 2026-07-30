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

    <!-- 导入站点 -->
    <NModal v-model:show="importVisible" title="导入站点" preset="card" style="width: 560px; max-width: 92vw" :mask-closable="false">
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
