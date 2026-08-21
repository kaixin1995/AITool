<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import {
  NAlert,
  NButton,
  NDrawer,
  NDrawerContent,
  NModal,
  NSelect,
  NSpace,
  NSwitch,
  NTag,
  useMessage,
  type SelectOption
} from 'naive-ui'
import { getChatTargets, type ChatModelTarget } from '@/api/chat'
import { runAiDiagnose, type DeveloperAiDiagnosePayload, type DeveloperAiDiagnoseResult } from '@/api/developer'
import { createProfile, getProfile, listProfiles, updateProfile } from '@/api/compatibility'
import { listModels, updateModel } from '@/api/models'
import { parseCompatibilityRules, serializeCompatibilityRules, type CompatibilityRuleForm } from './compatibilityState'
import { setProtocolDiagnosticsPrefill } from './developerInvocationsState'

const props = defineProps<{
  show: boolean
  context: DeveloperAiDiagnosePayload | null
}>()

const emit = defineEmits<{
  (e: 'update:show', value: boolean): void
  (e: 'openDiagnostics'): void
}>()

const message = useMessage()
const targets = ref<ChatModelTarget[]>([])
const selectedMappingId = ref<string | null>(null)
const selectedModelId = ref<string | null>(null)
const enableReasoning = ref(false)
const reasoningEffort = ref('high')
const diagnosing = ref(false)
const result = ref<DeveloperAiDiagnoseResult | null>(null)
const showApplyModal = ref(false)
const applyMode = ref<'create' | 'existing'>('create')
const newProfileName = ref('')
const selectedProfileId = ref<string | null>(null)
const availableProfiles = ref<Array<{ label: string; value: string }>>([])
const applying = ref(false)

const reasoningOptions: SelectOption[] = [
  { label: '低 (Low)', value: 'low' },
  { label: '中 (Medium)', value: 'medium' },
  { label: '高 (High)', value: 'high' },
  { label: '超高 (XHigh)', value: 'xhigh' },
  { label: '最大 (Max)', value: 'max' }
]

function formatTargetLabel(target: ChatModelTarget): string {
  const siteName = target.siteName?.trim() || '未命名站点'
  const remoteModelName = target.siteModelName?.trim()
  const displayName = target.modelDisplayName?.trim()
  const modelLabel = displayName || remoteModelName || '未知模型'
  return `${siteName} / ${modelLabel}`
}

const targetOptions = computed<SelectOption[]>(() => {
  const uniqueTargets = new Map<string, ChatModelTarget>()
  for (const target of targets.value) {
    if (!uniqueTargets.has(target.mappingId)) uniqueTargets.set(target.mappingId, target)
  }
  return [...uniqueTargets.values()].map((target) => ({
    label: formatTargetLabel(target),
    value: target.mappingId
  }))
})

async function loadTargets(): Promise<void> {
  try {
    targets.value = await getChatTargets()
    if (!selectedMappingId.value && targets.value.length > 0) {
      selectedMappingId.value = targets.value[0].mappingId
      selectedModelId.value = targets.value[0].modelId
    }
  } catch {
    // 静默
  }
}

watch(selectedMappingId, (mappingId) => {
  const target = targets.value.find((item) => item.mappingId === mappingId)
  selectedModelId.value = target?.modelId ?? null
})

watch(() => props.show, async (val) => {
  if (val) {
    result.value = null
    await loadTargets()
  }
})

async function handleStartDiagnose(): Promise<void> {
  if (!props.context) return
  if (!selectedModelId.value) {
    message.warning('请先选择用于诊断的模型')
    return
  }

  diagnosing.value = true
  result.value = null

  try {
    const res = await runAiDiagnose({
      ...props.context,
      modelId: selectedModelId.value,
      mappingId: selectedMappingId.value || undefined,
      enableReasoning: enableReasoning.value,
      reasoningEffort: reasoningEffort.value
    })
    result.value = res
    if (!res.success) {
      message.error(res.error || 'AI 诊断失败')
    } else {
      message.success('AI 诊断完成')
    }
  } catch (err) {
    message.error(err instanceof Error ? err.message : '诊断请求失败')
  } finally {
    diagnosing.value = false
  }
}

function handleGoToDiagnostics(): void {
  if (!props.context) return
  setProtocolDiagnosticsPrefill({
    direction: 'request',
    sourceProtocol: props.context.clientProtocol || 'OpenAI',
    targetProtocol: props.context.upstreamProtocolType || 'OpenAI',
    streaming: false,
    modelName: props.context.requestModel || '',
    payload: props.context.originalRequestBody || '',
    targetSiteName: props.context.targetSiteName,
    attemptedModel: props.context.attemptedModel,
    statusCode: props.context.statusCode,
    errorMessage: props.context.errorMessage,
    trialRules: result.value?.rules ?? []
  })
  emit('update:show', false)
  emit('openDiagnostics')
}

async function openApplyModal(): Promise<void> {
  if (!result.value?.rules?.length || !props.context) return
  try {
    const profs = await listProfiles()
    availableProfiles.value = profs.filter(p => p.isEnabled).map(p => ({
      label: p.name,
      value: p.id
    }))
    applyMode.value = availableProfiles.value.length > 0 ? 'existing' : 'create'
    selectedProfileId.value = availableProfiles.value[0]?.value || null
    newProfileName.value = `修复规则-${props.context.requestModel || '通用'}`
    showApplyModal.value = true
  } catch {
    message.error('加载规则集列表失败')
  }
}

async function handleConfirmApply(): Promise<void> {
  if (!result.value?.rules?.length || !props.context) return
  applying.value = true
  try {
    let targetProfileId: string | null = null
    const rulesToApply = result.value.rules

    if (applyMode.value === 'create') {
      const name = newProfileName.value.trim() || '新建修复规则集'
      await createProfile({
        name,
        description: `由 AI 诊断自动生成于 ${new Date().toLocaleString()}`,
        rulesJson: serializeCompatibilityRules(rulesToApply),
        isEnabled: true
      })
      // 重新查一次取创建的 id
      const profs = await listProfiles()
      const created = profs.find(p => p.name === name)
      targetProfileId = created?.id ?? null
    } else if (selectedProfileId.value) {
      targetProfileId = selectedProfileId.value
      const detail = await getProfile(targetProfileId)
      const existing = parseCompatibilityRules(detail.rulesJson)
      // 追加规则并去重
      const merged = [...existing, ...rulesToApply]
      await updateProfile(targetProfileId, {
        name: detail.name,
        description: detail.description,
        rulesJson: serializeCompatibilityRules(merged),
        isEnabled: detail.isEnabled
      })
    }

    // 如果能找到对应的 ModelLibraryItem，自动为其绑定该规则集
    if (targetProfileId && props.context.requestModel) {
      const modelsResp = await listModels()
      let matchedModel: { id: string; modelName: string; displayName?: string; isEnabled: boolean; overrideReasoningEffort?: string } | undefined
      for (const group of modelsResp.vendorGroups) {
        matchedModel = group.models.find(m => m.modelName === props.context?.requestModel)
        if (matchedModel) break
      }
      if (matchedModel) {
        await updateModel(matchedModel.id, {
          modelName: matchedModel.modelName,
          displayName: matchedModel.displayName,
          isEnabled: matchedModel.isEnabled,
          overrideReasoningEffort: matchedModel.overrideReasoningEffort,
          compatibilityProfileId: targetProfileId
        })
        message.success(`已为模型【${matchedModel.modelName}】绑定修复规则集并热生效！`)
      } else {
        message.success('规则集已更新！')
      }
    } else {
      message.success('规则集已更新！')
    }

    showApplyModal.value = false
    emit('update:show', false)
  } catch (err) {
    message.error(err instanceof Error ? err.message : '应用规则失败')
  } finally {
    applying.value = false
  }
}
</script>

<template>
  <NDrawer
    :show="show"
    :width="680"
    placement="right"
    @update:show="(v) => emit('update:show', v)"
  >
    <NDrawerContent title="🤖 AI 智能故障诊断" closable>
      <div class="ai-diagnose-container">
        <!-- 顶部选择诊断模型区（参考 ChatTestPane） -->
        <div class="ai-model-selector-bar">
          <div class="selector-row">
            <div class="selector-item flex-1">
              <span class="selector-label">诊断模型</span>
              <NSelect
                v-model:value="selectedMappingId"
                :options="targetOptions"
                filterable
                placeholder="请选择站点模型"
                size="small"
              />
            </div>
            <div class="selector-item w-32">
              <span class="selector-label">思考等级</span>
              <NSelect
                v-model:value="reasoningEffort"
                :options="reasoningOptions"
                :disabled="!enableReasoning"
                size="small"
              />
            </div>
            <div class="selector-item w-20 pt-5">
              <NSwitch v-model:value="enableReasoning" size="small">
                <template #checked>深度思考</template>
                <template #unchecked>常规</template>
              </NSwitch>
            </div>
            <div class="pt-5">
              <NButton
                type="primary"
                size="small"
                :loading="diagnosing"
                @click="handleStartDiagnose"
              >
                {{ diagnosing ? '诊断中...' : '开始诊断' }}
              </NButton>
            </div>
          </div>
        </div>

        <!-- 现场快照 -->
        <div v-if="context" class="context-snapshot-card">
          <div class="snapshot-header">
            <span class="snapshot-title">故障现场快照</span>
            <NTag size="small" type="error">HTTP {{ context.statusCode || 500 }}</NTag>
          </div>
          <div class="snapshot-grid">
            <div class="snapshot-item">
              <span class="snapshot-label">目标模型</span>
              <strong>{{ context.requestModel }} ➔ {{ context.attemptedModel || '-' }}</strong>
            </div>
            <div class="snapshot-item">
              <span class="snapshot-label">上游站点</span>
              <strong>{{ context.targetSiteName || '-' }}</strong>
            </div>
            <div class="snapshot-item">
              <span class="snapshot-label">协议链路</span>
              <strong>{{ context.clientProtocol }} ➔ {{ context.upstreamProtocolType }} ({{ context.forwardingMode }})</strong>
            </div>
          </div>
          <div v-if="context.errorMessage" class="snapshot-error">
            <div class="snapshot-label">上游报错原文</div>
            <pre class="error-pre">{{ context.errorMessage }}</pre>
          </div>
        </div>

        <!-- 诊断结果区 -->
        <div v-if="result" class="diagnosis-result-card">
          <div v-if="!result.success" class="result-error">
            <NAlert type="error" :show-icon="true" title="诊断失败">
              {{ result.error || '未能获得有效诊断结果' }}
            </NAlert>
          </div>

          <template v-else>
            <!-- 思考过程 -->
            <div v-if="result.reasoning" class="reasoning-box">
              <div class="reasoning-title">🧠 模型思考过程</div>
              <pre class="reasoning-text">{{ result.reasoning }}</pre>
            </div>

            <!-- 核心结论 -->
            <div v-if="result.summary" class="result-summary-box">
              <div class="summary-title">💡 诊断核心结论</div>
              <p class="summary-text">{{ result.summary }}</p>
            </div>

            <div v-if="result.rootCause" class="result-section">
              <div class="section-title">🔍 根因分析</div>
              <div class="section-content">{{ result.rootCause }}</div>
            </div>

            <div v-if="result.suggestedAction" class="result-section">
              <div class="section-title">🛠️ 建议操作</div>
              <div class="section-content">{{ result.suggestedAction }}</div>
            </div>

            <!-- 推荐规则 -->
            <div v-if="result.rules && result.rules.length > 0" class="rules-section">
              <div class="rules-header">
                <span class="rules-title">⚡ AI 推荐生成的兼容规则</span>
                <span class="rules-count">共 {{ result.rules.length }} 条</span>
              </div>
              <div class="rules-list">
                <div v-for="(rule, idx) in result.rules" :key="idx" class="rule-chip">
                  <NTag type="info" size="small">{{ rule.op }}</NTag>
                  <span v-if="rule.op === 'strip'" class="rule-detail">剔除字段: <code>{{ rule.target }}</code></span>
                  <span v-else-if="rule.op === 'rename'" class="rule-detail">重命名: <code>{{ rule.from }} ➔ {{ rule.to }}</code></span>
                  <span v-else-if="rule.op === 'default'" class="rule-detail">补默认值: <code>{{ rule.key }} = {{ rule.value }}</code></span>
                  <span v-else-if="rule.op === 'keep_reasoning'" class="rule-detail">保留 assistant 思维链 (reasoning_content)</span>
                  <NTag size="tiny" secondary class="ml-auto">{{ rule.scope }}</NTag>
                </div>
              </div>
            </div>

            <!-- 完整回答展开 -->
            <div v-if="result.content" class="raw-content-box mt-2">
              <div class="text-xs font-bold text-slate-500 mb-1">📋 AI 诊断报告完整详情</div>
              <pre class="raw-pre">{{ result.content }}</pre>
            </div>

            <!-- 底部操作按钮 -->
            <div class="result-actions-bar">
              <NSpace>
                <NButton
                  v-if="result.rules && result.rules.length > 0"
                  type="primary"
                  @click="openApplyModal"
                >
                  ⚡ 一键应用并修复此模型
                </NButton>
                <NButton secondary @click="handleGoToDiagnostics">
                  🧪 带规则到协议诊断台调试
                </NButton>
              </NSpace>
            </div>
          </template>
        </div>

        <div v-else-if="!diagnosing" class="empty-hint">
          点击上方【开始诊断】，AI 将结合调用现场和报错信息，为您深度分析根因并生成修复规则。
        </div>
      </div>
    </NDrawerContent>
  </NDrawer>

  <!-- 应用修复规则弹窗 -->
  <NModal
    v-model:show="showApplyModal"
    preset="dialog"
    title="应用并绑定修复规则"
    positive-text="确认应用"
    negative-text="取消"
    :loading="applying"
    @positive-click="handleConfirmApply"
  >
    <div class="apply-modal-content">
      <p class="mb-3 text-sm text-slate-600 dark:text-slate-300">
        确认将 AI 推荐的 <strong>{{ result?.rules?.length }}</strong> 条兼容规则保存并绑定到模型
        <NTag size="small" type="primary">{{ context?.requestModel }}</NTag>：
      </p>

      <div class="space-y-3">
        <div>
          <span class="block text-xs text-slate-500 mb-1">保存方式</span>
          <NSelect
            v-model:value="applyMode"
            :options="[
              { label: '新建兼容规则集', value: 'create' },
              { label: '追加到已有规则集', value: 'existing', disabled: availableProfiles.length === 0 }
            ]"
            size="small"
          />
        </div>

        <div v-if="applyMode === 'create'">
          <span class="block text-xs text-slate-500 mb-1">规则集名称</span>
          <input
            v-model="newProfileName"
            type="text"
            class="w-full px-2.5 py-1.5 text-sm rounded border border-slate-300 dark:border-slate-700 bg-white dark:bg-slate-800 focus:outline-none focus:ring-1 focus:ring-primary"
            placeholder="请输入规则集名称"
          />
        </div>

        <div v-else>
          <span class="block text-xs text-slate-500 mb-1">选择已有规则集</span>
          <NSelect
            v-model:value="selectedProfileId"
            :options="availableProfiles"
            size="small"
          />
        </div>
      </div>
    </div>
  </NModal>
</template>

<style scoped>
.ai-diagnose-container {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.ai-model-selector-bar {
  padding: 12px;
  background: var(--bg-card, #f8fafc);
  border: 1px solid var(--border-color, #e2e8f0);
  border-radius: 8px;
}

.selector-row {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.selector-item {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.selector-label {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted, #64748b);
}

.context-snapshot-card {
  padding: 12px;
  background: rgba(239, 68, 68, 0.04);
  border: 1px solid rgba(239, 68, 68, 0.2);
  border-radius: 8px;
}

.snapshot-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 8px;
}

.snapshot-title {
  font-size: 13px;
  font-weight: 700;
  color: #ef4444;
}

.snapshot-grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: 8px;
  font-size: 12px;
  margin-bottom: 8px;
}

.snapshot-item {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.snapshot-label {
  font-size: 11px;
  color: #64748b;
}

.snapshot-error {
  margin-top: 6px;
}

.error-pre {
  margin: 4px 0 0;
  padding: 6px 8px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 11px;
  background: rgba(0, 0, 0, 0.05);
  border-radius: 4px;
  color: #dc2626;
  white-space: pre-wrap;
  word-break: break-all;
  max-height: 120px;
  overflow-y: auto;
}

.diagnosis-result-card {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 14px;
  background: var(--bg-card, #ffffff);
  border: 1px solid var(--border-color, #e2e8f0);
  border-radius: 8px;
}

.reasoning-box {
  padding: 10px;
  background: rgba(147, 51, 234, 0.05);
  border: 1px solid rgba(147, 51, 234, 0.2);
  border-radius: 6px;
}

.reasoning-title {
  font-size: 12px;
  font-weight: 600;
  color: #9333ea;
  margin-bottom: 4px;
}

.reasoning-text {
  margin: 0;
  font-size: 11px;
  font-family: inherit;
  color: #6b21a8;
  white-space: pre-wrap;
  max-height: 160px;
  overflow-y: auto;
}

.result-summary-box {
  padding: 10px;
  background: rgba(16, 185, 129, 0.08);
  border-left: 4px solid #10b981;
  border-radius: 4px;
}

.summary-title {
  font-size: 13px;
  font-weight: 700;
  color: #059669;
  margin-bottom: 2px;
}

.summary-text {
  margin: 0;
  font-size: 13px;
  color: var(--text-color, #1e293b);
  line-height: 1.5;
}

.result-section {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.section-title {
  font-size: 12px;
  font-weight: 700;
  color: var(--text-color, #334155);
}

.section-content {
  font-size: 12px;
  line-height: 1.6;
  color: var(--text-color, #475569);
}

.rules-section {
  padding: 10px;
  background: #f1f5f9;
  border-radius: 6px;
}

:global(.dark) .rules-section {
  background: #1e293b;
}

.rules-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 8px;
}

.rules-title {
  font-size: 12px;
  font-weight: 700;
  color: #0284c7;
}

.rules-count {
  font-size: 11px;
  color: #64748b;
}

.rules-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.rule-chip {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  background: #ffffff;
  border: 1px solid #e2e8f0;
  border-radius: 4px;
  font-size: 12px;
}

:global(.dark) .rule-chip {
  background: #0f172a;
  border-color: #334155;
}

.rule-detail {
  font-size: 12px;
}

.rule-detail code {
  padding: 2px 4px;
  background: rgba(0, 0, 0, 0.06);
  border-radius: 3px;
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
}

.result-actions-bar {
  margin-top: 8px;
  padding-top: 12px;
  border-top: 1px solid var(--border-color, #e2e8f0);
}

.empty-hint {
  text-align: center;
  padding: 40px 20px;
  color: var(--text-muted, #94a3b8);
  font-size: 13px;
}

.raw-content-box {
  font-size: 12px;
  background: var(--bg-card, #f8fafc);
  padding: 10px;
  border-radius: 6px;
}

.raw-pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-all;
}
</style>
