<script setup lang="ts">
import { computed, h, onMounted, reactive, ref } from 'vue'
import {
  NCard, NButton, NSpace, NTag, NModal, NForm, NFormItem, NInput,
  NSwitch, NPopconfirm, NCollapse, NCollapseItem, NEmpty, useMessage
} from 'naive-ui'
import * as modelsApi from '@/api/models'
import type { ModelListItem, ModelPayload } from '@/api/models'

const message = useMessage()
const loading = ref(false)
const vendorGroups = ref<modelsApi.ModelVendorGroup[]>([])
const expandedNames = ref<string[]>([])

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

const isEditMode = computed(() => !!editingId.value)
const allModels = computed(() => vendorGroups.value.flatMap((g) => g.models))

async function loadModels(): Promise<void> {
  loading.value = true
  try {
    const resp = await modelsApi.listModels()
    vendorGroups.value = resp.vendorGroups
    // 默认展开所有厂商组。
    expandedNames.value = resp.vendorGroups.map((g) => g.vendorName)
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
  saving.value = true
  try {
    if (editingId.value) {
      await modelsApi.updateModel(editingId.value, form)
      message.success('模型已更新')
    } else {
      await modelsApi.createModel(form)
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

onMounted(loadModels)
</script>

<template>
  <div class="page-container">
    <NCard>
      <template #header>
        <NSpace justify="space-between" align="center">
          <span>模型库（共 {{ allModels.length }} 个模型）</span>
          <NButton type="primary" @click="openCreate">新建模型</NButton>
        </NSpace>
      </template>

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
  color: #888;
  margin-top: 4px;
}
</style>
