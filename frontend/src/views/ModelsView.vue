<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  NCard, NButton, NSpace, NTag, NModal, NForm, NFormItem, NInput, NInputNumber,
  NSwitch, NSelect, NPopconfirm, NEmpty, NSpin, NTabs, NTabPane, NTooltip, useMessage
} from 'naive-ui'
import * as modelsApi from '@/api/models'
import * as compatApi from '@/api/compatibility'
import type { ModelListItem, ModelPayload } from '@/api/models'
import { getHeaderProfiles, type HeaderProfile } from '@/api/headerProfiles'
import PageHeader from '@/components/PageHeader.vue'
import ModelPricingPanel from '@/components/ModelPricingPanel.vue'
import {
  buildVendorIconMarkup,
  extractSvgBody,
  removeVendorAt,
  renameVendor,
} from './models/vendorCatalogState'

export type ModelsTab = 'gallery' | 'rules' | 'pricing'

const message = useMessage()
const route = useRoute()
const router = useRouter()
const loading = ref(false)
const vendorGroups = ref<modelsApi.ModelVendorGroup[]>([])
const profileOptions = ref<{ label: string; value: string }[]>([])
const modelSearch = ref('')

function getModelsTabFromUrl(): ModelsTab {
  const hash = route.hash.replace(/^#/, '').toLowerCase()
  const queryTab = (typeof route.query.tab === 'string' ? route.query.tab : '').toLowerCase()
  const target = hash || queryTab
  if (target === 'rules' || target === 'vendor-rules') {
    return 'rules'
  }
  if (target === 'pricing' || target === 'price' || target === 'prices') {
    return 'pricing'
  }
  return 'gallery'
}

function getHashForTab(tab: ModelsTab): string {
  if (tab === 'rules') return '#rules'
  if (tab === 'pricing') return '#pricing'
  return '#gallery'
}

const activeTab = ref<ModelsTab>(getModelsTabFromUrl())

watch(activeTab, (tab) => {
  const nextHash = getHashForTab(tab)
  if (route.hash !== nextHash) {
    void router.replace({
      hash: nextHash,
      query: { ...route.query, tab: undefined }
    })
  }
})

watch(() => route.hash, () => {
  const tab = getModelsTabFromUrl()
  if (activeTab.value !== tab) {
    activeTab.value = tab
  }
})

watch(() => route.query.tab, () => {
  const tab = getModelsTabFromUrl()
  if (activeTab.value !== tab) {
    activeTab.value = tab
  }
})

const vendorRuleSearch = ref('')
const vendorCatalog = ref<modelsApi.ModelVendorCatalog>({ vendors: [], rules: [] })
const loadedVendorCatalog = ref<modelsApi.ModelVendorCatalog>({ vendors: [], rules: [] })
const savingVendorCatalog = ref(false)
const showVendorModal = ref(false)
const editingVendorIndex = ref(-1)

const matchTypeOptions = [
  { label: '精确', value: 'exact' },
  { label: '通配符 *', value: 'wildcard' },
  { label: '正则', value: 'regex' }
]

const showModal = ref(false)
const activeEditorTab = ref('basic')
const editingId = ref<string | null>(null)
const form = reactive<ModelPayload>({
  modelName: '',
  displayName: '',
  isEnabled: true,
  overrideReasoningEffort: '',
  compatibilityProfileId: null,
  clientEmulation: 'None',
  extraHeadersJson: ''
})
const saving = ref(false)
const headerProfiles = ref<HeaderProfile[]>([])

async function loadHeaderProfiles() {
  try {
    headerProfiles.value = await getHeaderProfiles()
  } catch {}
}

const clientEmulationOptions = computed(() => {
  const options = [{ label: '无 (None - 标准API直连)', value: 'None' }]
  if (headerProfiles.value && headerProfiles.value.length > 0) {
    for (const p of headerProfiles.value) {
      if (p.isEnabled) {
        options.push({
          label: p.isBuiltIn ? `⚡ [系统] ${p.name}` : `💡 [自定义] ${p.name}`,
          value: p.key
        })
      }
    }
  } else {
    options.push(
      { label: '⚡ Codex Desktop 官方客户端 (默认)', value: 'CodexCli' },
      { label: '⚡ Codex VS Code 插件', value: 'CodexVsCode' },
      { label: '⚡ OpenCode CLI 终端', value: 'OpenCode' },
      { label: '⚡ Claude Code 官方命令行', value: 'ClaudeCode' },
      { label: '⚡ ZCode / GLM 客户端', value: 'ZCode' },
      { label: '⚡ Google Antigravity CLI', value: 'Antigravity' }
    )
  }
  return options
})

// 映射管理（嵌入编辑弹窗，仅编辑模式可见）
const modelDetail = ref<modelsApi.ModelDetail | null>(null)
const newMappingSiteId = ref<string | null>(null)
const newMappingRemoteName = ref('')
const newMappingMaxConcurrency = ref(0)
const newMappingClientEmulation = ref('None')
const newMappingExtraHeadersJson = ref('')
const newMappingEnabled = ref(true)
const mappingLoading = ref(false)
const mappingUpdating = ref<Record<string, boolean>>({})
// 详情请求序号，避免快速切换模型时旧响应覆盖当前弹窗。
let modelDetailRequestId = 0

const isEditMode = computed(() => !!editingId.value)
const allModels = computed(() => vendorGroups.value.flatMap((g) => g.models))
const filteredVendorGroups = computed(() => {
  const keyword = modelSearch.value.trim().toLowerCase()
  return vendorGroups.value
    .map((group) => ({
      ...group,
      models: group.models.filter((model) => {
        if (!keyword) return true
        return `${model.modelName} ${group.vendorName}`.toLowerCase().includes(keyword)
      })
    }))
    .filter((group) => group.models.length > 0)
})
const visibleModelCount = computed(() => filteredVendorGroups.value.reduce((sum, group) => sum + group.models.length, 0))
const filteredVendors = computed(() => {
  const keyword = vendorRuleSearch.value.trim().toLowerCase()
  return vendorCatalog.value.vendors
    .map((vendor, index) => ({
      vendor,
      index,
      rules: vendorCatalog.value.rules.filter((rule) => rule.vendorName === vendor.vendorName)
    }))
    .filter(({ vendor, rules }) => {
      if (!keyword) return true
      return `${vendor.vendorName} ${rules.map((rule) => `${rule.matchType} ${rule.pattern}`).join(' ')}`
        .toLowerCase()
        .includes(keyword)
    })
})
const editingVendor = computed(() => vendorCatalog.value.vendors[editingVendorIndex.value] ?? null)
const editingVendorRules = computed(() => vendorCatalog.value.rules
  .map((rule, index) => ({ rule, index }))
  .filter(({ rule }) => rule.vendorName === editingVendor.value?.vendorName))
const editingVendorIcon = computed(() => buildVendorIconMarkup(editingVendor.value?.iconSvgBody ?? ''))

async function loadModels(): Promise<void> {
  loading.value = true
  try {
    const [resp, profiles, catalog] = await Promise.all([modelsApi.listModels(), compatApi.listProfiles(), modelsApi.getVendorCatalog()])
    vendorGroups.value = resp.vendorGroups
    loadedVendorCatalog.value = structuredClone(catalog)
    vendorCatalog.value = structuredClone(catalog)
    profileOptions.value = [
      { label: '无', value: '' },
      ...profiles.filter((profile) => profile.isEnabled).map((profile) => ({ label: profile.name, value: profile.id }))
    ]
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  activeEditorTab.value = 'basic'
  modelDetailRequestId++
  Object.assign(form, {
    modelName: '',
    displayName: '',
    isEnabled: true,
    overrideReasoningEffort: '',
    compatibilityProfileId: null,
    clientEmulation: 'None',
    extraHeadersJson: ''
  })
  // 新建模式不展示映射区块，清理可能残留的上一次编辑数据
  modelDetail.value = null
  newMappingSiteId.value = null
  newMappingRemoteName.value = ''
  newMappingMaxConcurrency.value = 0
  newMappingClientEmulation.value = 'None'
  newMappingExtraHeadersJson.value = ''
  newMappingEnabled.value = true
  mappingLoading.value = false
  loadHeaderProfiles()
  showModal.value = true
}

watch(
  () => route.query.action,
  (action) => {
    if (action === 'create') openCreate()
  },
  { immediate: true }
)

async function openEdit(model: ModelListItem): Promise<void> {
  editingId.value = model.id
  activeEditorTab.value = 'basic'
  // 切换模型时先清空旧详情，避免新请求完成前短暂显示上一个模型的映射。
  modelDetail.value = null
  Object.assign(form, {
    modelName: model.modelName,
    displayName: model.modelName,
    isEnabled: model.isEnabled,
    overrideReasoningEffort: model.overrideReasoningEffort,
    compatibilityProfileId: model.compatibilityProfileId,
    clientEmulation: 'None',
    extraHeadersJson: ''
  })
  loadHeaderProfiles()
  showModal.value = true
  // 同时加载映射数据，供弹窗内映射区块展示与编辑
  await loadModelDetail(model)
}

async function loadModelDetail(model: ModelListItem): Promise<void> {
  newMappingSiteId.value = null
  newMappingRemoteName.value = model.modelName
  newMappingMaxConcurrency.value = 0
  newMappingClientEmulation.value = 'None'
  newMappingExtraHeadersJson.value = ''
  newMappingEnabled.value = true
  await refreshModelDetail(model.id)
}

async function refreshModelDetail(modelId: string): Promise<void> {
  const requestId = ++modelDetailRequestId
  mappingLoading.value = true
  try {
    const detail = await modelsApi.getModelDetail(modelId)
    if (requestId === modelDetailRequestId && editingId.value === modelId) {
      modelDetail.value = detail
      // 后端兼容：旧响应缺 overrideReasoningEffort 时回退空串，避免表格输入绑定 undefined。
      for (const m of detail.siteMappings) {
        if (m.overrideReasoningEffort === undefined || m.overrideReasoningEffort === null) {
          m.overrideReasoningEffort = ''
        }
      }
      form.clientEmulation = detail.clientEmulation || 'None'
      form.extraHeadersJson = detail.extraHeadersJson || ''
    }
  } finally {
    if (requestId === modelDetailRequestId) {
      mappingLoading.value = false
    }
  }
}

async function handleSave(): Promise<void> {
  if (!form.modelName.trim()) {
    message.warning('模型名称不能为空')
    return
  }
  if (form.extraHeadersJson && form.extraHeadersJson.trim()) {
    try {
      const parsed = JSON.parse(form.extraHeadersJson.trim())
      if (typeof parsed !== 'object' || Array.isArray(parsed) || parsed === null) {
        message.warning('默认自定义请求头必须是合法的 JSON 对象，例如 {"User-Agent": "..."}')
        return
      }
    } catch {
      message.warning('默认自定义请求头 JSON 格式无效，请检查')
      return
    }
  }
  // 空字符串（选"无"）转 null，与后端 Guid? 一致。
  const payload = {
    ...form,
    displayName: form.modelName.trim(),
    compatibilityProfileId: form.compatibilityProfileId || null
  }
  saving.value = true
  try {
    if (editingId.value) {
      await modelsApi.updateModel(editingId.value, payload)
      message.success('模型已更新')
    } else {
      await modelsApi.createModel(payload)
      message.success('模型已创建')
    }
    showModal.value = false
    await loadModels()
  } finally {
    saving.value = false
  }
}

async function handleToggle(model: ModelListItem): Promise<void> {
  const result = await modelsApi.toggleModel(model.id)
  model.isEnabled = result.isEnabled
  message.success(`模型已${result.isEnabled ? '启用' : '禁用'}`)
}

async function handleDelete(model: ModelListItem): Promise<void> {
  await modelsApi.deleteModel(model.id)
  message.success('模型已删除')
  await loadModels()
}

async function handleClearAllModels(): Promise<void> {
  const result = await modelsApi.clearAllModels()
  message.success(`已清空 ${result.deletedModels} 个模型、${result.deletedMappings} 条映射`)
  await loadModels()
}

function restoreVendorCatalog(): void {
  vendorCatalog.value = structuredClone(loadedVendorCatalog.value)
  showVendorModal.value = false
  editingVendorIndex.value = -1
  message.success('已恢复为当前文件内容')
}

function openVendorEditor(index: number): void {
  editingVendorIndex.value = index
  showVendorModal.value = true
}

function addVendor(): void {
  vendorCatalog.value.vendors.push({
    vendorName: '',
    iconSvgBody: '',
    headerBackground: '#f8fafc',
    sortOrder: vendorCatalog.value.vendors.length * 10
  })
  openVendorEditor(vendorCatalog.value.vendors.length - 1)
}

function updateVendorName(value: string): void {
  renameVendor(vendorCatalog.value, editingVendorIndex.value, value)
}

function updateVendorIcon(value: string): void {
  if (editingVendor.value) editingVendor.value.iconSvgBody = extractSvgBody(value)
}

function deleteVendor(index: number): void {
  removeVendorAt(vendorCatalog.value, index)
  if (showVendorModal.value) showVendorModal.value = false
  editingVendorIndex.value = -1
}

function addVendorRule(): void {
  if (!editingVendor.value) return
  vendorCatalog.value.rules.push({
    vendorName: editingVendor.value.vendorName,
    matchType: 'wildcard',
    pattern: '',
    priority: (vendorCatalog.value.rules.length + 1) * 10
  })
}

function deleteVendorRule(index: number): void {
  vendorCatalog.value.rules.splice(index, 1)
}

function matchTypeLabel(matchType: string): string {
  if (matchType === 'exact') return '精确'
  if (matchType === 'regex') return '正则'
  return '通配符'
}

async function handleSaveVendorCatalog(): Promise<void> {
  const names = vendorCatalog.value.vendors.map((vendor) => vendor.vendorName.trim())
  if (names.some((name) => !name)) {
    message.warning('厂商名称不能为空')
    return
  }
  if (new Set(names.map((name) => name.toLowerCase())).size !== names.length) {
    message.warning('厂商名称不能重复')
    return
  }

  savingVendorCatalog.value = true
  try {
    await modelsApi.saveVendorCatalog(vendorCatalog.value)
    message.success('厂商规则已保存')
    showVendorModal.value = false
    await loadModels()
  } finally {
    savingVendorCatalog.value = false
  }
}

async function handleAddMapping(): Promise<void> {
  if (!editingId.value || !newMappingSiteId.value || !newMappingRemoteName.value.trim()) {
    message.warning('请选择站点并填写模型名')
    return
  }
  if (newMappingExtraHeadersJson.value && newMappingExtraHeadersJson.value.trim()) {
    try {
      const parsed = JSON.parse(newMappingExtraHeadersJson.value.trim())
      if (typeof parsed !== 'object' || Array.isArray(parsed) || parsed === null) {
        message.warning('自定义请求头必须是合法的 JSON 对象，例如 {"User-Agent": "..."}')
        return
      }
    } catch {
      message.warning('自定义请求头 JSON 格式无效，请检查')
      return
    }
  }
  const modelId = editingId.value
  try {
    await modelsApi.addModelMapping(
      modelId,
      newMappingSiteId.value,
      newMappingRemoteName.value.trim(),
      newMappingMaxConcurrency.value,
      newMappingClientEmulation.value,
      newMappingExtraHeadersJson.value.trim() || undefined,
      undefined,
      newMappingEnabled.value
    )
    message.success('站点关联已添加')
    if (editingId.value === modelId) {
      await refreshModelDetail(modelId)
      newMappingSiteId.value = null
      newMappingRemoteName.value = form.modelName
      newMappingMaxConcurrency.value = 0
      newMappingClientEmulation.value = 'None'
      newMappingExtraHeadersJson.value = ''
      newMappingEnabled.value = true
    }
    await loadModels()
  } catch (e) {
    message.error((e as Error).message)
  }
}

async function handleSaveMapping(mapping: modelsApi.ModelSiteMapping): Promise<void> {
  if (mapping.extraHeadersJson && mapping.extraHeadersJson.trim()) {
    try {
      const parsed = JSON.parse(mapping.extraHeadersJson.trim())
      if (typeof parsed !== 'object' || Array.isArray(parsed) || parsed === null) {
        message.warning('自定义请求头必须是合法的 JSON 对象，例如 {"User-Agent": "..."}')
        return
      }
    } catch {
      message.warning('自定义请求头 JSON 格式无效，请检查')
      return
    }
  }
  mappingUpdating.value[mapping.mappingId] = true
  try {
    await modelsApi.updateModelMapping(mapping.mappingId, {
      remoteModelName: mapping.remoteModelName,
      isEnabled: mapping.isEnabled,
      maxConcurrency: mapping.maxConcurrency,
      clientEmulation: mapping.clientEmulation || 'None',
      extraHeadersJson: mapping.extraHeadersJson?.trim() || undefined,
      // Web 端映射编辑器不再提供代理下拉，回传加载时已有的值，避免后端全量写回时把映射级代理清空。
      egressProxyUrl: mapping.egressProxyUrl?.trim() || undefined,
      overrideReasoningEffort: mapping.overrideReasoningEffort?.trim() || undefined
    })
    message.success(`站点【${mapping.siteName}】映射配置已保存`)
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    mappingUpdating.value[mapping.mappingId] = false
  }
}

async function handleUpdateMappingConcurrency(mapping: modelsApi.ModelSiteMapping, value: number | null): Promise<void> {
  const result = await modelsApi.updateMappingConcurrency(mapping.mappingId, Math.max(0, value ?? 0))
  mapping.maxConcurrency = result.maxConcurrency
  message.success('最大并发已更新')
}

async function handleDeleteMapping(mappingId: string): Promise<void> {
  if (!editingId.value) return
  const modelId = editingId.value
  await modelsApi.deleteModelMapping(modelId, mappingId)
  message.success('关联已删除')
  if (editingId.value === modelId) {
    await refreshModelDetail(modelId)
  }
  await loadModels()
}

function modelStatusType(model: ModelListItem): 'success' | 'default' {
  return model.isEnabled ? 'success' : 'default'
}

onMounted(() => {
  loadModels()
  loadHeaderProfiles()
})
</script>

<template>
  <div class="page-container models-page">
    <PageHeader title="模型库管理" subtitle="支持按厂商分组浏览模型，并维护模型与厂商的匹配规则">
      <template #actions>
        <NTag v-if="allModels.length" round :bordered="false" size="small">共 {{ allModels.length }} 个</NTag>
        <NPopconfirm @positive-click="handleClearAllModels">
          <template #trigger><NButton secondary type="error">清空模型</NButton></template>
          确认清空全部模型、映射和健康监控？
        </NPopconfirm>
        <NButton type="primary" @click="openCreate">＋ 新增模型</NButton>
      </template>
    </PageHeader>

    <NCard class="models-tab-card" :content-style="{ padding: '0' }">
      <NTabs v-model:value="activeTab" type="line" class="models-tabs" pane-class="models-tab-pane">
        <NTabPane name="gallery" tab="模型分组">
          <div class="model-toolbar">
            <div class="model-search-box">
              <NInput v-model:value="modelSearch" placeholder="搜索模型名称或厂商名" clearable />
            </div>
            <div class="model-toolbar-summary">
              共 <strong>{{ visibleModelCount }}</strong> / <span>{{ allModels.length }}</span> 个模型
            </div>
          </div>

        <NEmpty v-if="!loading && vendorGroups.length === 0" description="暂无模型，点击上方按钮新增" />
        <div v-else-if="filteredVendorGroups.length === 0" class="table-empty model-empty-state">
          <div class="table-empty-text">没有匹配的模型</div>
        </div>

        <div v-else class="vendor-groups">
        <section v-for="group in filteredVendorGroups" :key="group.vendorName" class="vendor-group">
          <div class="vendor-group-header" :style="{ background: group.headerBackground || undefined }">
            <div class="vendor-group-title-wrap">
              <div class="vendor-group-icon" :class="{ 'vendor-group-icon-fallback': !group.iconSvgBody }">
                <span v-if="group.iconSvgBody" v-html="buildVendorIconMarkup(group.iconSvgBody)" />
                <span v-else>{{ group.vendorName.slice(0, 1) }}</span>
              </div>
              <div>
                <h3 class="vendor-group-title">{{ group.vendorName }}</h3>
                <div class="vendor-group-subtitle">当前分组下共 {{ group.models.length }} 个模型</div>
              </div>
            </div>
            <NTag round :bordered="false" class="vendor-group-count">{{ group.models.length }}</NTag>
          </div>

          <div class="vendor-model-grid">
            <article v-for="model in group.models" :key="model.id" class="model-card">
              <div class="model-card-top">
                <div class="model-card-title">
                  <div class="model-card-name">{{ model.modelName }}</div>
                </div>
                <NTag size="small" :type="modelStatusType(model)" :bordered="false">
                  {{ model.isEnabled ? '启用' : '禁用' }}
                </NTag>
              </div>

              <div class="model-card-meta">
                <NTag size="small" type="info" :bordered="false">关联站点 {{ model.siteCount }}</NTag>
                <span v-if="model.overrideReasoningEffort" class="model-card-created">思考等级 {{ model.overrideReasoningEffort }}</span>
                <span v-else class="model-card-created">透传思考等级</span>
              </div>

              <div class="model-card-actions">
                <NButton size="small" secondary :type="model.isEnabled ? 'warning' : 'success'" @click="handleToggle(model)">
                  {{ model.isEnabled ? '禁用' : '启用' }}
                </NButton>
                <NButton size="small" secondary type="primary" @click="openEdit(model)">编辑</NButton>
                <NPopconfirm @positive-click="handleDelete(model)">
                  <template #trigger>
                    <NButton size="small" secondary type="error">删除</NButton>
                  </template>
                  删除模型「{{ model.modelName }}」？关联映射和路由规则会一并清理。
                </NPopconfirm>
              </div>
            </article>
          </div>
        </section>
      </div>
        </NTabPane>
        <NTabPane name="rules" tab="厂商规则">
          <div class="vendor-editor-toolbar">
            <div class="vendor-editor-help">
              每个厂商可配置标题样式和多条匹配规则。规则按优先级从小到大匹配，支持精确、通配符和正则。
            </div>
            <div class="vendor-editor-actions">
              <NInput v-model:value="vendorRuleSearch" size="small" clearable placeholder="搜索厂商名或规则，如 Gemini / qwen*" />
              <NPopconfirm @positive-click="restoreVendorCatalog">
                <template #trigger><NButton size="small" secondary>恢复当前文件内容</NButton></template>
                确认恢复为当前文件内容吗？未保存修改将丢失。
              </NPopconfirm>
              <NButton size="small" secondary type="primary" @click="addVendor">新增厂商</NButton>
              <NButton size="small" type="primary" :loading="savingVendorCatalog" @click="handleSaveVendorCatalog">保存厂商规则</NButton>
            </div>
          </div>

          <div class="vendor-editor-section">
            <div v-if="filteredVendors.length === 0" class="vendor-rule-empty">
              {{ vendorCatalog.vendors.length === 0 ? '当前还没有厂商配置，点击上方“新增厂商”即可开始维护。' : '没有匹配的厂商规则' }}
            </div>
            <div v-else class="vendor-definition-list">
              <section v-for="entry in filteredVendors" :key="`${entry.vendor.vendorName}-${entry.index}`" class="vendor-summary-card">
                <div class="vendor-summary-header" :style="{ background: entry.vendor.headerBackground || undefined }">
                  <div class="vendor-summary-title-wrap">
                    <div class="vendor-group-icon" :class="{ 'vendor-group-icon-fallback': !entry.vendor.iconSvgBody }">
                      <span v-if="entry.vendor.iconSvgBody" v-html="buildVendorIconMarkup(entry.vendor.iconSvgBody)" />
                      <span v-else>{{ (entry.vendor.vendorName || '厂').slice(0, 1) }}</span>
                    </div>
                    <div class="vendor-summary-heading">
                      <div class="vendor-summary-name">{{ entry.vendor.vendorName || '未命名厂商' }}</div>
                      <div class="vendor-summary-meta">规则 {{ entry.rules.length }} 条 · 排序 {{ entry.vendor.sortOrder }}</div>
                    </div>
                  </div>
                  <NSpace class="vendor-summary-actions">
                    <NButton size="small" secondary type="primary" @click="openVendorEditor(entry.index)">编辑</NButton>
                    <NPopconfirm @positive-click="deleteVendor(entry.index)">
                      <template #trigger><NButton size="small" secondary type="error">删除</NButton></template>
                      删除该厂商及其全部匹配规则？
                    </NPopconfirm>
                  </NSpace>
                </div>
                <div class="vendor-summary-body">
                  <div v-if="entry.rules.length" class="vendor-summary-rules">
                    <div v-for="(rule, ruleIndex) in entry.rules" :key="`${rule.pattern}-${ruleIndex}`" class="vendor-summary-rule-item">
                      <NTag size="small" :bordered="false">{{ matchTypeLabel(rule.matchType) }}</NTag>
                      <span class="vendor-summary-rule-pattern">{{ rule.pattern || '未填写匹配表达式' }}</span>
                    </div>
                  </div>
                  <div v-else class="vendor-rule-empty compact">当前还没有配置匹配规则。</div>
                </div>
              </section>
            </div>
          </div>
        </NTabPane>
        <NTabPane name="pricing" tab="模型价格">
          <ModelPricingPanel />
        </NTabPane>
      </NTabs>
    </NCard>

    <NModal
      v-model:show="showModal"
      :title="isEditMode ? '编辑模型' : '新建模型'"
      preset="card"
      style="width: min(1320px, 96vw); max-width: 1320px"
      :mask-closable="false"
    >
      <div class="model-editor-modal">
        <NTabs v-model:value="activeEditorTab" type="line" animated class="model-editor-tabs">
          <NTabPane name="basic" tab="基础设置">
            <div class="editor-tab-intro">
              <div class="editor-tab-title">
                模型定义
                <NTooltip trigger="hover">
                  <template #trigger><span class="tip-icon">?</span></template>
                  对外统一模型名称，用于系统暴露与路由匹配。
                </NTooltip>
              </div>
              <NTag :type="form.isEnabled ? 'success' : 'default'" :bordered="false" round>
                {{ form.isEnabled ? '当前启用' : '当前禁用' }}
              </NTag>
            </div>

            <NForm label-placement="top" class="model-editor-form">
              <section class="editor-section">
                <div class="editor-section-heading">
                  <h4>
                    基本信息
                    <NTooltip trigger="hover">
                      <template #trigger><span class="tip-icon">?</span></template>
                      对外统一模型名称，用于系统暴露与路由匹配。
                    </NTooltip>
                  </h4>
                </div>
                <div class="model-form-grid model-form-grid-single">
                  <NFormItem label="模型名称（唯一）" required>
                    <NInput v-model:value="form.modelName" placeholder="如 gpt-4o 或 claude-3-5-sonnet" />
                  </NFormItem>
                </div>
              </section>

              <section class="editor-section">
                <div class="editor-section-heading">
                  <h4>
                    请求策略
                    <NTooltip trigger="hover">
                      <template #trigger><span class="tip-icon">?</span></template>
                      可选择统一覆盖思考等级，或为该模型指定兼容规则集。
                    </NTooltip>
                  </h4>
                </div>
                <div class="model-form-grid model-form-grid-secondary">
                  <NFormItem label="强制思考等级（留空=透传）">
                    <NInput v-model:value="form.overrideReasoningEffort" placeholder="如 low / medium / high" />
                  </NFormItem>
                  <NFormItem label="兼容规则集">
                    <NSelect
                      v-model:value="form.compatibilityProfileId"
                      :options="profileOptions"
                      placeholder="选择规则集（可选）"
                    />
                  </NFormItem>
                </div>
              </section>

              <section class="editor-section">
                <div class="editor-section-heading">
                  <h4>
                    客户端特征模拟 / 请求头方案（模型默认）
                    <NTooltip trigger="hover">
                      <template #trigger><span class="tip-icon">?</span></template>
                      为该模型配置全局默认客户端身份伪装与请求头模板。可在调试工具「请求头模板库」中自由添加或修改方案。
                    </NTooltip>
                  </h4>
                </div>
                <div class="model-form-grid model-form-grid-secondary">
                  <NFormItem label="默认请求头方案">
                    <NSelect
                      v-model:value="form.clientEmulation"
                      :options="clientEmulationOptions"
                      placeholder="选择特征模拟与请求头方案（默认无）"
                    />
                  </NFormItem>
                </div>
              </section>

              <section class="editor-status-card">
                <div class="editor-status-title">
                  启用模型
                  <NTooltip trigger="hover">
                    <template #trigger><span class="tip-icon">?</span></template>
                    禁用后模型不会出现在可用模型和路由候选中。
                  </NTooltip>
                </div>
                <NSwitch v-model:value="form.isEnabled" />
              </section>
            </NForm>
          </NTabPane>

          <NTabPane name="mappings" tab="站点映射" :disabled="!isEditMode">
            <div class="editor-tab-intro">
              <div class="editor-tab-title">
                站点映射
                <NTooltip trigger="hover">
                  <template #trigger><span class="tip-icon">?</span></template>
                  为当前模型配置不同站点（含普通站点与 Google / Codex 等 OAuth 托管站点）的远端模型名、最大并发与专属模拟特征。
                </NTooltip>
              </div>
              <NTag v-if="modelDetail" type="info" :bordered="false" round>
                {{ modelDetail.siteMappings.length }} 个关联
              </NTag>
            </div>

            <NSpin :show="mappingLoading">
              <div class="mapping-editor-stack">
                <section class="mapping-list-panel editor-section">
                  <div class="editor-section-heading">
                    <h4>
                      已关联站点
                      <NTooltip trigger="hover">
                        <template #trigger><span class="tip-icon">?</span></template>
                        每个站点映射可独立配置最大并发与客户端特征伪装，修改后点击「保存配置」生效。
                      </NTooltip>
                    </h4>
                  </div>
                  <NEmpty v-if="!modelDetail || modelDetail.siteMappings.length === 0" description="暂无关联站点" size="small" />
                  <div v-else class="mapping-table-wrap">
                    <div class="mapping-table-header">
                      <span class="col-site">站点</span>
                      <span class="col-remote">远端模型名</span>
                      <span class="col-concurrency">最大并发 (0=不限)</span>
                      <span class="col-emulation">客户端特征模拟</span>
                      <span class="col-effort">强制思考等级<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>站点映射级思考等级，优先级最高：留空跟随模型库设置；模型库也为空时透传客户端原始值。可选 low / medium / high / xhigh / max。</NTooltip></span>
                      <span class="col-status">启用</span>
                      <span class="col-actions">操作</span>
                    </div>
                    <div class="mapping-rows-list">
                      <div v-for="m in modelDetail.siteMappings" :key="m.mappingId" class="mapping-row-item">
                        <div class="col-site">
                          <strong class="mapping-site-name" :title="m.siteName">{{ m.siteName }}</strong>
                        </div>
                        <div class="col-remote">
                          <NInput v-model:value="m.remoteModelName" size="small" placeholder="站点接受的模型名" />
                        </div>
                        <div class="col-concurrency">
                          <NInputNumber v-model:value="m.maxConcurrency" size="small" :min="0" :precision="0" placeholder="0=不限" />
                        </div>
                        <div class="col-emulation">
                          <NSelect v-model:value="m.clientEmulation" :options="clientEmulationOptions" size="small" />
                        </div>
                        <div class="col-effort">
                          <NInput v-model:value="m.overrideReasoningEffort" size="small" placeholder="留空=跟随模型/透传" />
                        </div>
                        <div class="col-status">
                          <NSwitch v-model:value="m.isEnabled" size="small" />
                        </div>
                        <div class="col-actions">
                          <NButton size="small" type="primary" secondary :loading="mappingUpdating[m.mappingId]" @click="handleSaveMapping(m)">
                            保存
                          </NButton>
                          <NPopconfirm @positive-click="handleDeleteMapping(m.mappingId)">
                            <template #trigger><NButton size="small" quaternary type="error">删除</NButton></template>
                            删除该站点关联映射？
                          </NPopconfirm>
                        </div>
                      </div>
                    </div>
                  </div>
                </section>

                <section class="mapping-add-panel editor-section">
                  <div class="editor-section-heading">
                    <h4>
                      添加关联站点
                      <NTooltip trigger="hover">
                        <template #trigger><span class="tip-icon">?</span></template>
                        选择一个可用站点，并配置该模型在站点上的映射参数与特征模拟。
                      </NTooltip>
                    </h4>
                  </div>
                  <NForm label-placement="top" class="mapping-add-form-new">
                    <div class="mapping-add-grid-primary">
                      <NFormItem label="站点" required>
                        <NSelect
                          v-model:value="newMappingSiteId"
                          :options="modelDetail?.availableSites.map(s => ({ label: s.name, value: s.id })) ?? []"
                          placeholder="选择站点"
                        />
                      </NFormItem>
                      <NFormItem label="站点上的模型名" required>
                        <NInput v-model:value="newMappingRemoteName" placeholder="如 gpt-4o-mini" />
                      </NFormItem>
                      <NFormItem label="最大并发">
                        <NInputNumber v-model:value="newMappingMaxConcurrency" :min="0" :precision="0" placeholder="0=不限" />
                      </NFormItem>
                      <NFormItem label="客户端特征模拟">
                        <NSelect v-model:value="newMappingClientEmulation" :options="clientEmulationOptions" />
                      </NFormItem>
                      <NFormItem label="启用状态" class="mapping-enabled-item">
                        <div class="mapping-enabled-line">
                          <NSwitch v-model:value="newMappingEnabled" />
                          <span>启用映射</span>
                        </div>
                      </NFormItem>
                      <NFormItem label=" " class="mapping-add-action">
                        <NButton type="primary" class="mapping-add-button" @click="handleAddMapping">＋ 添加关联</NButton>
                      </NFormItem>
                    </div>
                  </NForm>
                </section>
              </div>
            </NSpin>
          </NTabPane>
        </NTabs>
      </div>

      <template #footer>
        <NSpace justify="end">
          <NButton @click="showModal = false">取消</NButton>
          <NButton type="primary" :loading="saving" @click="handleSave">保存</NButton>
        </NSpace>
      </template>
    </NModal>

    <NModal
      v-model:show="showVendorModal"
      :title="`编辑厂商 - ${editingVendor?.vendorName || '未命名厂商'}`"
      preset="card"
      style="width: 960px; max-width: 94vw"
      :mask-closable="false"
    >
      <template v-if="editingVendor">
        <div class="vendor-modal-heading">
          <strong>详细配置</strong>
          <NPopconfirm @positive-click="deleteVendor(editingVendorIndex)">
            <template #trigger><NButton size="small" secondary type="error">删除厂商</NButton></template>
            删除该厂商及其全部匹配规则？
          </NPopconfirm>
        </div>

        <div class="vendor-definition-grid">
          <NFormItem label="厂商名称">
            <NInput :value="editingVendor.vendorName" @update:value="updateVendorName" />
          </NFormItem>
          <NFormItem label="标题背景色">
            <NInput v-model:value="editingVendor.headerBackground" placeholder="#eef6ff" />
          </NFormItem>
          <NFormItem label="排序值">
            <NInputNumber v-model:value="editingVendor.sortOrder" :precision="0" />
          </NFormItem>
        </div>

        <NFormItem label="标题 SVG 图标内容">
          <NInput
            :value="editingVendor.iconSvgBody"
            type="textarea"
            :autosize="{ minRows: 5, maxRows: 12 }"
            placeholder="可直接粘贴 <svg ...>...</svg> 或内部 <path> / <defs> 内容"
            @update:value="updateVendorIcon"
          />
        </NFormItem>

        <div class="vendor-preview-card">
          <div class="vendor-preview-label">标题预览</div>
          <div class="vendor-preview-header" :style="{ background: editingVendor.headerBackground || '#f8fafc' }">
            <div class="vendor-preview-icon" :class="{ 'vendor-group-icon-fallback': !editingVendorIcon }">
              <span v-if="editingVendorIcon" v-html="editingVendorIcon" />
              <span v-else>{{ (editingVendor.vendorName || '未').slice(0, 1) }}</span>
            </div>
            <div>
              <div class="vendor-preview-name">{{ editingVendor.vendorName || '未命名厂商' }}</div>
              <div class="vendor-preview-subtitle">模型分组标题示例</div>
            </div>
          </div>
        </div>

        <div class="vendor-rule-section">
          <div class="vendor-editor-section-header">
            <div>
              <h4>匹配规则</h4>
              <div class="vendor-editor-help">当前规则自动归属于该厂商，无需单独选择厂商名。</div>
            </div>
            <NButton size="small" secondary type="primary" @click="addVendorRule">新增规则</NButton>
          </div>

          <div v-if="editingVendorRules.length === 0" class="vendor-rule-empty">
            当前厂商还没有匹配规则，点击右上角“新增规则”即可直接补充。
          </div>
          <div v-else class="vendor-rule-list">
            <div v-for="entry in editingVendorRules" :key="entry.index" class="vendor-rule-row">
              <NFormItem label="优先级">
                <NInputNumber v-model:value="entry.rule.priority" :precision="0" />
              </NFormItem>
              <NFormItem label="匹配方式">
                <NSelect v-model:value="entry.rule.matchType" :options="matchTypeOptions" />
              </NFormItem>
              <NFormItem label="匹配表达式">
                <NInput v-model:value="entry.rule.pattern" placeholder="如 doubao*,skylark* 或 ^claude-3.*$" />
              </NFormItem>
              <div class="vendor-rule-delete">
                <NButton size="small" secondary type="error" @click="deleteVendorRule(entry.index)">删除</NButton>
              </div>
            </div>
          </div>
        </div>
      </template>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="showVendorModal = false">关闭</NButton>
          <NButton type="primary" :loading="savingVendorCatalog" @click="handleSaveVendorCatalog">保存厂商规则</NButton>
        </NSpace>
      </template>
    </NModal>

  </div>
</template>

<style scoped>
.models-page {
  min-width: 0;
}

.models-tab-card {
  min-width: 0;
  overflow: hidden;
}

.models-tab-card :deep(.n-card__content) {
  min-width: 0;
}

.models-tabs :deep(.n-tabs-nav) {
  padding: 0 12px;
}

.models-tab-pane {
  padding: 16px;
}

.model-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 20px;
  padding: 16px 18px;
  border: 1px solid var(--border-color-global);
  border-radius: 16px;
  background: var(--bg-card);
}

.model-search-box {
  flex: 1;
  max-width: 520px;
  min-width: 0;
}

.model-toolbar-summary {
  color: var(--text-color-secondary);
  white-space: nowrap;
}

.vendor-groups {
  display: flex;
  flex-direction: column;
  gap: 22px;
}

.vendor-group {
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  overflow: hidden;
  background: var(--bg-card);
  box-shadow: 0 8px 24px rgba(15, 23, 42, 0.04);
}

.vendor-group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 18px 22px;
  color: #1f2937;
  border-bottom: 1px solid rgba(15, 23, 42, 0.06);
}

.vendor-group-title-wrap {
  display: flex;
  align-items: center;
  gap: 14px;
  min-width: 0;
}

.vendor-group-icon {
  width: 44px;
  height: 44px;
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.7);
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  border: 1px solid rgba(15, 23, 42, 0.06);
}

.vendor-group-icon :deep(svg) {
  width: 24px;
  height: 24px;
  color: #111827;
}

.vendor-group-icon-fallback span {
  font-weight: 700;
  color: #111827;
}

.vendor-group-title {
  margin: 0;
  font-size: 20px;
  font-weight: 700;
}

.vendor-group-subtitle {
  margin-top: 4px;
  color: #475569;
  font-size: 13px;
}

.vendor-group-count {
  flex-shrink: 0;
}

.vendor-model-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
  gap: 18px;
  padding: 20px;
}

.model-card {
  display: flex;
  flex-direction: column;
  gap: 14px;
  min-height: 176px;
  padding: 18px;
  border: 1px solid var(--border-color-global);
  border-radius: 16px;
  background: var(--bg-card);
}

.model-card-top {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.model-card-title {
  min-width: 0;
}

.model-card-name {
  color: var(--text-primary);
  font-size: 16px;
  font-weight: 700;
  line-height: 1.4;
  word-break: break-word;
}

.model-card-meta {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.model-card-created {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.model-card-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: auto;
}

.model-empty-state {
  padding: 36px 20px;
  border: 1px dashed var(--border-color-global);
  border-radius: 16px;
  background: var(--bg-card);
}

.vendor-editor-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 16px;
  padding: 14px 16px;
  border: 1px solid var(--border-color-global);
  border-radius: 12px;
  background: var(--bg-card);
}

.vendor-editor-help {
  max-width: 520px;
  color: var(--text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}

.vendor-editor-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.vendor-editor-actions :deep(.n-input) {
  width: 260px;
}

.vendor-editor-section {
  padding: 18px;
  border: 1px solid var(--border-color-global);
  border-radius: 16px;
  background: var(--bg-card);
}

.vendor-definition-list {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.vendor-summary-card {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--border-color-global);
  border-radius: 16px;
  background: var(--bg-card);
  overflow: hidden;
}

/* 厂商规则卡片头部：与“模型分组”页一致的图标 + 厂商配色渐变条 */
.vendor-summary-card .vendor-summary-header {
  padding: 14px 18px;
  align-items: center;
  color: #1f2937;
  border-bottom: 1px solid rgba(15, 23, 42, 0.06);
}

.vendor-summary-actions {
  flex-shrink: 0;
}

.vendor-summary-title-wrap {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.vendor-summary-heading {
  min-width: 0;
}

.vendor-summary-card .vendor-summary-name {
  color: #1f2937;
}

.vendor-summary-card .vendor-summary-meta {
  color: #475569;
}

.vendor-summary-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px 18px 16px;
}

.vendor-summary-header,
.vendor-modal-heading,
.vendor-editor-section-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.vendor-summary-name {
  color: var(--text-primary);
  font-size: 16px;
  font-weight: 700;
  word-break: break-word;
}

.vendor-summary-meta,
.vendor-preview-label,
.vendor-preview-subtitle {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.vendor-summary-rules {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.vendor-summary-rule-item {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.vendor-summary-rule-pattern {
  min-width: 0;
  color: var(--text-primary);
  word-break: break-word;
}

.vendor-rule-empty {
  padding: 14px;
  border: 1px dashed rgba(148, 163, 184, 0.35);
  border-radius: 12px;
  color: var(--text-color-secondary);
  font-size: 13px;
  text-align: center;
}

.vendor-rule-empty.compact {
  padding: 10px 12px;
  text-align: left;
}

.vendor-modal-heading {
  align-items: center;
  margin-bottom: 16px;
}

.vendor-definition-grid {
  display: grid;
  grid-template-columns: 1.2fr 180px 120px;
  gap: 12px;
}

.vendor-definition-grid :deep(.n-input-number) {
  width: 100%;
}

.vendor-preview-card {
  margin-top: 4px;
  padding: 14px;
  border: 1px dashed rgba(148, 163, 184, 0.35);
  border-radius: 14px;
}

.vendor-preview-label {
  margin-bottom: 8px;
}

.vendor-preview-header {
  display: flex;
  align-items: center;
  gap: 12px;
  min-height: 72px;
  padding: 14px 16px;
  border-radius: 12px;
}

.vendor-preview-icon {
  display: flex;
  width: 42px;
  height: 42px;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  border: 1px solid rgba(15, 23, 42, 0.06);
  border-radius: 12px;
  background: rgba(255, 255, 255, 0.7);
}

.vendor-preview-icon :deep(svg) {
  width: 24px;
  height: 24px;
}

.vendor-preview-name {
  color: #1f2937;
  font-weight: 600;
}

.vendor-rule-section {
  margin-top: 18px;
  padding-top: 16px;
  border-top: 1px dashed rgba(148, 163, 184, 0.35);
}

.vendor-editor-section-header h4 {
  margin: 0 0 4px;
}

.vendor-rule-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-top: 12px;
}

.vendor-rule-row {
  display: grid;
  grid-template-columns: 110px 160px minmax(0, 1fr) 68px;
  gap: 10px;
  align-items: start;
}

.vendor-rule-row :deep(.n-input-number) {
  width: 100%;
}

.vendor-rule-delete {
  padding-top: 30px;
  text-align: right;
}

.table-empty-text {
  color: var(--text-color-secondary);
  text-align: center;
}

.model-editor-modal {
  min-width: 0;
}

.model-editor-tabs :deep(.n-tabs-nav) {
  margin-bottom: 4px;
}

.model-editor-tabs :deep(.n-tab-pane) {
  padding-top: 18px;
}

.editor-tab-intro {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 18px;
  padding: 16px 18px;
  border: 1px solid var(--border-color-global);
  border-radius: 14px;
  background: var(--bg-card);
}

.editor-tab-title {
  color: var(--text-primary);
  font-size: 17px;
  font-weight: 700;
}

.editor-tab-description,
.editor-section-heading p,
.editor-status-description {
  margin: 5px 0 0;
  color: var(--text-color-secondary);
  font-size: 13px;
  line-height: 1.6;
}

.model-editor-form {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.editor-section,
.editor-status-card {
  min-width: 0;
  padding: 18px;
  border: 1px solid var(--border-color-global);
  border-radius: 14px;
  background: var(--bg-card);
}

.editor-section-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 10px;
}

.editor-section-heading h4 {
  display: inline-flex;
  align-items: center;
  margin: 0;
  color: var(--text-primary);
  font-size: 14px;
  font-weight: 600;
}

.editor-tab-title,
.editor-status-title {
  display: inline-flex;
  align-items: center;
}

.tip-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 15px;
  height: 15px;
  margin-left: 6px;
  border: 1.5px solid var(--text-color-secondary, #94a3b8);
  border-radius: 50%;
  color: var(--text-color-secondary, #94a3b8);
  font-size: 10px;
  font-weight: 700;
  line-height: 1;
  cursor: help;
  vertical-align: middle;
  flex-shrink: 0;
  transition: all 0.2s ease;
}

.tip-icon:hover {
  color: var(--primary-color, #6366f1);
  border-color: var(--primary-color, #6366f1);
}

[data-theme='dark'] .tip-icon {
  color: rgba(255, 255, 255, 0.55);
  border-color: rgba(255, 255, 255, 0.55);
}

[data-theme='dark'] .tip-icon:hover {
  color: var(--primary-color, #818cf8);
  border-color: var(--primary-color, #818cf8);
}

.model-form-grid {
  display: grid;
  gap: 14px 18px;
}

.model-form-grid-single {
  grid-template-columns: 1fr;
}

.model-form-grid-primary,
.model-form-grid-secondary {
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.model-form-grid :deep(.n-form-item) {
  min-width: 0;
  margin-bottom: 0;
}

.editor-status-card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  background: color-mix(in srgb, var(--primary-color) 5%, var(--bg-card));
}

.editor-status-title {
  color: var(--text-primary);
  font-size: 14px;
  font-weight: 600;
}

.mapping-editor-stack {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.mapping-list-panel,
.mapping-add-panel {
  min-width: 0;
}

.mapping-table-wrap {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.mapping-table-header,
.mapping-row-item {
  display: grid;
  grid-template-columns: minmax(150px, 1.1fr) minmax(190px, 1.4fr) 120px minmax(220px, 1.4fr) minmax(150px, 1fr) 50px 110px;
  gap: 12px;
  align-items: center;
}

.mapping-table-header {
  padding: 6px 12px;
  font-size: 12px;
  font-weight: 600;
  color: var(--text-color-secondary);
  border-bottom: 1px solid var(--border-color-global);
}

.mapping-table-header .col-status {
  text-align: center;
}

.mapping-table-header .col-actions {
  text-align: right;
}

.mapping-rows-list {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: min(48vh, 520px);
  padding-right: 4px;
  overflow-y: auto;
}

.mapping-row-item {
  padding: 8px 12px;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
  background: var(--bg-card-secondary, var(--bg-card));
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.03);
  transition: all 0.15s ease;
}

.mapping-row-item:hover {
  border-color: var(--primary-color-hover, #6366f1);
  background: var(--bg-hover, rgba(255, 255, 255, 0.03));
}

.mapping-row-item .col-site {
  overflow: hidden;
}

.mapping-site-name {
  display: block;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13.5px;
  font-weight: 600;
  color: var(--text-primary);
}

.mapping-row-item .col-status {
  display: flex;
  justify-content: center;
  align-items: center;
}

.mapping-row-item .col-actions {
  display: flex;
  align-items: center;
  gap: 6px;
  justify-content: flex-end;
}

.mapping-add-form-new {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.mapping-add-grid-primary {
  display: grid;
  grid-template-columns: minmax(160px, 1.2fr) minmax(200px, 1.5fr) 130px minmax(240px, 1.5fr) auto auto;
  gap: 12px;
  align-items: end;
}

.mapping-add-grid-primary :deep(.n-form-item) {
  min-width: 0;
  margin-bottom: 0;
}

.mapping-enabled-line {
  display: flex;
  min-height: 34px;
  align-items: center;
  gap: 8px;
  color: var(--text-secondary);
  white-space: nowrap;
}

.mapping-add-action :deep(.n-form-item-label) {
  visibility: hidden;
}

.mapping-add-button {
  width: 100%;
  min-height: 34px;
}

@media (max-width: 960px) {
  .mapping-table-header {
    display: none;
  }

  .mapping-row-item {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .mapping-add-grid-primary {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 768px) {
  .mapping-row-item,
  .mapping-add-grid-primary {
    grid-template-columns: 1fr;
  }

  .model-toolbar,
  .vendor-group-header {
    align-items: stretch;
    flex-direction: column;
  }

  .model-search-box {
    max-width: none;
  }

  .vendor-model-grid {
    grid-template-columns: 1fr;
    padding: 14px;
  }

  .model-card-actions :deep(.n-button) {
    flex: 1 1 auto;
  }

  .vendor-editor-toolbar,
  .vendor-summary-header,
  .vendor-editor-section-header {
    align-items: stretch;
    flex-direction: column;
  }

  .vendor-editor-actions {
    justify-content: flex-start;
  }

  .vendor-editor-actions :deep(.n-input) {
    width: 100%;
  }

  .vendor-definition-grid,
  .vendor-rule-row {
    grid-template-columns: 1fr;
  }

  .vendor-rule-delete {
    padding-top: 0;
    text-align: left;
  }

  .model-form-grid-primary,
  .model-form-grid-secondary,
  .mapping-row-item,
  .mapping-add-grid-primary,
  .mapping-add-grid-secondary {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 560px) {
  .editor-tab-intro,
  .editor-status-card {
    align-items: stretch;
    flex-direction: column;
  }

  .editor-section,
  .editor-status-card,
  .mapping-row-item {
    padding: 12px;
  }
}
</style>
