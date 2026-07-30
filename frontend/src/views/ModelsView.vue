<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import {
  NCard, NButton, NSpace, NTag, NModal, NForm, NFormItem, NInput, NInputNumber,
  NSwitch, NSelect, NPopconfirm, NEmpty, NDrawer, NDrawerContent, NSpin, useMessage
} from 'naive-ui'
import * as modelsApi from '@/api/models'
import * as compatApi from '@/api/compatibility'
import type { ModelListItem, ModelPayload } from '@/api/models'
import PageHeader from '@/components/PageHeader.vue'

const message = useMessage()
const loading = ref(false)
const vendorGroups = ref<modelsApi.ModelVendorGroup[]>([])
const profileOptions = ref<{ label: string; value: string }[]>([])
const modelSearch = ref('')

const showModal = ref(false)
const editingId = ref<string | null>(null)
const form = reactive<ModelPayload>({
  modelName: '',
  displayName: '',
  isEnabled: true,
  overrideReasoningEffort: '',
  compatibilityProfileId: null
})
const saving = ref(false)

// 映射管理 Drawer
const mappingDrawer = ref(false)
const mappingModel = ref<ModelListItem | null>(null)
const modelDetail = ref<modelsApi.ModelDetail | null>(null)
const newMappingSiteId = ref<string | null>(null)
const newMappingRemoteName = ref('')
const newMappingEnabled = ref(true)
const mappingLoading = ref(false)

const isEditMode = computed(() => !!editingId.value)
const allModels = computed(() => vendorGroups.value.flatMap((g) => g.models))
const filteredVendorGroups = computed(() => {
  const keyword = modelSearch.value.trim().toLowerCase()
  return vendorGroups.value
    .map((group) => ({
      ...group,
      models: group.models.filter((model) => {
        if (!keyword) return true
        return `${model.modelName} ${model.displayName} ${group.vendorName}`.toLowerCase().includes(keyword)
      })
    }))
    .filter((group) => group.models.length > 0)
})
const visibleModelCount = computed(() => filteredVendorGroups.value.reduce((sum, group) => sum + group.models.length, 0))

async function loadModels(): Promise<void> {
  loading.value = true
  try {
    const [resp, profiles] = await Promise.all([modelsApi.listModels(), compatApi.listProfiles()])
    vendorGroups.value = resp.vendorGroups
    profileOptions.value = [
      { label: '无', value: '' },
      ...profiles.map((p) => ({ label: p.name, value: p.id }))
    ]
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  Object.assign(form, { modelName: '', displayName: '', isEnabled: true, overrideReasoningEffort: '', compatibilityProfileId: null })
  showModal.value = true
}

async function openEdit(model: ModelListItem): Promise<void> {
  editingId.value = model.id
  Object.assign(form, {
    modelName: model.modelName,
    displayName: model.displayName,
    isEnabled: model.isEnabled,
    overrideReasoningEffort: model.overrideReasoningEffort,
    compatibilityProfileId: model.compatibilityProfileId
  })
  showModal.value = true
}

async function handleSave(): Promise<void> {
  if (!form.modelName.trim()) {
    message.warning('模型名称不能为空')
    return
  }
  // 空字符串（选"无"）转 null，与后端 Guid? 一致。
  const payload = { ...form, compatibilityProfileId: form.compatibilityProfileId || null }
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

async function openMappingDrawer(model: ModelListItem): Promise<void> {
  mappingModel.value = model
  mappingDrawer.value = true
  mappingLoading.value = true
  newMappingSiteId.value = null
  newMappingRemoteName.value = model.modelName
  newMappingEnabled.value = true
  try {
    modelDetail.value = await modelsApi.getModelDetail(model.id)
  } finally { mappingLoading.value = false }
}

async function handleAddMapping(): Promise<void> {
  if (!mappingModel.value || !newMappingSiteId.value || !newMappingRemoteName.value.trim()) {
    message.warning('请选择站点并填写模型名'); return
  }
  try {
    await modelsApi.addModelMapping(mappingModel.value.id, newMappingSiteId.value, newMappingRemoteName.value.trim(), newMappingEnabled.value)
    message.success('关联已添加')
    modelDetail.value = await modelsApi.getModelDetail(mappingModel.value.id)
    newMappingSiteId.value = null
    newMappingEnabled.value = true
    await loadModels()
  } catch (e) { message.error((e as Error).message) }
}

async function handleUpdateMappingConcurrency(mapping: modelsApi.ModelSiteMapping, value: number | null): Promise<void> {
  const result = await modelsApi.updateMappingConcurrency(mapping.mappingId, Math.max(0, value ?? 0))
  mapping.maxConcurrency = result.maxConcurrency
  message.success('最大并发已更新')
}

async function handleDeleteMapping(mappingId: string): Promise<void> {
  if (!mappingModel.value) return
  await modelsApi.deleteModelMapping(mappingModel.value.id, mappingId)
  message.success('关联已删除')
  modelDetail.value = await modelsApi.getModelDetail(mappingModel.value.id)
  await loadModels()
}

function modelStatusType(model: ModelListItem): 'success' | 'default' {
  return model.isEnabled ? 'success' : 'default'
}

onMounted(loadModels)
</script>

<template>
  <div class="page-container models-page">
    <PageHeader title="模型库管理" subtitle="支持按厂商分组浏览模型，并维护模型与厂商的匹配规则">
      <template #actions>
        <NTag v-if="allModels.length" round :bordered="false" size="small">共 {{ allModels.length }} 个</NTag>
        <NButton type="primary" @click="openCreate">＋ 新增模型</NButton>
      </template>
    </PageHeader>

    <NCard class="models-tab-card" :content-style="{ padding: '16px' }">
      <div class="model-toolbar">
        <div class="model-search-box">
          <NInput v-model:value="modelSearch" placeholder="搜索模型名称或显示名" clearable />
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
                <span v-if="group.iconSvgBody" v-html="group.iconSvgBody" />
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
                  <div class="model-card-display">{{ model.displayName || model.modelName }}</div>
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
                <NButton size="small" secondary @click="openMappingDrawer(model)">映射</NButton>
                <NPopconfirm @positive-click="handleDelete(model)">
                  <template #trigger>
                    <NButton size="small" secondary type="error">删除</NButton>
                  </template>
                  删除模型「{{ model.displayName }}」？关联映射和路由规则会一并清理。
                </NPopconfirm>
              </div>
            </article>
          </div>
        </section>
      </div>
    </NCard>

    <NModal
      v-model:show="showModal"
      :title="isEditMode ? '编辑模型' : '新建模型'"
      preset="card"
      style="width: 480px; max-width: 92vw"
      :mask-closable="false"
    >
      <NForm label-placement="top">
        <NFormItem label="模型名称（唯一）">
          <NInput v-model:value="form.modelName" placeholder="如 gpt-4o" :disabled="isEditMode" />
        </NFormItem>
        <NFormItem label="显示名称">
          <NInput v-model:value="form.displayName" placeholder="留空则用模型名称" />
        </NFormItem>
        <NFormItem label="强制思考等级（留空=透传）">
          <NInput v-model:value="form.overrideReasoningEffort" placeholder="如 low/medium/high" />
        </NFormItem>
        <NFormItem label="兼容规则集">
          <NSelect
            v-model:value="form.compatibilityProfileId"
            :options="profileOptions"
            placeholder="选择规则集（可选）"
          />
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

    <!-- 映射管理 Drawer -->
    <NDrawer v-model:show="mappingDrawer" :width="520" placement="right">
      <NDrawerContent :title="`站点映射 - ${mappingModel?.displayName ?? ''}`" closable>
        <NSpin :show="mappingLoading">
          <h4 style="margin: 0 0 8px; font-size: 13px; color: var(--n-text-color-3, #888)">已关联站点</h4>
          <NEmpty v-if="!modelDetail || modelDetail.siteMappings.length === 0" description="暂无关联" size="small" />
          <div v-for="m in modelDetail?.siteMappings" :key="m.mappingId" class="mapping-row">
            <div class="mapping-row-main">
              <div class="mapping-site-line">
                <span>{{ m.siteName }}</span>
                <NTag size="small" :bordered="false">{{ m.remoteModelName }}</NTag>
                <NTag v-if="!m.isEnabled" size="tiny" :bordered="false">禁用</NTag>
              </div>
              <div class="mapping-concurrency-line">
                <span class="mapping-field-label">最大并发</span>
                <NInputNumber v-model:value="m.maxConcurrency" size="tiny" :min="0" :precision="0" placeholder="0=不限" class="mapping-concurrency-input" />
                <NButton size="tiny" secondary @click="handleUpdateMappingConcurrency(m, m.maxConcurrency)">保存</NButton>
              </div>
            </div>
            <NPopconfirm @positive-click="handleDeleteMapping(m.mappingId)">
              <template #trigger><NButton size="tiny" quaternary type="error">删除</NButton></template>
              删除该关联？
            </NPopconfirm>
          </div>

          <h4 style="margin: 20px 0 8px; font-size: 13px; color: var(--n-text-color-3, #888)">添加关联</h4>
          <NSpace vertical :size="8">
            <NSelect
              v-model:value="newMappingSiteId"
              :options="modelDetail?.availableSites.map(s => ({ label: s.name, value: s.id })) ?? []"
              placeholder="选择站点"
            />
            <NInput v-model:value="newMappingRemoteName" placeholder="站点上的模型名" />
            <label class="mapping-enabled-line">
              <NSwitch v-model:value="newMappingEnabled" />
              <span>启用映射</span>
            </label>
            <NButton type="primary" size="small" @click="handleAddMapping">添加</NButton>
          </NSpace>
        </NSpin>
      </NDrawerContent>
    </NDrawer>
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
  font-size: 26px;
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

.model-card-display {
  margin-top: 4px;
  color: var(--text-color-secondary);
  font-size: 13px;
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

.table-empty-text {
  color: var(--text-color-secondary);
  text-align: center;
}

.mapping-row {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  padding: 10px 0;
  border-bottom: 1px solid var(--border-color-global);
}

.mapping-row-main {
  flex: 1;
  min-width: 0;
}

.mapping-site-line,
.mapping-concurrency-line,
.mapping-enabled-line {
  display: flex;
  align-items: center;
  gap: 8px;
}

.mapping-site-line {
  flex-wrap: wrap;
  margin-bottom: 8px;
}

.mapping-field-label,
.mapping-enabled-line {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.mapping-concurrency-input {
  width: 112px;
}

@media (max-width: 768px) {
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
}
</style>
