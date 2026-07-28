<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import {
  NCard, NButton, NSpace, NTag, NModal, NForm, NFormItem, NInput,
  NSwitch, NSelect, NPopconfirm, NCollapse, NCollapseItem, NEmpty, NDrawer, NDrawerContent, useMessage
} from 'naive-ui'
import * as modelsApi from '@/api/models'
import * as compatApi from '@/api/compatibility'
import type { ModelListItem, ModelPayload } from '@/api/models'
import PageHeader from '@/components/PageHeader.vue'

const message = useMessage()
const loading = ref(false)
const vendorGroups = ref<modelsApi.ModelVendorGroup[]>([])
const expandedNames = ref<string[]>([])
const profileOptions = ref<{ label: string; value: string }[]>([])

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
const mappingLoading = ref(false)

const isEditMode = computed(() => !!editingId.value)
const allModels = computed(() => vendorGroups.value.flatMap((g) => g.models))

async function loadModels(): Promise<void> {
  loading.value = true
  try {
    const [resp, profiles] = await Promise.all([modelsApi.listModels(), compatApi.listProfiles()])
    vendorGroups.value = resp.vendorGroups
    // 默认展开所有厂商组。
    expandedNames.value = resp.vendorGroups.map((g) => g.vendorName)
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
  try {
    modelDetail.value = await modelsApi.getModelDetail(model.id)
  } finally { mappingLoading.value = false }
}

async function handleAddMapping(): Promise<void> {
  if (!mappingModel.value || !newMappingSiteId.value || !newMappingRemoteName.value.trim()) {
    message.warning('请选择站点并填写模型名'); return
  }
  try {
    await modelsApi.addModelMapping(mappingModel.value.id, newMappingSiteId.value, newMappingRemoteName.value.trim())
    message.success('关联已添加')
    modelDetail.value = await modelsApi.getModelDetail(mappingModel.value.id)
    newMappingSiteId.value = null
    await loadModels()
  } catch (e) { message.error((e as Error).message) }
}

async function handleDeleteMapping(mappingId: string): Promise<void> {
  if (!mappingModel.value) return
  await modelsApi.deleteModelMapping(mappingModel.value.id, mappingId)
  message.success('关联已删除')
  modelDetail.value = await modelsApi.getModelDetail(mappingModel.value.id)
  await loadModels()
}

onMounted(loadModels)
</script>

<template>
  <div class="page-container">
    <PageHeader title="模型库管理" subtitle="支持按厂商分组浏览模型，并维护模型与厂商的匹配规则">
      <template #actions>
        <NTag v-if="allModels.length" round :bordered="false" size="small">共 {{ allModels.length }} 个</NTag>
        <NButton type="primary" @click="openCreate">新建模型</NButton>
      </template>
    </PageHeader>
    <NCard>
      <NEmpty v-if="!loading && vendorGroups.length === 0" description="暂无模型，点击右上角创建" />

      <NCollapse v-else v-model:expanded-names="expandedNames" arrow-placement="left">
        <NCollapseItem
          v-for="group in vendorGroups"
          :key="group.vendorName"
          :name="group.vendorName"
        >
          <template #header>
            <NSpace align="center" :size="8">
              <span
                v-if="group.iconSvgBody"
                class="vendor-icon"
                v-html="group.iconSvgBody"
              />
              <span style="font-weight: 600">{{ group.vendorName }}</span>
              <NTag size="small" round :bordered="false">{{ group.models.length }}</NTag>
            </NSpace>
          </template>

          <div class="model-grid">
            <NCard
              v-for="model in group.models"
              :key="model.id"
              size="small"
              class="model-card"
            >
              <NSpace justify="space-between" align="center">
                <div>
                  <NSpace align="center" :size="6">
                    <span style="font-weight: 600">{{ model.displayName }}</span>
                    <NTag
                      size="tiny"
                      :type="model.isEnabled ? 'success' : 'default'"
                      :bordered="false"
                    >
                      {{ model.isEnabled ? '启用' : '禁用' }}
                    </NTag>
                  </NSpace>
                  <div class="model-meta">{{ model.modelName }} · {{ model.siteCount }} 站点</div>
                </div>
                <NSpace :size="4">
                  <NButton size="tiny" quaternary @click="openEdit(model)">编辑</NButton>
                  <NButton size="tiny" quaternary @click="openMappingDrawer(model)">映射</NButton>
                  <NButton size="tiny" quaternary @click="handleToggle(model)">
                    {{ model.isEnabled ? '禁用' : '启用' }}
                  </NButton>
                  <NPopconfirm @positive-click="handleDelete(model)">
                    <template #trigger>
                      <NButton size="tiny" quaternary type="error">删除</NButton>
                    </template>
                    删除模型「{{ model.displayName }}」？关联映射和路由规则会一并清理。
                  </NPopconfirm>
                </NSpace>
              </NSpace>
            </NCard>
          </div>
        </NCollapseItem>
      </NCollapse>
    </NCard>

    <NModal
      v-model:show="showModal"
      :title="isEditMode ? '编辑模型' : '新建模型'"
      preset="card"
      style="width: 480px"
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
            <NSpace align="center" :size="8" style="flex: 1">
              <span>{{ m.siteName }}</span>
              <NTag size="small" :bordered="false">{{ m.remoteModelName }}</NTag>
              <NTag v-if="!m.isEnabled" size="tiny" :bordered="false">禁用</NTag>
            </NSpace>
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
            <NButton type="primary" size="small" @click="handleAddMapping">添加</NButton>
          </NSpace>
        </NSpin>
      </NDrawerContent>
    </NDrawer>
  </div>
</template>

<style scoped>
.vendor-icon {
  width: 20px;
  height: 20px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}
.vendor-icon :deep(svg) {
  width: 100%;
  height: 100%;
}
.model-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
  gap: 12px;
}
.model-card {
  border-radius: 8px;
}
.model-meta {
  font-size: 12px;
  color: var(--text-color-secondary);
  margin-top: 4px;
}
</style>
