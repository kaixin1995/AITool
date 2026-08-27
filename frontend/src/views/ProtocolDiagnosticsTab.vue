<script setup lang="ts">
import { computed, inject, nextTick, onMounted, ref, watch, type Ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NCollapse,
  NCollapseItem,
  NDivider,
  NDrawer,
  NDrawerContent,
  NEmpty,
  NForm,
  NFormItem,
  NGrid,
  NGridItem,
  NInput,
  NInputNumber,
  NModal,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NSpace,
  NSpin,
  NSwitch,
  NTag,
  NTimeline,
  NTimelineItem,
  NTooltip,
  useMessage,
  type SelectOption
} from 'naive-ui'
import { getChatTargets, type ChatModelTarget } from '@/api/chat'
import { listSites, type SiteListItem } from '@/api/sites'
import {
  runAutoDiagnoseLoop,
  getDiagnosticDumps,
  getDiagnosticDumpContent,
  getDiagnosticConfig,
  updateDiagnosticConfig,
  type AutoDiagnoseLoopPayload,
  type AutoDiagnoseLoopResult,
  type DiagnosticDumpItem,
  type DiagnosticConfig
} from '@/api/developer'
import {
  createProfile,
  getProfile,
  listProfiles,
  updateProfile,
  type CompatibilityProfileListItem
} from '@/api/compatibility'
import {
  parseCompatibilityRules,
  serializeCompatibilityRules,
  type CompatibilityRuleForm
} from './compatibilityState'
import JsonDiffView from '@/components/JsonDiffView.vue'
import { takeProtocolDiagnosticsPrefill } from './developerInvocationsState'

const message = useMessage()

// ────────────────────────────────────────────────
// AI 自动试错与自愈调试状态
// ────────────────────────────────────────────────
const chatTargets = ref<ChatModelTarget[]>([])
const availableSites = ref<SiteListItem[]>([])

// 诊断大模型参数
const selectedDiagnosticMappingId = ref<string | null>(null)
const selectedDiagnosticModelId = ref<string | null>(null)
const enableReasoning = ref(false)
const reasoningEffort = ref('high')

// 目标测试站点与模型
const selectedTargetSiteId = ref<string | null>(null)
const targetModelName = ref('gemini-3.7-flash')
const sourceProtocol = ref<string>('OpenAI')
const targetProtocol = ref<string>('Gemini')
const maxRounds = ref(3)

// 故障现场输入
const originalRequestBody = ref('{\n  "model": "gemini-3.7-flash",\n  "messages": [\n    { "role": "user", "content": "hello" }\n  ]\n}')
const initialPreparedRequestBody = ref('')
const initialErrorResponse = ref('HTTP 400 Bad Request: {"error": {"message": "Invalid argument or unsupported parameter."}}')
const initialStatusCode = ref(400)

// 调试执行状态与结果
const loopLoading = ref(false)
const loopResult = ref<AutoDiagnoseLoopResult | null>(null)
const resultSectionRef = ref<HTMLElement | null>(null)

// 抓包载入弹窗
const showLoadDumpModal = ref(false)
const recentDumps = ref<DiagnosticDumpItem[]>([])
const loadingDumps = ref(false)

// ────────────────────────────────────────────────
// 规则保存与应用弹窗
// ────────────────────────────────────────────────
const saveDialogVisible = ref(false)
const saveTargetMode = ref<'create' | 'existing'>('create')
const newProfileName = ref('')
const existingProfileId = ref<string | null>(null)
const saveLoading = ref(false)
const draftRules = ref<CompatibilityRuleForm[]>([])
const profiles = ref<CompatibilityProfileListItem[]>([])

const prefillSignal = inject<Ref<number>>('protocol-diagnostics-prefill')

const protocolOptions = [
  { label: 'OpenAI Chat', value: 'OpenAI' },
  { label: 'Anthropic Messages', value: 'Anthropic' },
  { label: 'OpenAI Responses', value: 'Responses' },
  { label: 'Google Gemini', value: 'Gemini' }
]

function formatTargetLabel(target: ChatModelTarget): string {
  const siteName = target.siteName?.trim() || '未命名站点'
  const remoteModelName = target.siteModelName?.trim()
  const displayName = target.modelDisplayName?.trim()
  const modelLabel = displayName || remoteModelName || '未知模型'
  return `${siteName} / ${modelLabel}`
}

const diagnosticModelOptions = computed<SelectOption[]>(() => {
  const unique = new Map<string, ChatModelTarget>()
  for (const t of chatTargets.value) {
    if (!unique.has(t.mappingId)) unique.set(t.mappingId, t)
  }
  return [...unique.values()].map((t) => ({
    label: formatTargetLabel(t),
    value: t.mappingId
  }))
})

const targetSiteOptions = computed<SelectOption[]>(() => {
  return availableSites.value.map((s) => ({
    label: `${s.name} (${s.protocolType || 'OpenAI'})`,
    value: s.id
  }))
})

watch(selectedDiagnosticMappingId, (mappingId) => {
  const t = chatTargets.value.find((x) => x.mappingId === mappingId)
  selectedDiagnosticModelId.value = t?.modelId ?? null
})

watch(selectedTargetSiteId, (siteId) => {
  const s = availableSites.value.find((x) => x.id === siteId)
  if (s) {
    targetProtocol.value = s.protocolType || 'OpenAI'
  }
})

async function loadInitData(): Promise<void> {
  try {
    const [targetsData, sitesData, profilesData] = await Promise.all([
      getChatTargets(),
      listSites(true),
      listProfiles()
    ])
    chatTargets.value = targetsData
    if (!selectedDiagnosticMappingId.value && targetsData.length > 0) {
      selectedDiagnosticMappingId.value = targetsData[0].mappingId
      selectedDiagnosticModelId.value = targetsData[0].modelId
    }

    availableSites.value = sitesData
    if (!selectedTargetSiteId.value && sitesData.length > 0) {
      selectedTargetSiteId.value = sitesData[0].id
      targetProtocol.value = sitesData[0].protocolType || 'OpenAI'
    }

    profiles.value = profilesData.filter((p) => p.isEnabled)
  } catch {
    // 静默降级
  }
}

async function openLoadDumpModal(): Promise<void> {
  showLoadDumpModal.value = true
  loadingDumps.value = true
  try {
    const data = await getDiagnosticDumps(30)
    recentDumps.value = Array.isArray(data) ? data : []
  } catch {
    message.error('加载最近抓包列表失败')
  } finally {
    loadingDumps.value = false
  }
}

async function applyDumpToDiagnostic(dump: DiagnosticDumpItem): Promise<void> {
  try {
    const dumpContent = await getDiagnosticDumpContent(dump.fileName)

    sourceProtocol.value = dump.clientProtocol || dumpContent?.diagnostic?.clientProtocol || 'OpenAI'
    targetProtocol.value = dump.upstreamProtocol || dumpContent?.diagnostic?.upstreamProtocol || 'Gemini'
    targetModelName.value = dump.attemptedModel || dump.requestModel || dumpContent?.diagnostic?.attemptedModel || ''

    const siteName = dump.siteName || dumpContent?.diagnostic?.siteName
    const matchedSite = availableSites.value.find((s) => s.name === siteName || (dumpContent?.diagnostic?.siteId && s.id === dumpContent.diagnostic.siteId))
    if (matchedSite) {
      selectedTargetSiteId.value = matchedSite.id
    }

    initialStatusCode.value = dump.statusCode || dumpContent?.diagnostic?.httpStatusCode || 400

    // 载入原始请求体
    if (dumpContent?.clientRequestBody) {
      originalRequestBody.value = typeof dumpContent.clientRequestBody === 'string'
        ? dumpContent.clientRequestBody
        : JSON.stringify(dumpContent.clientRequestBody, null, 2)
    }

    // 载入准备/转换后的请求体
    if (dumpContent?.preparedRequestBody) {
      initialPreparedRequestBody.value = typeof dumpContent.preparedRequestBody === 'string'
        ? dumpContent.preparedRequestBody
        : JSON.stringify(dumpContent.preparedRequestBody, null, 2)
    } else {
      initialPreparedRequestBody.value = ''
    }

    // 载入上游错误响应正文
    if (dumpContent?.upstreamResponseBody) {
      initialErrorResponse.value = typeof dumpContent.upstreamResponseBody === 'string'
        ? dumpContent.upstreamResponseBody
        : JSON.stringify(dumpContent.upstreamResponseBody, null, 2)
    } else if (dumpContent?.diagnostic?.errorMessage || dump.errorSummary) {
      initialErrorResponse.value = dumpContent?.diagnostic?.errorMessage || dump.errorSummary
    }

    showLoadDumpModal.value = false
    message.success(`已完整载入抓包现场：${dump.routeName || dump.requestModel}`)
  } catch (err: any) {
    message.error(`载入抓包现场详情失败: ${err.message || '未知错误'}`)
  }
}

async function handleStartAutoDiagnoseLoop(): Promise<void> {
  if (!selectedDiagnosticModelId.value) {
    message.warning('请先选择用于分析的 AI 诊断模型')
    return
  }

  const site = availableSites.value.find((s) => s.id === selectedTargetSiteId.value)
  const payload: AutoDiagnoseLoopPayload = {
    diagnosticModelId: selectedDiagnosticModelId.value,
    diagnosticMappingId: selectedDiagnosticMappingId.value || undefined,
    enableReasoning: enableReasoning.value,
    reasoningEffort: reasoningEffort.value,

    targetSiteId: selectedTargetSiteId.value || undefined,
    targetSiteName: site?.name || undefined,
    targetModelName: targetModelName.value.trim() || 'gemini-3.7-flash',
    sourceProtocol: sourceProtocol.value,
    targetProtocol: targetProtocol.value,

    originalRequestBody: originalRequestBody.value,
    initialPreparedRequestBody: initialPreparedRequestBody.value || undefined,
    initialErrorResponse: initialErrorResponse.value,
    initialStatusCode: Number(initialStatusCode.value) || 400,
    maxRounds: maxRounds.value
  }

  loopLoading.value = true
  loopResult.value = null

  try {
    const res = await runAutoDiagnoseLoop(payload)
    loopResult.value = res
    if (res.error) {
      message.error(`自愈调试执行失败: ${res.error}`)
    } else if (res.success) {
      message.success(`自愈调试成功！共尝试 ${res.totalRounds} 轮，上游已正常响应 200 OK`)
    } else {
      message.warning(`自愈调试已尝试 ${res.totalRounds} 轮，未能成功收敛：${res.summary || '请查看下方分析报告'}`)
    }

    await nextTick()
    resultSectionRef.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  } catch (err: any) {
    message.error(err?.message || '执行自愈调试发生异常')
  } finally {
    loopLoading.value = false
  }
}

function openSaveRulesDialog(rules: CompatibilityRuleForm[]): void {
  draftRules.value = rules && rules.length > 0 ? rules : []
  saveTargetMode.value = 'create'
  newProfileName.value = `自愈规则 - ${sourceProtocol.value}➔${targetProtocol.value} (${targetModelName.value})`
  existingProfileId.value = null
  saveDialogVisible.value = true
}

async function saveRules(): Promise<void> {
  saveLoading.value = true
  try {
    const rulesJson = serializeCompatibilityRules(draftRules.value)
    if (saveTargetMode.value === 'create') {
      await createProfile({
        name: newProfileName.value.trim() || '自愈规则集',
        description: `由 AI 协议自愈调试自动生成 (${sourceProtocol.value} ➔ ${targetProtocol.value})`,
        rulesJson,
        isEnabled: true
      })
      message.success('已新建并启用兼容规则集')
    } else if (existingProfileId.value) {
      const detail = await getProfile(existingProfileId.value)
      const existing = parseCompatibilityRules(detail.rulesJson)
      await updateProfile(existingProfileId.value, {
        name: detail.name,
        description: detail.description,
        rulesJson: serializeCompatibilityRules([...existing, ...draftRules.value]),
        isEnabled: detail.isEnabled
      })
      message.success('已追加规则到指定规则集')
    }
    saveDialogVisible.value = false
    const profilesData = await listProfiles()
    profiles.value = profilesData.filter((p) => p.isEnabled)
  } catch (err: any) {
    message.error(err instanceof Error ? err.message : '保存失败')
  } finally {
    saveLoading.value = false
  }
}

function copyText(text: string, tip = '已复制到剪贴板'): void {
  navigator.clipboard.writeText(text).then(
    () => message.success(tip),
    () => message.error('复制失败，请手动选择复制')
  )
}

// 接收外部 prefill：优先在挂载后消费（页签 display-directive='if'，跨页签激活时组件全新挂载，
// prefillSignal 信号在挂载前 bump 不会触发 watcher——挂载消费覆盖 hash 跳转/父组件切页签所有路径）。
function applyPendingPrefill(): void {
  const prefill = takeProtocolDiagnosticsPrefill()
  if (!prefill) return

  sourceProtocol.value = prefill.sourceProtocol || 'OpenAI'
  targetProtocol.value = prefill.targetProtocol || 'Gemini'
  targetModelName.value = prefill.attemptedModel || prefill.modelName || ''
  originalRequestBody.value = prefill.payload || ''
  if (prefill.preparedPayload) {
    initialPreparedRequestBody.value = prefill.preparedPayload
  }
  if (prefill.errorMessage) {
    initialErrorResponse.value = prefill.errorMessage
  }
  if (prefill.statusCode) {
    initialStatusCode.value = prefill.statusCode
  }

  const matchedSite = availableSites.value.find((s) => s.name === prefill.targetSiteName)
  if (matchedSite) {
    selectedTargetSiteId.value = matchedSite.id
  }
}

// 兜底：组件已挂载时（同页签内）信号变化仍可触发消费；take-once 语义保证不重复应用。
watch(
  () => prefillSignal?.value,
  () => applyPendingPrefill()
)

const showConfigModal = ref(false)
const configLoading = ref(false)
const configSaving = ref(false)
const configForm = ref<DiagnosticConfig>({
  maxBodyLengthMb: 4,
  maxRoundResponseMb: 2,
  retentionDays: 3,
  maxFailuresPerDay: 50
})

async function loadConfig(): Promise<void> {
  configLoading.value = true
  try {
    const cfg = await getDiagnosticConfig()
    configForm.value = { ...cfg }
  } catch (err: any) {
    // 静默忽略
  } finally {
    configLoading.value = false
  }
}

async function handleSaveConfig(): Promise<void> {
  configSaving.value = true
  try {
    const updated = await updateDiagnosticConfig(configForm.value)
    configForm.value = { ...updated }
    message.success('诊断限制参数已更新并即时生效！')
    showConfigModal.value = false
  } catch (err: any) {
    message.error(err?.message || '保存诊断参数失败')
  } finally {
    configSaving.value = false
  }
}

onMounted(async () => {
  // 先等基础数据（站点列表）就绪再消费 prefill，保证站点名匹配能命中。
  await loadInitData()
  void loadConfig()
  applyPendingPrefill()
})
</script>

<template>
  <div class="protocol-diagnostics-tab flex flex-col gap-4">
    <!-- 顶部工作台头部 -->
    <div class="p-4 rounded-xl border border-slate-200/80 dark:border-slate-800 bg-gradient-to-r from-slate-50 to-blue-50/50 dark:from-slate-900 dark:to-slate-800/60 flex flex-col md:flex-row md:items-center justify-between gap-4">
      <div class="flex flex-col gap-1">
        <div class="flex items-center gap-2">
          <span class="font-bold text-base text-slate-800 dark:text-slate-100">AI 智能协议自愈与实操调试工作台</span>
          <NTag type="primary" size="small" round>实机自动循环微调</NTag>
        </div>
        <div class="text-xs text-slate-500 max-w-3xl leading-relaxed">
          当代理请求因参数不兼容（如 Google Gemini / Anthropic / OpenAI 报 400/422）失败时，借助 AI 深度分析根因 ➔ 自动修改请求体 ➔ 向上游发起真实试探 ➔ 若失败反馈给 AI 迭代微调 ➔ 成功后输出归因报告并一键保存为兼容规则。
        </div>
      </div>

      <!-- 动作栏 -->
      <div class="flex items-center gap-2.5 shrink-0 flex-wrap">
        <NButton size="small" secondary @click="showConfigModal = true">
          ⚙️ 限制设置 (当前 {{ configForm.maxBodyLengthMb }}MB)
        </NButton>
        <NButton size="small" secondary type="info" @click="openLoadDumpModal">
          📁 从抓包现场载入
        </NButton>
      </div>
    </div>

    <!-- AI 自动试错与自愈调试工作台 -->
    <div class="flex flex-col gap-4">
      <!-- 参数配置与输入卡片 -->
      <NCard title="调试参数与目标配置" size="small">
        <NGrid :cols="24" :x-gap="12" :y-gap="12">
          <!-- 诊断模型 -->
          <NGridItem :span="8">
            <div class="flex flex-col gap-1">
              <label class="text-xs font-bold text-slate-600 dark:text-slate-300">诊断 AI 大模型</label>
              <NSelect
                v-model:value="selectedDiagnosticMappingId"
                :options="diagnosticModelOptions"
                filterable
                placeholder="选择用于诊断与微调的模型"
                size="small"
              />
            </div>
          </NGridItem>

          <!-- 目标测试站点 -->
          <NGridItem :span="8">
            <div class="flex flex-col gap-1">
              <label class="text-xs font-bold text-slate-600 dark:text-slate-300">目标测试上游站点</label>
              <NSelect
                v-model:value="selectedTargetSiteId"
                :options="targetSiteOptions"
                filterable
                placeholder="选择用于实机调用的上游站点"
                size="small"
              />
            </div>
          </NGridItem>

          <!-- 目标模型名称 -->
          <NGridItem :span="8">
            <div class="flex flex-col gap-1">
              <label class="text-xs font-bold text-slate-600 dark:text-slate-300">上游实际模型名称</label>
              <NInput
                v-model:value="targetModelName"
                placeholder="例如: gemini-3.7-flash"
                size="small"
              />
            </div>
          </NGridItem>

          <!-- 协议方向与轮数 -->
          <NGridItem :span="6">
            <div class="flex flex-col gap-1">
              <label class="text-xs font-bold text-slate-600 dark:text-slate-300">客户端源协议</label>
              <NSelect v-model:value="sourceProtocol" :options="protocolOptions" size="small" />
            </div>
          </NGridItem>

          <NGridItem :span="6">
            <div class="flex flex-col gap-1">
              <label class="text-xs font-bold text-slate-600 dark:text-slate-300">上游目标协议</label>
              <NSelect v-model:value="targetProtocol" :options="protocolOptions" size="small" />
            </div>
          </NGridItem>

          <NGridItem :span="6">
            <div class="flex flex-col gap-1">
              <label class="text-xs font-bold text-slate-600 dark:text-slate-300">最大试错轮数 (1~5)</label>
              <NInputNumber v-model:value="maxRounds" :min="1" :max="5" size="small" />
            </div>
          </NGridItem>

          <NGridItem :span="6">
            <div class="flex items-center gap-3 pt-5">
              <div class="flex items-center gap-2">
                <span class="text-xs text-slate-500">思维链推理:</span>
                <NSwitch v-model:value="enableReasoning" size="small" />
              </div>
            </div>
          </NGridItem>
        </NGrid>

        <!-- 请求体与报错输入 -->
        <NDivider style="margin: 14px 0 10px 0" />

        <NGrid :cols="24" :x-gap="12" :y-gap="8">
          <NGridItem :span="12">
            <div class="flex items-center justify-between mb-1">
              <span class="text-xs font-bold text-slate-700 dark:text-slate-200">原始客户端请求正文 (Original Body)</span>
              <NButton size="tiny" tertiary @click="copyText(originalRequestBody)">复制</NButton>
            </div>
            <NInput
              v-model:value="originalRequestBody"
              type="textarea"
              placeholder="输入或粘贴原始 JSON 请求体..."
              :autosize="{ minRows: 6, maxRows: 12 }"
              style="font-family: monospace; font-size: 12px;"
            />
          </NGridItem>

          <NGridItem :span="12">
            <div class="flex items-center justify-between mb-1">
              <span class="text-xs font-bold text-slate-700 dark:text-slate-200">上游报错原文 (Error Body & Code)</span>
              <div class="flex items-center gap-2">
                <span class="text-xs text-slate-400">状态码:</span>
                <NInputNumber v-model:value="initialStatusCode" size="tiny" style="width: 80px;" />
              </div>
            </div>
            <NInput
              v-model:value="initialErrorResponse"
              type="textarea"
              placeholder="输入或粘贴上游返回的真实报错信息 (HTTP 400/422/500)..."
              :autosize="{ minRows: 6, maxRows: 12 }"
              style="font-family: monospace; font-size: 12px;"
            />
          </NGridItem>
        </NGrid>

        <!-- 启动调试按钮 -->
        <div class="mt-4 flex justify-end">
          <NButton
            type="primary"
            size="medium"
            :loading="loopLoading"
            @click="handleStartAutoDiagnoseLoop"
          >
            🚀 启动 AI 自动试错与自愈调试
          </NButton>
        </div>
      </NCard>

      <!-- 执行过程与自愈成果展示 -->
      <div v-if="loopLoading" class="flex flex-col items-center justify-center p-12 bg-white dark:bg-slate-900 rounded-xl border border-slate-200 dark:border-slate-800 gap-3">
        <NSpin size="large" />
        <span class="text-sm font-semibold text-blue-600 dark:text-blue-400 animate-pulse">
          🤖 AI 正在分析报错、微调 Payload 并向上游发起实机测试... 请稍候
        </span>
      </div>

      <div v-else-if="loopResult" ref="resultSectionRef" class="flex flex-col gap-4">
        <!-- 启动或致命错误卡片 -->
        <NAlert
          v-if="loopResult.error"
          type="error"
          title="❌ AI 诊断执行失败"
          :bordered="false"
          class="rounded-xl shadow-sm"
        >
          <div class="text-xs font-semibold leading-relaxed">{{ loopResult.error }}</div>
        </NAlert>

        <!-- 结果大状态卡片 -->
        <div
          v-if="!loopResult.error || loopResult.summary"
          :class="[
            'p-4 rounded-xl border flex flex-col gap-2 shadow-sm',
            loopResult.success
              ? 'border-emerald-300 bg-emerald-50/70 dark:bg-emerald-950/30 dark:border-emerald-800'
              : 'border-amber-300 bg-amber-50/70 dark:bg-amber-950/30 dark:border-amber-800'
          ]"
        >
          <div class="flex items-center justify-between">
            <div class="flex items-center gap-2">
              <NTag :type="loopResult.success ? 'success' : 'warning'" size="medium" round>
                {{ loopResult.success ? '✅ 自愈成功 (200 OK)' : '⚠️ 多轮测试未完全收敛' }}
              </NTag>
              <span class="font-bold text-base text-slate-800 dark:text-slate-100">
                {{ loopResult.summary || (loopResult.success ? '实机测试通过' : '自愈试探未通过') }}
              </span>
            </div>
            <NButton
              v-if="loopResult.rules && loopResult.rules.length > 0"
              type="primary"
              size="small"
              @click="openSaveRulesDialog(loopResult.rules)"
            >
              ⚡ 一键应用为兼容规则 ({{ loopResult.rules.length }} 条)
            </NButton>
          </div>

          <div v-if="loopResult.rootCause" class="text-xs text-slate-700 dark:text-slate-300 leading-relaxed bg-white/80 dark:bg-slate-900/80 p-3 rounded-lg border border-slate-200/80 dark:border-slate-800">
            <strong>根因归因：</strong>{{ loopResult.rootCause }}
          </div>

          <div v-if="loopResult.suggestedAction" class="text-xs text-blue-800 dark:text-blue-200 leading-relaxed bg-blue-50/80 dark:bg-blue-950/40 p-3 rounded-lg border border-blue-200/80 dark:border-blue-900">
            <strong>💡 AI 建议措施：</strong>{{ loopResult.suggestedAction }}
          </div>
        </div>

        <!-- 详细多轮执行时间线 -->
        <NCard title="实机测试与微调迭代全记录 (Timeline)" size="small">
          <NTimeline>
            <NTimelineItem
              v-for="round in loopResult.rounds"
              :key="round.roundNumber"
              :type="round.success ? 'success' : 'error'"
              :title="`第 ${round.roundNumber} 轮试探 [${round.success ? 'HTTP 200 成功' : 'HTTP ' + round.statusCode + ' 失败'}] - 耗时 ${round.durationMs}ms`"
            >
              <div class="flex flex-col gap-2 p-3 bg-slate-50 dark:bg-slate-900/60 rounded-lg border border-slate-200 dark:border-slate-800 text-xs mb-2">
                <div><strong class="text-blue-600 dark:text-blue-400">💡 AI 假设：</strong>{{ round.hypothesis }}</div>
                <div><strong class="text-amber-600 dark:text-amber-400">🛠️ 本轮调整：</strong>{{ round.explanation }}</div>
                <div v-if="!round.success && round.errorMessage" class="text-red-600 dark:text-red-400 font-mono">
                  <strong>上游报错：</strong>{{ round.errorMessage }}
                </div>

                <NCollapse>
                  <NCollapseItem title="查看本轮发往上游的 Payload" name="1">
                    <pre class="p-2 bg-slate-950 text-slate-200 rounded font-mono text-[11px] overflow-auto max-h-48">{{ round.adjustedRequestBody }}</pre>
                  </NCollapseItem>
                </NCollapse>
              </div>
            </NTimelineItem>
          </NTimeline>
        </NCard>

        <!-- 修复前后 Diff 对比视图 (若成功) -->
        <NCard v-if="loopResult.success && loopResult.workingPayload" title="修复前后请求正文 (JSON Diff 对比)" size="small">
          <div class="text-xs text-slate-500 mb-2">
            左侧：原始失败请求 (Before) ➔ 右侧：验证通过有效请求 (After)
          </div>
          <JsonDiffView
            :before="originalRequestBody"
            :after="loopResult.workingPayload"
          />
        </NCard>
      </div>
    </div>

    <!-- 抓包载入弹窗 -->
    <NModal v-model:show="showLoadDumpModal" preset="card" title="从最近抓包载入故障现场" style="max-width: 800px;">
      <div v-if="loadingDumps" class="flex justify-center p-8">
        <NSpin />
      </div>
      <div v-else-if="recentDumps.length === 0" class="p-8 text-center text-slate-400">
        暂无抓包记录
      </div>
      <div v-else class="flex flex-col gap-2 max-h-96 overflow-y-auto">
        <div
          v-for="dump in recentDumps"
          :key="dump.fileName"
          class="p-3 rounded-lg border border-slate-200 dark:border-slate-800 hover:bg-blue-50/50 dark:hover:bg-slate-800/60 cursor-pointer flex items-center justify-between gap-2 text-xs transition"
          @click="applyDumpToDiagnostic(dump)"
        >
          <div class="flex flex-col gap-1">
            <div class="flex items-center gap-2">
              <NTag :type="dump.category === 'failure' ? 'error' : 'success'" size="tiny">
                {{ dump.category === 'failure' ? '失败抓包' : '成功样本' }}
              </NTag>
              <span class="font-bold text-slate-800 dark:text-slate-100">{{ dump.routeName || dump.requestModel }}</span>
              <span class="text-slate-400">➔ {{ dump.attemptedModel }} ({{ dump.siteName }})</span>
            </div>
            <div class="text-[11px] text-slate-400 font-mono truncate max-w-lg">
              {{ dump.errorSummary || 'HTTP ' + dump.statusCode }}
            </div>
          </div>
          <NButton size="tiny" type="primary" ghost>载入现场</NButton>
        </div>
      </div>
    </NModal>

    <!-- 一键应用规则弹窗 -->
    <NModal v-model:show="saveDialogVisible" preset="card" title="保存/应用推荐兼容规则" style="max-width: 600px;">
      <div class="flex flex-col gap-4 text-xs">
        <NRadioGroup v-model:value="saveTargetMode">
          <NRadioButton value="create">新建兼容规则集</NRadioButton>
          <NRadioButton value="existing">追加到已有规则集</NRadioButton>
        </NRadioGroup>

        <div v-if="saveTargetMode === 'create'" class="flex flex-col gap-1">
          <label class="font-bold text-slate-700 dark:text-slate-300">新规则集名称</label>
          <NInput v-model:value="newProfileName" placeholder="例如: 自愈修复规则集" size="small" />
        </div>

        <div v-else class="flex flex-col gap-1">
          <label class="font-bold text-slate-700 dark:text-slate-300">选择已有规则集</label>
          <NSelect
            v-model:value="existingProfileId"
            :options="profiles.map(p => ({ label: p.name, value: p.id }))"
            placeholder="请选择"
            size="small"
          />
        </div>

        <div>
          <label class="font-bold text-slate-700 dark:text-slate-300 block mb-1">本次将应用的规则 ({{ draftRules.length }} 条)</label>
          <pre class="p-2 bg-slate-100 dark:bg-slate-900 rounded font-mono text-[11px] overflow-auto max-h-40">{{ JSON.stringify(draftRules, null, 2) }}</pre>
        </div>

        <div class="flex justify-end gap-2 mt-2">
          <NButton @click="saveDialogVisible = false">取消</NButton>
          <NButton type="primary" :loading="saveLoading" @click="saveRules">保存并生效</NButton>
        </div>
      </div>
    </NModal>

    <!-- 动态限制设置弹窗 -->
    <NModal
      v-model:show="showConfigModal"
      preset="card"
      title="⚙️ 诊断抓包与自愈限制参数设置"
      style="width: 540px; max-width: 95vw;"
    >
      <NAlert type="info" :bordered="false" class="mb-4 text-xs">
        在此可随时临时放宽正文捕获上限与保留天数，设置保存后<strong>立即生效</strong>，无需重启服务。
      </NAlert>

      <NForm label-placement="left" label-width="180" size="small">
        <NFormItem label="抓包正文捕获上限 (MB)">
          <div class="flex items-center gap-2 w-full">
            <NInputNumber v-model:value="configForm.maxBodyLengthMb" :min="1" :max="50" class="w-32" />
            <span class="text-xs text-slate-400">MB (支持 1 ~ 50MB)</span>
          </div>
        </NFormItem>

        <NFormItem label="AI自愈试探响应上限 (MB)">
          <div class="flex items-center gap-2 w-full">
            <NInputNumber v-model:value="configForm.maxRoundResponseMb" :min="1" :max="20" class="w-32" />
            <span class="text-xs text-slate-400">MB (支持 1 ~ 20MB)</span>
          </div>
        </NFormItem>

        <NFormItem label="历史抓包保留天数 (天)">
          <div class="flex items-center gap-2 w-full">
            <NInputNumber v-model:value="configForm.retentionDays" :min="1" :max="30" class="w-32" />
            <span class="text-xs text-slate-400">天 (支持 1 ~ 30天)</span>
          </div>
        </NFormItem>

        <NFormItem label="单日单目录失败抓包上限">
          <div class="flex items-center gap-2 w-full">
            <NInputNumber v-model:value="configForm.maxFailuresPerDay" :min="10" :max="500" class="w-32" />
            <span class="text-xs text-slate-400">个 (支持 10 ~ 500个)</span>
          </div>
        </NFormItem>
      </NForm>

      <template #footer>
        <div class="flex justify-end gap-2">
          <NButton secondary @click="showConfigModal = false">取消</NButton>
          <NButton type="primary" :loading="configSaving" @click="handleSaveConfig">保存并即时生效</NButton>
        </div>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.protocol-diagnostics-tab {
  min-width: 0;
}
</style>
