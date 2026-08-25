<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NCode,
  NDivider,
  NDropdown,
  NEmpty,
  NForm,
  NFormItem,
  NGrid,
  NGridItem,
  NInput,
  NInputNumber,
  NModal,
  NPopconfirm,
  NSpace,
  NSwitch,
  NTag,
  NTooltip,
  useMessage
} from 'naive-ui'
import {
  getHeaderProfiles,
  createHeaderProfile,
  updateHeaderProfile,
  deleteHeaderProfile,
  previewHeaders,
  type HeaderProfile,
  type HeaderProfilePayload
} from '@/api/headerProfiles'

const message = useMessage()
const loading = ref(false)
const profiles = ref<HeaderProfile[]>([])

// 编辑/新建弹窗
const showModal = ref(false)
const isEditing = ref(false)
const currentId = ref<string | null>(null)
const isCurrentBuiltIn = ref(false)
const modalSubmitting = ref(false)

const formModel = ref<HeaderProfilePayload>({
  key: '',
  name: '',
  description: '',
  headersJson: '',
  isEnabled: true,
  sortOrder: 10
})

// 实时预览
const previewLoading = ref(false)
const previewResult = ref<Record<string, string> | null>(null)

// 占位符列表
const placeholderList = [
  { tag: '${guid}', desc: '生成标准 GUID (带连字符: 8-4-4-4-12)' },
  { tag: '${guid:N}', desc: '生成无连字符 GUID (32位十六进制)' },
  { tag: '${nanoid:12}', desc: '生成指定长度的 NanoId (如 12 位)' },
  { tag: '${timestamp}', desc: '当前秒级时间戳 (Unix Timestamp)' },
  { tag: '${timestamp_ms}', desc: '当前毫秒级时间戳' },
  { tag: '${model}', desc: '当前请求调用的远端模型名称' }
]

async function loadProfiles() {
  loading.value = true
  try {
    profiles.value = await getHeaderProfiles()
  } catch (err: any) {
    message.error(err?.response?.data?.message || err?.message || '加载请求头方案列表失败')
  } finally {
    loading.value = false
  }
}

function openCreateModal() {
  isEditing.value = false
  currentId.value = null
  isCurrentBuiltIn.value = false
  previewResult.value = null
  formModel.value = {
    key: '',
    name: '',
    description: '',
    headersJson: JSON.stringify(
      {
        'User-Agent': 'my-custom-client/1.0.0',
        'x-request-id': '${guid}',
        'x-client-trace': '${nanoid:12}'
      },
      null,
      2
    ),
    isEnabled: true,
    sortOrder: 20
  }
  showModal.value = true
}

function openEditModal(profile: HeaderProfile) {
  isEditing.value = true
  currentId.value = profile.id
  isCurrentBuiltIn.value = profile.isBuiltIn
  previewResult.value = null
  formModel.value = {
    key: profile.key,
    name: profile.name,
    description: profile.description || '',
    headersJson: profile.headersJson || '',
    isEnabled: profile.isEnabled,
    sortOrder: profile.sortOrder
  }
  showModal.value = true
}

function cloneProfile(profile: HeaderProfile) {
  isEditing.value = false
  currentId.value = null
  isCurrentBuiltIn.value = false
  previewResult.value = null
  formModel.value = {
    key: `${profile.key}_copy`,
    name: `${profile.name} (副本)`,
    description: profile.description || '',
    headersJson: profile.headersJson || '',
    isEnabled: true,
    sortOrder: profile.sortOrder + 10
  }
  showModal.value = true
}

async function handleSave() {
  const key = formModel.value.key?.trim()
  const name = formModel.value.name?.trim()

  if (!key) {
    message.warning('请输入方案 Key')
    return
  }
  if (!name) {
    message.warning('请输入方案名称')
    return
  }

  const rawHeaders = formModel.value.headersJson?.trim()
  if (rawHeaders) {
    try {
      JSON.parse(rawHeaders)
    } catch {
      message.error('请求头 JSON 格式不合法，请检查括号与引号')
      return
    }
  }

  const payload: HeaderProfilePayload = {
    key,
    name,
    description: formModel.value.description?.trim() || null,
    headersJson: rawHeaders || null,
    isEnabled: formModel.value.isEnabled,
    sortOrder: formModel.value.sortOrder
  }

  modalSubmitting.value = true
  try {
    if (isEditing.value && currentId.value) {
      await updateHeaderProfile(currentId.value, payload)
      message.success('请求头方案已更新')
    } else {
      await createHeaderProfile(payload)
      message.success('请求头方案已创建')
    }
    showModal.value = false
    await loadProfiles()
  } catch (err: any) {
    message.error(err?.response?.data?.message || err?.message || '保存失败')
  } finally {
    modalSubmitting.value = false
  }
}

async function handleDelete(profile: HeaderProfile) {
  try {
    await deleteHeaderProfile(profile.id)
    message.success('请求头方案已删除')
    await loadProfiles()
  } catch (err: any) {
    message.error(err?.response?.data?.message || err?.message || '删除失败')
  }
}

async function handleLivePreview() {
  previewLoading.value = true
  try {
    const res = await previewHeaders({
      emulationPreset: isCurrentBuiltIn.value ? formModel.value.key : undefined,
      headersJson: formModel.value.headersJson,
      modelName: 'gpt-4o'
    })
    previewResult.value = res.previewHeaders
    message.success('实时求值成功')
  } catch (err: any) {
    message.error(err?.response?.data?.message || err?.message || '求值失败')
  } finally {
    previewLoading.value = false
  }
}

function copyPlaceholder(tag: string) {
  navigator.clipboard.writeText(tag)
  message.success(`已复制占位符: ${tag}`)
}

function parseHeadersCount(json?: string | null): number {
  if (!json?.trim()) return 0
  try {
    const obj = JSON.parse(json)
    return Object.keys(obj).length
  } catch {
    return 0
  }
}

onMounted(() => {
  loadProfiles()
})
</script>

<template>
  <div class="header-profiles-tab">
    <div class="tab-header">
      <div>
        <h3 class="tab-title">请求头模板与客户端仿真库</h3>
        <p class="tab-desc">
          集中管理所有客户端特征预设与自定义请求头。在模型库与站点映射中直接通过下拉框引用，网关在转发时自动注入官方客户端指纹并计算动态占位符。
        </p>
      </div>
      <NSpace>
        <NButton secondary size="small" :loading="loading" @click="loadProfiles">🔄 刷新</NButton>
        <NButton type="primary" size="small" @click="openCreateModal">+ 新增自定义方案</NButton>
      </NSpace>
    </div>

    <div v-if="loading" class="loading-state">
      <span>加载方案中...</span>
    </div>

    <div v-else-if="profiles.length === 0" class="empty-state">
      <NEmpty description="暂无请求头方案" />
    </div>

    <div v-else class="profile-grid">
      <div
        v-for="p in profiles"
        :key="p.id"
        class="profile-card"
        :class="{ 'profile-card-builtin': p.isBuiltIn, 'profile-card-disabled': !p.isEnabled }"
      >
        <div class="card-head">
          <div class="title-wrap">
            <span class="profile-name">{{ p.name }}</span>
            <NTag v-if="p.isBuiltIn" size="tiny" type="primary" :bordered="false">系统内置</NTag>
            <NTag v-else size="tiny" type="info" :bordered="false">自定义</NTag>
            <NTag v-if="!p.isEnabled" size="tiny" type="default" :bordered="false">已禁用</NTag>
          </div>
          <span class="profile-key">Key: <code>{{ p.key }}</code></span>
        </div>

        <div class="card-body">
          <p class="profile-desc">{{ p.description || '暂无说明' }}</p>

          <div class="headers-summary">
            <span class="summary-label">包含特征头：</span>
            <span class="summary-count">{{ parseHeadersCount(p.headersJson) }} 个</span>
          </div>

          <pre v-if="p.headersJson" class="headers-code-preview">{{ p.headersJson }}</pre>
        </div>

        <div class="card-foot">
          <div class="foot-left">
            <span class="order-label">排序: {{ p.sortOrder }}</span>
          </div>
          <NSpace size="small">
            <NButton size="tiny" secondary @click="cloneProfile(p)">克隆</NButton>
            <NButton size="tiny" type="primary" ghost @click="openEditModal(p)">
              {{ p.isBuiltIn ? '查看 / 自定义' : '编辑' }}
            </NButton>
            <NPopconfirm v-if="!p.isBuiltIn" @positive-click="handleDelete(p)">
              <template #trigger>
                <NButton size="tiny" type="error" ghost>删除</NButton>
              </template>
              确定删除此自定义请求头方案吗？
            </NPopconfirm>
          </NSpace>
        </div>
      </div>
    </div>

    <!-- 方案编辑/创建弹窗 -->
    <NModal
      v-model:show="showModal"
      preset="card"
      :title="isEditing ? (isCurrentBuiltIn ? `查看 / 配置系统预设 [${formModel.name}]` : `编辑方案 [${formModel.name}]`) : '新建自定义请求头方案'"
      style="width: 720px; max-width: 95vw;"
    >
      <NForm label-placement="top" size="small">
        <NGrid :cols="24" :x-gap="12">
          <NGridItem :span="12">
            <NFormItem label="方案标识 Key（全局唯一，供系统引用）" required>
              <NInput
                v-model:value="formModel.key"
                :disabled="isCurrentBuiltIn"
                placeholder="例如：my-cursor-emulation"
              />
            </NFormItem>
          </NGridItem>

          <NGridItem :span="12">
            <NFormItem label="显示名称" required>
              <NInput v-model:value="formModel.name" placeholder="例如：Cursor IDE 客户端模拟" />
            </NFormItem>
          </NGridItem>

          <NGridItem :span="24">
            <NFormItem label="说明与用途">
              <NInput
                v-model:value="formModel.description"
                placeholder="例如：模拟 Cursor 插件请求头，附带动态 Session 与 Request ID"
              />
            </NFormItem>
          </NGridItem>

          <NGridItem :span="12">
            <NFormItem label="排序序号">
              <NInputNumber v-model:value="formModel.sortOrder" :min="0" :max="999" style="width: 100%;" />
            </NFormItem>
          </NGridItem>

          <NGridItem :span="12">
            <NFormItem label="启用状态">
              <NSwitch v-model:value="formModel.isEnabled" />
              <span style="margin-left: 8px; font-size: 12px; color: var(--n-text-color-3);">
                {{ formModel.isEnabled ? '启用' : '禁用' }}
              </span>
            </NFormItem>
          </NGridItem>

          <NGridItem :span="24">
            <NFormItem label="请求头模板（JSON 键值对字典，支持动态占位符）">
              <div class="json-editor-wrap">
                <div class="placeholder-bar">
                  <span class="placeholder-label">可用动态占位符（点击复制）：</span>
                  <div class="placeholder-chips">
                    <NTooltip v-for="item in placeholderList" :key="item.tag">
                      <template #trigger>
                        <button
                          type="button"
                          class="placeholder-chip-btn"
                          @click="copyPlaceholder(item.tag)"
                        >
                          {{ item.tag }}
                        </button>
                      </template>
                      {{ item.desc }}
                    </NTooltip>
                  </div>
                </div>

                <NInput
                  v-model:value="formModel.headersJson"
                  type="textarea"
                  :rows="8"
                  placeholder='{\n  "User-Agent": "my-client/1.0.0",\n  "x-request-id": "${guid}"\n}'
                  style="font-family: monospace; font-size: 12px;"
                />

                <div class="preview-action-row">
                  <NButton size="tiny" secondary :loading="previewLoading" @click="handleLivePreview">
                    ⚡ 实时求值与测试
                  </NButton>
                </div>

                <div v-if="previewResult" class="preview-result-panel">
                  <div class="preview-result-title">实时求值结果预览（发往上游时的真实请求头）：</div>
                  <pre class="preview-pre">{{ JSON.stringify(previewResult, null, 2) }}</pre>
                </div>
              </div>
            </NFormItem>
          </NGridItem>
        </NGrid>
      </NForm>

      <template #footer>
        <NSpace justify="end">
          <NButton @click="showModal = false">取消</NButton>
          <NButton type="primary" :loading="modalSubmitting" @click="handleSave">保存方案</NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.header-profiles-tab {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
}

.tab-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  flex-wrap: wrap;
  gap: 12px;
  background: var(--n-card-color);
  padding: 14px 16px;
  border-radius: 8px;
  border: 1px solid var(--n-border-color);
}

.tab-title {
  margin: 0 0 4px 0;
  font-size: 15px;
  font-weight: 600;
  color: var(--n-text-color);
}

.tab-desc {
  margin: 0;
  font-size: 13px;
  color: var(--n-text-color-3);
  line-height: 1.5;
}

.profile-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 14px;
}

.profile-card {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  background: var(--n-card-color);
  border: 1px solid var(--n-border-color);
  border-radius: 8px;
  padding: 14px;
  transition: all 0.2s ease;
}

.profile-card:hover {
  border-color: var(--n-primary-color);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.05);
}

.profile-card-builtin {
  border-left: 3px solid var(--n-primary-color);
}

.profile-card-disabled {
  opacity: 0.65;
}

.card-head {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 8px;
  margin-bottom: 8px;
}

.title-wrap {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.profile-name {
  font-weight: 600;
  font-size: 14px;
  color: var(--n-text-color);
}

.profile-key {
  font-size: 12px;
  color: var(--n-text-color-3);
}

.profile-key code {
  background: var(--n-color-embedded);
  padding: 1px 4px;
  border-radius: 3px;
  font-family: monospace;
}

.card-body {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.profile-desc {
  margin: 0;
  font-size: 12px;
  color: var(--n-text-color-2);
  line-height: 1.5;
}

.headers-summary {
  display: flex;
  align-items: center;
  font-size: 12px;
  color: var(--n-text-color-3);
}

.summary-count {
  font-weight: 600;
  color: var(--n-primary-color);
}

.headers-code-preview {
  margin: 0;
  padding: 8px;
  background: var(--n-color-embedded);
  border-radius: 4px;
  font-family: monospace;
  font-size: 11px;
  color: var(--n-text-color-2);
  max-height: 120px;
  overflow-y: auto;
  white-space: pre-wrap;
  word-break: break-all;
}

.card-foot {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 12px;
  padding-top: 8px;
  border-top: 1px solid var(--n-border-color);
}

.foot-left {
  font-size: 12px;
  color: var(--n-text-color-3);
}

.json-editor-wrap {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.placeholder-bar {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  padding: 4px 6px;
  background: var(--n-color-embedded);
  border-radius: 4px;
  font-size: 11px;
}

.placeholder-label {
  color: var(--n-text-color-3);
}

.placeholder-chips {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.placeholder-chip-btn {
  background: var(--n-card-color);
  border: 1px solid var(--n-border-color);
  border-radius: 3px;
  padding: 1px 5px;
  font-family: monospace;
  font-size: 11px;
  color: var(--n-primary-color);
  cursor: pointer;
  transition: all 0.15s;
}

.placeholder-chip-btn:hover {
  background: var(--n-primary-color);
  color: #fff;
}

.preview-action-row {
  display: flex;
  justify-content: flex-end;
}

.preview-result-panel {
  background: var(--n-color-embedded);
  border-radius: 6px;
  padding: 8px 10px;
  font-size: 11px;
}

.preview-result-title {
  font-weight: 600;
  color: var(--n-text-color-2);
  margin-bottom: 4px;
}

.preview-pre {
  margin: 0;
  font-family: monospace;
  color: var(--n-primary-color);
  white-space: pre-wrap;
  word-break: break-all;
}

.loading-state,
.empty-state {
  display: flex;
  justify-content: center;
  align-items: center;
  padding: 48px 0;
}
</style>
