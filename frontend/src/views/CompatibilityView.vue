<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { NButton, NCard, NCheckbox, NEmpty, NForm, NFormItem, NInput, NPopconfirm, NSelect, NSpace, NTag, useMessage, type SelectOption } from 'naive-ui'
import * as api from '@/api/compatibility'
import type { CompatibilityProfileListItem } from '@/api/compatibility'
import PageHeader from '@/components/PageHeader.vue'
import { parseCompatibilityRules, serializeCompatibilityRules, type CompatibilityRuleForm } from './compatibilityState'

const props = withDefaults(defineProps<{ embedded?: boolean }>(), { embedded: false })
const message = useMessage()
const loading = ref(false)
const items = ref<CompatibilityProfileListItem[]>([])
const editorVisible = ref(false)
const editingId = ref<string | null>(null)
const saving = ref(false)
const form = reactive({
  name: '',
  description: '',
  isEnabled: true,
  rules: [] as CompatibilityRuleForm[]
})

const operationOptions: SelectOption[] = [
  { label: '剔除 strip', value: 'strip' },
  { label: '重命名 rename', value: 'rename' },
  { label: '补默认值 default', value: 'default' },
  { label: '保留思维链 keep_reasoning', value: 'keep_reasoning' }
]
const scopeOptions: SelectOption[] = [
  { label: '两者 all', value: 'all' },
  { label: '仅透传', value: 'passthrough' },
  { label: '仅兼容中转', value: 'bridge' }
]

async function load(): Promise<void> {
  loading.value = true
  try {
    items.value = await api.listProfiles()
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  Object.assign(form, { name: '', description: '', isEnabled: true, rules: [] })
  editorVisible.value = true
}

async function openEdit(row: CompatibilityProfileListItem): Promise<void> {
  const detail = await api.getProfile(row.id)
  editingId.value = row.id
  Object.assign(form, {
    name: detail.name,
    description: detail.description,
    isEnabled: detail.isEnabled,
    rules: parseCompatibilityRules(detail.rulesJson)
  })
  editorVisible.value = true
}

function addRule(): void {
  form.rules.push({ op: 'strip', target: '', scope: 'all' })
}

function removeRule(index: number): void {
  form.rules.splice(index, 1)
}

async function handleSave(): Promise<void> {
  if (!form.name.trim()) {
    message.warning('名称不能为空')
    return
  }

  saving.value = true
  try {
    const payload = {
      name: form.name.trim(),
      description: form.description.trim(),
      isEnabled: form.isEnabled,
      rulesJson: serializeCompatibilityRules(form.rules)
    }
    if (editingId.value) {
      await api.updateProfile(editingId.value, payload)
      message.success('已更新')
    } else {
      await api.createProfile(payload)
      message.success('已创建')
    }
    editorVisible.value = false
    editingId.value = null
    await load()
  } finally {
    saving.value = false
  }
}

async function handleToggle(row: CompatibilityProfileListItem): Promise<void> {
  await api.toggleProfile(row.id)
  await load()
  if (editingId.value === row.id) form.isEnabled = !row.isEnabled
}

async function handleDelete(row: CompatibilityProfileListItem): Promise<void> {
  await api.deleteProfile(row.id)
  if (editingId.value === row.id) {
    editingId.value = null
    editorVisible.value = false
  }
  message.success('已删除')
  await load()
}

onMounted(load)
</script>

<template>
  <div :class="{ 'page-container': !props.embedded, 'compatibility-page': true }">
    <PageHeader v-if="!props.embedded" title="兼容规则集" subtitle="独立维护字段级兼容规则（剔除/重命名/补默认值），可被多个模型引用，避免重复配置" />

    <div class="compatibility-layout">
      <NCard class="profile-panel" size="small">
        <template #header>
          <div class="panel-header">
            <strong>规则集列表</strong>
            <NButton size="small" type="primary" @click="openCreate">新建规则集</NButton>
          </div>
        </template>

        <NEmpty v-if="!loading && items.length === 0" description="暂无规则集，点击右上角新建" />
        <div v-else class="profile-list">
          <article
            v-for="item in items"
            :key="item.id"
            class="profile-item"
            :class="{ active: editingId === item.id && editorVisible }"
            @click="openEdit(item)"
          >
            <div class="profile-title">
              <strong>{{ item.name }}</strong>
              <NTag size="tiny" :type="item.isEnabled ? 'success' : 'error'" :bordered="false">
                {{ item.isEnabled ? '启用' : '禁用' }}
              </NTag>
            </div>
            <div class="profile-description">{{ item.description || '（无说明）' }}</div>
            <div class="profile-meta">
              <span>规则 {{ item.ruleCount }} 条</span>
              <NSpace size="small">
                <NButton size="tiny" quaternary @click.stop="handleToggle(item)">
                  {{ item.isEnabled ? '禁用' : '启用' }}
                </NButton>
                <NPopconfirm @positive-click="handleDelete(item)">
                  <template #trigger>
                    <NButton size="tiny" quaternary type="error" @click.stop>删除</NButton>
                  </template>
                  确认删除此规则集？引用它的模型将变为不应用规则集。
                </NPopconfirm>
              </NSpace>
            </div>
          </article>
        </div>
      </NCard>

      <NCard class="editor-panel" size="small">
        <template #header>
          <div>
            <strong>{{ editingId ? `编辑：${form.name}` : editorVisible ? '新建规则集' : '编辑规则集' }}</strong>
            <div class="editor-hint">
              {{ editorVisible ? '修改后点击保存生效' : '从左侧选择一个规则集，或点击新建' }}
            </div>
          </div>
        </template>

        <NEmpty v-if="!editorVisible" description="请选择或新建规则集" />
        <NForm v-else label-placement="top">
          <NFormItem label="名称">
            <NInput v-model:value="form.name" placeholder="如 GPT-5 兼容、z.ai 兼容" />
          </NFormItem>
          <NFormItem label="说明">
            <NInput v-model:value="form.description" placeholder="适用场景描述" />
          </NFormItem>
          <NCheckbox v-model:checked="form.isEnabled">
            启用（禁用的规则集不会出现在模型下拉里，也不会生效）
          </NCheckbox>

          <div class="rules-header">
            <strong>规则</strong>
            <NButton size="small" secondary @click="addRule">+ 添加规则</NButton>
          </div>
          <div class="rules-tip">
            每条规则可选择操作类型与生效路径；strip 的 target 支持字段路径，裸字段名自动作用于 messages 每条。
          </div>

          <NEmpty v-if="form.rules.length === 0" description="暂无规则，点击“添加规则”" size="small" />
          <div v-else class="rules-list">
            <div v-for="(rule, index) in form.rules" :key="index" class="rule-row">
              <NSelect v-model:value="rule.op" :options="operationOptions" />
              <div class="rule-fields">
                <NInput v-if="rule.op === 'strip'" v-model:value="rule.target" placeholder="target 字段路径" />
                <template v-else-if="rule.op === 'rename'">
                  <NInput v-model:value="rule.from" placeholder="from 旧名" />
                  <NInput v-model:value="rule.to" placeholder="to 新名" />
                </template>
                <template v-else-if="rule.op === 'keep_reasoning'">
                  <span class="rule-hint">Anthropic→OpenAI 转换时保留 thinking 为 reasoning_content（deepseek 等上游工具调用时要求回传）</span>
                </template>
                <template v-else>
                  <NInput v-model:value="rule.key" placeholder="key 字段名" />
                  <NInput v-model:value="rule.value" placeholder="value 值" />
                </template>
              </div>
              <NSelect v-model:value="rule.scope" :options="scopeOptions" />
              <NButton type="error" secondary @click="removeRule(index)">×</NButton>
            </div>
          </div>

          <NSpace class="editor-actions">
            <NButton type="primary" :loading="saving" @click="handleSave">保存</NButton>
            <NButton @click="editorVisible = false; editingId = null">取消</NButton>
          </NSpace>
        </NForm>
      </NCard>
    </div>
  </div>
</template>

<style scoped>
.compatibility-layout {
  display: grid;
  grid-template-columns: 360px minmax(0, 1fr);
  gap: 16px;
}

.panel-header,
.profile-title,
.profile-meta,
.rules-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.profile-list,
.rules-list {
  display: grid;
  gap: 8px;
}

.profile-item {
  padding: 12px;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
  cursor: pointer;
  transition: border-color 0.15s, background-color 0.15s;
}

.profile-item:hover,
.profile-item.active {
  border-color: #0d6efd;
}

.profile-item.active {
  background: rgba(13, 110, 253, 0.06);
}

.profile-description,
.profile-meta,
.editor-hint,
.rules-tip {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.profile-description {
  margin-top: 5px;
  overflow-wrap: anywhere;
}

.profile-meta {
  margin-top: 8px;
}

.editor-hint {
  margin-top: 4px;
  font-weight: 400;
}

.rules-header {
  margin: 18px 0 5px;
}

.rules-tip {
  margin-bottom: 10px;
  line-height: 1.6;
}

.rule-row {
  display: grid;
  grid-template-columns: 150px minmax(200px, 1fr) 150px 40px;
  gap: 8px;
  align-items: center;
  padding: 9px;
  border-radius: 8px;
  background: var(--bg-page);
}

.rule-fields {
  display: flex;
  min-width: 0;
  gap: 8px;
}

.rule-hint {
  font-size: 12px;
  color: var(--n-text-color-3, #909399);
  line-height: 1.5;
  align-self: center;
}

.editor-actions {
  margin-top: 20px;
}

@media (max-width: 900px) {
  .compatibility-layout {
    grid-template-columns: minmax(0, 1fr);
  }

  .rule-row {
    grid-template-columns: minmax(0, 1fr);
  }

  .rule-fields {
    flex-direction: column;
  }
}
</style>
