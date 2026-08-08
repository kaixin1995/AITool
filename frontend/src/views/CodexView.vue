<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NAlert, NCard, NButton, NSpace, NTag, NEmpty, NSpin, NModal, NInput, NPopconfirm, NProgress, NCheckbox, NTabs, NTabPane, useMessage } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/codex'
import type {
  CodexAccount,
  CodexCredentialImportFailure,
  CodexInspectionLog,
  CodexInspectionRunResult,
  CodexInspectionStatus,
  CodexModelSelection,
  CodexRemoteModelItem,
  CodexResetCreditsInfo
} from '@/api/codex'
import {
  inspectionActionLabel,
  isInspectionDisabledError
} from './codexInspectionState'

const message = useMessage()
const route = useRoute()
const router = useRouter()
const activeTab = ref(route.query.tab === 'inspection' ? 'inspection' : 'accounts')
const loading = ref(false)
const accounts = ref<CodexAccount[]>([])
const inspection = ref<CodexInspectionStatus | null>(null)
const inspectionLastRun = ref<CodexInspectionRunResult | null>(null)
const inspectionLogs = ref<CodexInspectionLog[]>([])
const inspectionRunning = ref(false)
const inspectionDisabled = ref(false)
const inspectionStatusError = ref('')
const inspectionLastRunError = ref('')
const inspectionLogsError = ref('')
// 功能未开启时的提示态
const featureDisabled = ref(false)

// OAuth 弹窗
const oauthModal = ref(false)
const oauthUrl = ref('')
const oauthCallbackInput = ref('')
const oauthDisplayName = ref('')
const oauthLoading = ref(false)
const oauthStartLoading = ref(false)

// 凭证导入弹窗
const importModal = ref(false)
const importJsonText = ref('')
const importFiles = ref<File[]>([])
const importLoading = ref(false)
const importFailures = ref<CodexCredentialImportFailure[]>([])

const exportMode = ref(false)
const selectedExportAccountIds = ref<string[]>([])
const exportLoading = ref(false)

// 编辑账号（重命名 + 修改凭证）弹窗
const editModal = ref(false)
const editAccount = ref<CodexAccount | null>(null)
const editDisplayName = ref('')
const editRefreshToken = ref('')
const editLoading = ref(false)
const editTokenRefreshing = ref(false)

// 重置额度信用弹窗
const resetCreditModal = ref(false)
const resetCreditAccount = ref<CodexAccount | null>(null)
const resetCreditInfo = ref<CodexResetCreditsInfo | null>(null)
const resetCreditLoading = ref(false)
const resetCreditSubmitting = ref(false)

// 拉取/导入模型弹窗
const modelModal = ref(false)
const modelAccount = ref<CodexAccount | null>(null)
type EditableCodexModel = CodexRemoteModelItem & { alias: string }

const modelList = ref<EditableCodexModel[]>([])
const checkedModels = ref<string[]>([])
const modelSearch = ref('')
const modelLoading = ref(false)

const filteredModelList = computed(() => {
  const keyword = modelSearch.value.trim().toLowerCase()
  if (!keyword) return modelList.value
  return modelList.value.filter((model) => (
    `${model.remoteModelName} ${model.displayName} ${model.alias}`
      .toLowerCase()
      .includes(keyword)
  ))
})
const visibleCheckedModelCount = computed(() => (
  filteredModelList.value.filter(model => (
    checkedModels.value.includes(model.remoteModelName)
  )).length
))
const allVisibleModelsChecked = computed(() => (
  filteredModelList.value.length > 0
  && visibleCheckedModelCount.value === filteredModelList.value.length
))
const someVisibleModelsChecked = computed(() => (
  visibleCheckedModelCount.value > 0
  && !allVisibleModelsChecked.value
))

let pollTimer: ReturnType<typeof setInterval> | null = null
let accountsRequestId = 0
let inspectionRequestId = 0

// 使巡检前已经发出的旧查询失效，避免响应晚到时覆盖巡检后的新数据。
function invalidatePendingRefreshes(): void {
  accountsRequestId++
  inspectionRequestId++
}

async function loadAccounts(showError: boolean): Promise<void> {
  const requestId = ++accountsRequestId
  try {
    const result = await api.listCodexAccounts()
    if (requestId === accountsRequestId) {
      accounts.value = result
      featureDisabled.value = false
    }
  } catch (e) {
    if (requestId !== accountsRequestId) return
    // Codex 功能未开启时后端返回 404，显示提示而非空白。
    if ((e as { status?: number }).status === 404) {
      featureDisabled.value = true
    } else if (showError) {
      message.error((e as Error).message)
    }
  }
}

async function loadInspection(force = false): Promise<void> {
  if (inspectionDisabled.value && !force) return
  // 巡检运行期间不读取中间状态，完成后由 handleRunInspection 统一刷新。
  if (inspectionRunning.value) return

  const requestId = ++inspectionRequestId
  inspectionStatusError.value = ''
  inspectionLastRunError.value = ''
  inspectionLogsError.value = ''
  const [statusResult, lastRunResult, logsResult] = await Promise.allSettled([
    api.getCodexInspectionStatus(),
    api.getCodexInspectionLastRun(),
    api.getCodexInspectionLogs()
  ])

  if (requestId !== inspectionRequestId) return

  if (statusResult.status === 'fulfilled') {
    inspection.value = statusResult.value
    inspectionDisabled.value = false
  } else if (isInspectionDisabledError(statusResult.reason)) {
    inspection.value = null
    inspectionDisabled.value = true
  } else {
    inspectionStatusError.value = '巡检状态加载失败，请稍后重试。'
  }

  if (inspectionDisabled.value) return

  if (lastRunResult.status === 'fulfilled') {
    inspectionLastRun.value = lastRunResult.value
  } else {
    inspectionLastRunError.value = '上次巡检结果加载失败，当前保留上次成功数据。'
  }

  if (logsResult.status === 'fulfilled') {
    inspectionLogs.value = logsResult.value
  } else {
    inspectionLogsError.value = '巡检日志加载失败，当前保留上次成功数据。'
  }
}

async function load(): Promise<void> {
  loading.value = true
  featureDisabled.value = false
  await Promise.all([loadAccounts(true), loadInspection(true)])
  loading.value = false
}

let silentRefreshInFlight: Promise<void> | null = null

async function refreshSilently(force = false): Promise<void> {
  const inFlight = silentRefreshInFlight
  if (inFlight) {
    await inFlight
    // 巡检完成后的强制刷新必须在旧请求结束后重新发起一次。
    if (!force || (silentRefreshInFlight && silentRefreshInFlight !== inFlight)) return
  }

  const refresh: Promise<void> = Promise.all([loadAccounts(false), loadInspection()]).then(() => undefined)
  silentRefreshInFlight = refresh
  try {
    await refresh
  } finally {
    if (silentRefreshInFlight === refresh) silentRefreshInFlight = null
  }
}

function openOAuthModal(): void {
  oauthUrl.value = ''
  oauthCallbackInput.value = ''
  oauthDisplayName.value = ''
  oauthModal.value = true
}

async function handleStartOAuth(): Promise<void> {
  oauthStartLoading.value = true
  try {
    const result = await api.startCodexOAuth()
    oauthUrl.value = result.url
    oauthCallbackInput.value = ''
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    oauthStartLoading.value = false
  }
}

async function handleCompleteOAuth(): Promise<void> {
  if (!oauthCallbackInput.value.trim()) { message.warning('请粘贴回调 URL'); return }
  oauthLoading.value = true
  try {
    await api.completeCodexOAuth(oauthCallbackInput.value.trim(), oauthDisplayName.value.trim() || undefined)
    message.success('OAuth 登录成功')
    oauthModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { oauthLoading.value = false }
}

async function handleToggle(acc: CodexAccount): Promise<void> {
  try {
    await api.toggleCodexAccount(acc.id)
    acc.isEnabled = !acc.isEnabled
  } catch (e) { message.error((e as Error).message) }
}
async function handleRefreshQuota(acc: CodexAccount): Promise<void> {
  try {
    await api.refreshCodexQuota(acc.id)
    message.success('已刷新额度')
    await load()
  } catch (e) { message.error((e as Error).message) }
}
async function handleDelete(acc: CodexAccount): Promise<void> {
  await api.deleteCodexAccount(acc.id)
  message.success('已删除账号')
  await load()
}
async function handleRunInspection(force: boolean): Promise<void> {
  if (inspectionRunning.value) return
  inspectionRunning.value = true
  // 真实巡检开始前作废已发出的轮询请求，防止旧账号列表/巡检详情回写。
  invalidatePendingRefreshes()
  let succeeded = false
  try {
    inspectionLastRun.value = await api.runCodexInspection(force)
    succeeded = true
    message.success(force ? '真实巡检已完成' : '手动巡检已完成')
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    inspectionRunning.value = false
  }
  // 先结束运行态，再读取账号、状态和日志，确保巡检后的最新数据完整回显。
  if (succeeded) await refreshSilently(true)
}

// 编辑（重命名 + 修改凭证）
function openEdit(acc: CodexAccount): void {
  editAccount.value = acc
  editDisplayName.value = acc.displayName
  editRefreshToken.value = ''
  editModal.value = true
}
async function handleSaveEdit(): Promise<void> {
  if (!editAccount.value || !editDisplayName.value.trim()) { message.warning('名称不能为空'); return }
  editLoading.value = true
  try {
    const result = await api.updateCodexAccount(
      editAccount.value.id,
      editDisplayName.value.trim(),
      editRefreshToken.value || undefined
    )
    if (result.message) {
      message.warning(result.message)
    } else if (editRefreshToken.value) {
      message.success('凭证已更新并刷新')
    } else {
      message.success('已更新')
    }
    editModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { editLoading.value = false }
}

// 手动刷新 access_token（兜底手段：自动刷新未生效时用此恢复）
async function handleManualRefreshToken(): Promise<void> {
  if (!editAccount.value) return
  editTokenRefreshing.value = true
  try {
    await api.refreshCodexToken(editAccount.value.id)
    message.success('Token 已刷新')
    // 刷新后更新当前编辑的账号对象和列表，让用户看到新的过期时间
    await load()
    if (editAccount.value) {
      const updated = accounts.value.find(a => a.id === editAccount.value!.id)
      if (updated) editAccount.value = updated
    }
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    editTokenRefreshing.value = false
  }
}

// 重置额度信用
async function openResetCredit(acc: CodexAccount): Promise<void> {
  resetCreditAccount.value = acc
  resetCreditInfo.value = null
  resetCreditModal.value = true
  resetCreditLoading.value = true
  try {
    const info = await api.getResetCredits(acc.id)
    resetCreditInfo.value = info
    if (!info.success) {
      message.error(info.error || '重置信用加载失败')
    }
  } catch (e) { message.error((e as Error).message) } finally { resetCreditLoading.value = false }
}
async function handleConsumeResetCredit(): Promise<void> {
  if (!resetCreditAccount.value || resetCreditSubmitting.value) return
  resetCreditSubmitting.value = true
  try {
    await api.consumeResetCredit(resetCreditAccount.value.id)
    message.success('手动重置额度成功')
    resetCreditModal.value = false
    await load()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    resetCreditSubmitting.value = false
  }
}

// 拉取/导入模型
async function openFetchModels(acc: CodexAccount): Promise<void> {
  modelAccount.value = acc
  modelList.value = []
  checkedModels.value = []
  modelModal.value = true
  modelLoading.value = true
  try {
    modelSearch.value = ''
    const models = await api.fetchCodexModels(acc.id)
    modelList.value = models.map(model => ({
      ...model,
      alias: model.existingDisplayName
        || model.displayName
        || model.remoteModelName
    }))
    checkedModels.value = models
      .filter(model => (
        model.existingMappingId
          ? model.isEnabled
          : true
      ))
      .map(model => model.remoteModelName)
  } catch (e) { message.error((e as Error).message) } finally { modelLoading.value = false }
}

function toggleVisibleModels(checked: boolean): void {
  const visibleNames = filteredModelList.value.map(
    model => model.remoteModelName
  )
  if (checked) {
    checkedModels.value = Array.from(new Set([
      ...checkedModels.value,
      ...visibleNames
    ]))
  } else {
    checkedModels.value = checkedModels.value.filter(
      name => !visibleNames.includes(name)
    )
  }
}
async function handleImportModels(): Promise<void> {
  if (!modelAccount.value || checkedModels.value.length === 0) { message.warning('请选择要导入的模型'); return }
  modelLoading.value = true
  try {
    const selections: CodexModelSelection[] = modelList.value.map(model => ({
      remoteModelName: model.remoteModelName,
      displayName: model.alias.trim() || model.remoteModelName,
      selected: checkedModels.value.includes(model.remoteModelName)
    }))
    await api.importSelectedCodexModels(modelAccount.value.id, selections)
    message.success(`已导入 ${checkedModels.value.length} 个模型`)
    modelModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { modelLoading.value = false }
}

function openImportCredential(): void {
  importJsonText.value = ''
  importFiles.value = []
  importFailures.value = []
  importModal.value = true
}

function closeImportCredential(): void {
  importJsonText.value = ''
  importFiles.value = []
  importFailures.value = []
  importModal.value = false
}

function handleCredentialFiles(event: Event): void {
  const input = event.target as HTMLInputElement
  importFiles.value = Array.from(input.files ?? [])
}

async function handleImportCredential(): Promise<void> {
  if (importFiles.value.length === 0 && !importJsonText.value.trim()) {
    message.warning('请选择凭证文件或粘贴凭证 JSON')
    return
  }
  importLoading.value = true
  importFailures.value = []
  try {
    const result = importFiles.value.length > 0
      ? await api.importCredentialFiles(importFiles.value)
      : await api.importCredential(importJsonText.value.trim())
    importJsonText.value = ''
    importFiles.value = []
    importFailures.value = result.failures

    if (result.failures.length > 0) {
      if (result.successes.length > 0) {
        message.warning(`成功导入 ${result.successes.length} 个，失败 ${result.failures.length} 个`)
        await load()
      } else {
        message.error('凭证导入失败')
      }
      return
    }

    message.success(`成功导入 ${result.successes.length} 个凭证`)
    importModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { importLoading.value = false }
}

function beginExportCredentials(): void {
  selectedExportAccountIds.value = []
  exportMode.value = true
}

function cancelExportCredentials(): void {
  selectedExportAccountIds.value = []
  exportMode.value = false
}

function toggleExportAccount(id: string, checked: boolean): void {
  selectedExportAccountIds.value = checked
    ? Array.from(new Set([...selectedExportAccountIds.value, id]))
    : selectedExportAccountIds.value.filter(accountId => accountId !== id)
}

function downloadCredential(
  credential: api.CodexExportCredential,
  index: number
): void {
  const identity = String(
    credential.email
    || credential.account_id
    || index + 1
  ).replace(/[^a-zA-Z0-9._-]+/g, '_')
  const blob = new Blob(
    [JSON.stringify(credential, null, 2)],
    { type: 'application/json' }
  )
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `codex_credential_${identity}.json`
  link.click()
  URL.revokeObjectURL(url)
}

async function handleExportCredentials(): Promise<void> {
  if (selectedExportAccountIds.value.length === 0) {
    message.warning('请选择要导出的账号')
    return
  }
  exportLoading.value = true
  try {
    const result = await api.exportCredentials(
      selectedExportAccountIds.value
    )
    result.credentials.forEach(downloadCredential)
    message.success(`已导出 ${result.credentials.length} 个凭证文件`)
    cancelExportCredentials()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    exportLoading.value = false
  }
}

async function copyText(text: string): Promise<void> {
  if (!text) return
  if (window.isSecureContext && navigator.clipboard) {
    try {
      await navigator.clipboard.writeText(text)
      message.success('已复制到剪贴板')
      return
    } catch {
      // HTTP 或权限受限时使用传统复制方式。
    }
  }
  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.style.position = 'fixed'
  textarea.style.left = '-9999px'
  document.body.appendChild(textarea)
  textarea.select()
  try {
    document.execCommand('copy')
    message.success('已复制到剪贴板')
  } catch {
    message.error('复制失败，请手动复制')
  } finally {
    document.body.removeChild(textarea)
  }
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '从未'
  return new Date(value).toLocaleString('zh-CN')
}

function isTokenExpired(expiresAt: string | null | undefined): boolean {
  if (!expiresAt) return false
  return new Date(expiresAt).getTime() <= Date.now()
}

function isTokenExpiringSoon(expiresAt: string | null | undefined): boolean {
  if (!expiresAt) return false
  const expires = new Date(expiresAt).getTime()
  const now = Date.now()
  // 低于 3 天视为即将过期，卡片上标红提醒
  return expires > now && expires <= now + 3 * 24 * 60 * 60 * 1000
}

function formatBeijingTime(value: string | null | undefined): string {
  if (!value) return '—'
  return new Date(value).toLocaleString('zh-CN', {
    timeZone: 'Asia/Shanghai',
    hour12: false
  })
}

function formatQuotaPercent(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(Number(value))) return '-'
  return `${Math.round(Number(value))}%`
}

function formatInspectionPercent(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(Number(value))) return '-'
  return `${Number(value).toFixed(1)}%`
}

function accountQuotaPercent(acc: CodexAccount): number | null {
  if (acc.windows && acc.windows.length > 0) {
    return Math.min(...acc.windows.map((w) => Math.max(0, 100 - Number(w.usedPercent || 0))))
  }
  const percents = [acc.fiveHourUsedPercent, acc.weeklyUsedPercent]
    .filter((value): value is number => value != null && Number.isFinite(Number(value)))
  return percents.length ? Math.min(...percents.map((value) => Math.max(0, 100 - value))) : null
}

function accountStatusLabel(acc: CodexAccount): string {
  if (acc.isQuotaCooling) return '冷却中'
  return acc.isEnabled ? '正常' : '已禁用'
}

function accountStatusType(acc: CodexAccount): 'success' | 'warning' | 'default' {
  if (acc.isQuotaCooling) return 'warning'
  return acc.isEnabled ? 'success' : 'default'
}

// 额度进度条颜色
function quotaColor(percent: number | null | undefined): 'success' | 'warning' | 'error' {
  if (percent == null) return 'success'
  if (percent < 20) return 'error'
  if (percent < 50) return 'warning'
  return 'success'
}

watch(activeTab, (tab) => {
  const query = { ...route.query }
  if (tab === 'inspection') {
    query.tab = 'inspection'
    if (exportMode.value) cancelExportCredentials()
    void loadInspection(true)
  } else {
    delete query.tab
  }
  void router.replace({ query })
})

watch(() => route.query.tab, (tab) => {
  activeTab.value = tab === 'inspection' ? 'inspection' : 'accounts'
})

onMounted(() => {
  load()
  pollTimer = setInterval(() => {
    if (document.visibilityState === 'visible') {
      void refreshSilently()
    }
  }, 5000)
})
onUnmounted(() => { if (pollTimer) clearInterval(pollTimer) })
</script>

<template>
  <div class="page-container">
    <PageHeader title="OAuth 管理" subtitle="管理 Codex OAuth 登录账号、凭证导入、额度、巡检与自动禁用">
      <template #actions>
        <template v-if="!exportMode">
          <NButton secondary :disabled="accounts.length === 0" @click="beginExportCredentials">导出凭证</NButton>
          <NButton type="primary" @click="openOAuthModal">＋ OAuth 登录</NButton>
          <NButton secondary type="primary" @click="openImportCredential">上传凭证</NButton>
        </template>
      </template>
    </PageHeader>
    <NSpin :show="loading">
      <NTabs v-model:value="activeTab" type="line" animated>
        <NTabPane name="accounts" tab="账号额度">
          <div v-if="exportMode" class="codex-export-toolbar">
            <strong>已选中 {{ selectedExportAccountIds.length }} 个账号</strong>
            <NButton size="small" type="warning" :loading="exportLoading" :disabled="selectedExportAccountIds.length === 0" @click="handleExportCredentials">下载凭证 JSON</NButton>
            <NButton size="small" secondary @click="cancelExportCredentials">取消</NButton>
          </div>
          <div class="codex-stack">
            <NEmpty v-if="featureDisabled" description="Codex 功能未开启，请在系统设置中开启" />
            <NEmpty v-else-if="accounts.length === 0" description="暂无 Codex 账号，可使用右上角 OAuth 登录或导入凭证" />

            <div v-else class="codex-grid" :class="{ 'export-mode': exportMode }">
              <article
                v-for="acc in accounts"
                :key="acc.id"
                class="codex-card"
                :class="{ disabled: !acc.isEnabled, selected: selectedExportAccountIds.includes(acc.id) }"
                @click="exportMode && toggleExportAccount(acc.id, !selectedExportAccountIds.includes(acc.id))"
              >
                <NCheckbox
                  v-if="exportMode"
                  class="codex-export-checkbox"
                  :checked="selectedExportAccountIds.includes(acc.id)"
                  @click.stop
                  @update:checked="(checked: boolean) => toggleExportAccount(acc.id, checked)"
                />
                <div class="codex-card-header">
                  <div class="codex-card-header-main">
                    <div class="codex-account-name">{{ acc.displayName }}</div>
                    <div class="codex-account-email">
                      {{ acc.email || '' }}
                      <button
                        v-if="(acc.resetCreditsAvailableCount ?? 0) > 0"
                        type="button"
                        class="codex-reset-credits-hint"
                        title="点击查看详情"
                        @click.stop="openResetCredit(acc)"
                      >
                        剩余 {{ acc.resetCreditsAvailableCount }} 次手动重置
                      </button>
                    </div>
                    <div class="codex-badges">
                      <NTag size="small" :type="accountStatusType(acc)" :bordered="false">{{ accountStatusLabel(acc) }}</NTag>
                      <span v-if="acc.planType" class="codex-plan">{{ acc.planType }}</span>
                      <span v-if="acc.tokenExpiresAt" class="codex-token-expiry" :class="{ 'codex-token-expired': isTokenExpired(acc.tokenExpiresAt), 'codex-token-warning': isTokenExpiringSoon(acc.tokenExpiresAt) }">
                        Token：{{ formatDateTime(acc.tokenExpiresAt) }}
                      </span>
                    </div>
                  </div>
                </div>

                <div v-if="acc.windows && acc.windows.length > 0" class="codex-windows-container">
                  <div v-for="w in acc.windows" :key="w.id" class="codex-window">
                    <div class="codex-window-label">{{ w.label }}</div>
                    <NProgress
                      :percentage="Math.max(0, 100 - Math.round(w.usedPercent))"
                      :status="quotaColor(100 - w.usedPercent)"
                      :show-indicator="false"
                      :height="6"
                      :border-radius="3"
                    />
                    <span class="codex-window-percent">{{ Math.max(0, 100 - Math.round(w.usedPercent)) }}%</span>
                    <div v-if="w.resetLabel" class="codex-window-reset">重置于 {{ w.resetLabel }}</div>
                  </div>
                </div>
                <div v-else class="codex-window-placeholder">
                  {{ acc.lastQuotaCheckedAt ? '暂无额度窗口数据' : '未刷新额度，点击下方「刷新额度」获取' }}
                </div>

                <div v-if="!exportMode" class="codex-card-meta">
                  <div class="codex-source-meta">
                    <div v-if="acc.lastQuotaCheckedAt">刷新时间：{{ formatDateTime(acc.lastQuotaCheckedAt) }}</div>
                  </div>
                  <div class="codex-card-actions">
                    <NButton class="codex-icon-button" circle secondary title="刷新额度" aria-label="刷新额度" @click="handleRefreshQuota(acc)">
                      <svg viewBox="0 0 24 24" aria-hidden="true"><polyline points="23 4 23 10 17 10" /><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" /></svg>
                    </NButton>
                    <NButton class="codex-icon-button" circle secondary :title="acc.isEnabled ? '禁用账号' : '启用账号'" :aria-label="acc.isEnabled ? '禁用账号' : '启用账号'" @click="handleToggle(acc)">
                      <svg v-if="acc.isEnabled" viewBox="0 0 24 24" aria-hidden="true"><line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" /></svg>
                      <svg v-else viewBox="0 0 24 24" aria-hidden="true"><polyline points="20 6 9 17 4 12" /></svg>
                    </NButton>
                    <NButton class="codex-icon-button primary" circle secondary title="编辑账号" aria-label="编辑账号" @click="openEdit(acc)">
                      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" /><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" /></svg>
                    </NButton>
                    <NButton class="codex-icon-button info" circle secondary title="拉取模型" aria-label="拉取模型" @click="openFetchModels(acc)">
                      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="7 10 12 15 17 10" /><line x1="12" y1="15" x2="12" y2="3" /></svg>
                    </NButton>
                    <NPopconfirm @positive-click="handleDelete(acc)">
                      <template #trigger>
                        <NButton class="codex-icon-button danger" circle secondary title="删除账号" aria-label="删除账号">
                          <svg viewBox="0 0 24 24" aria-hidden="true"><polyline points="3 6 5 6 21 6" /><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" /></svg>
                        </NButton>
                      </template>
                      删除账号「{{ acc.displayName }}」？关联站点和路由会一并清理。
                    </NPopconfirm>
                  </div>
                </div>
              </article>
            </div>
          </div>
        </NTabPane>

        <NTabPane name="inspection" tab="巡检">
          <div v-if="inspectionDisabled" class="inspection-empty-state">
            <NEmpty description="Codex 巡检功能未开启" />
            <NButton size="small" secondary @click="loadInspection(true)">重新检测</NButton>
          </div>
          <div v-else-if="!inspection" class="inspection-empty-state">
            <NEmpty :description="inspectionStatusError || '正在加载巡检状态'" />
            <NButton size="small" secondary @click="loadInspection(true)">重试</NButton>
          </div>
          <div v-else class="inspection-workspace">
            <NAlert v-if="inspectionStatusError" type="error" :show-icon="false">
              巡检状态刷新失败，当前显示上次成功状态。
            </NAlert>
            <NCard class="inspection-card" size="small">
              <div class="inspection-content">
                <div class="inspection-main">
                  <NTag :type="inspection.isRunning || inspectionRunning ? 'warning' : 'success'" :bordered="false">
                    {{ inspection.isRunning || inspectionRunning ? '巡检中' : '空闲' }}
                  </NTag>
                  <div>
                    <div class="inspection-title">Codex 巡检状态</div>
                    <div class="inspection-meta">上次完成：{{ formatDateTime(inspection.lastFinishedAt) }}</div>
                  </div>
                </div>
                <div class="inspection-actions">
                  <span v-if="inspection.nextScheduledAt" class="inspection-meta">下次：{{ formatDateTime(inspection.nextScheduledAt) }}</span>
                  <NButton size="small" :loading="inspectionRunning" @click="handleRunInspection(false)">手动巡检</NButton>
                  <NButton size="small" type="primary" :loading="inspectionRunning" @click="handleRunInspection(true)">真实巡检</NButton>
                  <NButton size="small" secondary :disabled="inspectionRunning" @click="loadInspection()">刷新状态</NButton>
                </div>
              </div>
            </NCard>

            <NAlert v-if="inspectionLastRunError" type="error" :show-icon="false">
              {{ inspectionLastRunError }}
            </NAlert>
            <NCard v-if="inspectionLastRun" title="上次巡检结果" size="small">
              <div class="inspection-summary-grid">
                <div><span>保留</span><strong>{{ inspectionLastRun.keepCount }}</strong></div>
                <div><span>禁用</span><strong>{{ inspectionLastRun.disableCount }}</strong></div>
                <div><span>启用</span><strong>{{ inspectionLastRun.enableCount }}</strong></div>
                <div><span>缓存命中</span><strong>{{ inspectionLastRun.cacheCount }}</strong></div>
                <div><span>真实刷新</span><strong>{{ inspectionLastRun.realRefreshCount }}</strong></div>
              </div>
              <div class="inspection-run-meta">
                {{ inspectionLastRun.autoTriggered ? '自动巡检' : '手动巡检' }} ·
                {{ inspectionLastRun.forcedRefresh ? '强制真实刷新' : '允许使用缓存' }} ·
                完成于 {{ formatDateTime(inspectionLastRun.finishedAt) }}
              </div>
              <div class="inspection-table-scroll">
                <div class="inspection-table">
                  <div class="inspection-table-head">
                    <span>账号</span><span>5 小时</span><span>周额度</span><span>来源</span><span>动作</span><span>原因</span>
                  </div>
                  <div v-for="item in inspectionLastRun.accounts" :key="item.accountId" class="inspection-table-row">
                    <strong>{{ item.displayName }}</strong>
                    <span>{{ formatInspectionPercent(item.fiveHourUsedPercent) }}</span>
                    <span>{{ formatInspectionPercent(item.weeklyUsedPercent) }}</span>
                    <NTag size="tiny" :bordered="false">{{ item.fromCache ? '缓存' : '实时' }}</NTag>
                    <NTag size="tiny" :type="item.action === 'disable' ? 'error' : item.action === 'enable' ? 'success' : 'default'" :bordered="false">{{ inspectionActionLabel(item.action) }}</NTag>
                    <span class="inspection-reason">{{ item.reason }}</span>
                  </div>
                </div>
              </div>
            </NCard>

            <NCard title="巡检日志" size="small">
              <NAlert v-if="inspectionLogsError" type="error" :show-icon="false">
                {{ inspectionLogsError }}
              </NAlert>
              <NEmpty v-if="inspectionLogs.length === 0 && !inspectionLogsError" description="暂无巡检日志" size="small" />
              <div v-if="inspectionLogs.length > 0" class="inspection-log-list">
                <div v-for="(log, idx) in inspectionLogs" :key="`${log.at}-${idx}`" class="inspection-log-row">
                  <span>{{ formatDateTime(log.at) }}</span>
                  <NTag size="tiny" :bordered="false">{{ log.category }}</NTag>
                  <strong>{{ log.message }}</strong>
                </div>
              </div>
            </NCard>
          </div>
        </NTabPane>
      </NTabs>
    </NSpin>

    <!-- OAuth 弹窗：保持旧页面“先填写名称，再开始登录”的操作顺序。 -->
    <NModal v-model:show="oauthModal" title="Codex OAuth 登录" preset="card" style="width: 720px; max-width: 92vw" :mask-closable="false">
      <div class="oauth-form-group">
        <p class="oauth-form-label">账号显示名称（可选）</p>
        <NInput v-model:value="oauthDisplayName" placeholder="留空则用邮箱" />
      </div>
      <NButton type="primary" :loading="oauthStartLoading" @click="handleStartOAuth">开始登录</NButton>

      <div v-if="oauthUrl" class="oauth-auth-area">
        <NAlert type="info" :show-icon="false">
          <strong>操作步骤：</strong>
          <ol class="oauth-steps">
            <li>点击下方链接，在新标签页中登录 OpenAI 账号。</li>
            <li>登录后浏览器跳转到 localhost 并显示无法访问是正常现象。</li>
            <li>复制浏览器地址栏中的完整 URL，粘贴到下方输入框。</li>
            <li>点击“完成登录”。</li>
          </ol>
        </NAlert>
        <p class="oauth-form-label">授权链接</p>
        <div class="oauth-url-row">
          <NInput :value="oauthUrl" readonly />
          <NButton secondary @click="copyText(oauthUrl)">复制</NButton>
          <NButton tag="a" :href="oauthUrl" target="_blank" secondary type="primary">打开</NButton>
        </div>
        <p class="oauth-form-label">粘贴登录后跳转的完整 URL</p>
        <NInput v-model:value="oauthCallbackInput" placeholder="http://localhost:1455/auth/callback?code=...&state=..." type="textarea" :autosize="{ minRows: 3 }" />
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="oauthModal = false">取消</NButton>
          <NButton type="primary" :loading="oauthLoading" @click="handleCompleteOAuth">完成登录</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 导入凭证弹窗 -->
    <NModal v-model:show="importModal" title="导入 Codex 凭证" preset="card" style="width: 600px; max-width: 92vw" :mask-closable="false" @after-leave="closeImportCredential">
      <p class="credential-import-label">选择一个或多个 CPA 凭证 JSON 文件：</p>
      <input class="credential-file-input" type="file" accept=".json,application/json" multiple @change="handleCredentialFiles">
      <div v-if="importFiles.length" class="credential-file-summary">已选择 {{ importFiles.length }} 个文件，将优先导入所选文件。</div>
      <div class="credential-import-divider"><span>或粘贴 JSON</span></div>
      <p class="credential-import-label">粘贴 CPA 格式的凭证 JSON（含 access_token / refresh_token / id_token）：</p>
      <NInput
        v-model:value="importJsonText"
        type="textarea"
        :autosize="{ minRows: 8, maxRows: 20 }"
        placeholder='{"access_token":"...","refresh_token":"...","id_token":"..."}'
        style="font-family: monospace"
      />
      <div v-if="importFailures.length" class="credential-import-failures">
        <strong>导入失败明细</strong>
        <div v-for="(failure, idx) in importFailures" :key="`${failure.fileName}-${idx}`">
          {{ failure.fileName || '凭证 JSON' }}：{{ failure.error }}
        </div>
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="closeImportCredential">取消</NButton>
          <NButton type="primary" :loading="importLoading" @click="handleImportCredential">导入</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 编辑账号（重命名）弹窗 -->
    <NModal v-model:show="editModal" title="编辑账号" preset="card" style="width: 480px; max-width: 92vw">
      <NSpace vertical :size="16">
        <div>
          <p class="edit-label">显示名称</p>
          <NInput v-model:value="editDisplayName" placeholder="显示名称" />
        </div>
        <div>
          <p class="edit-label">更新凭证（可选）</p>
          <NInput
            v-model:value="editRefreshToken"
            type="textarea"
            placeholder="粘贴新的 refresh_token，留空则不修改凭证"
            :autosize="{ minRows: 2, maxRows: 4 }"
          />
          <p class="edit-hint">填入后会立即用新凭证刷新 access_token。token 过期或失效时可用此功能恢复。</p>
        </div>
        <div>
          <p class="edit-label">手动刷新 Token</p>
          <NSpace align="center" :size="8">
            <NButton :loading="editTokenRefreshing" @click="handleManualRefreshToken">立即刷新 access_token</NButton>
            <span v-if="editAccount?.tokenExpiresAt" class="edit-hint">
              当前过期：{{ formatDateTime(editAccount.tokenExpiresAt) }}
            </span>
          </NSpace>
          <p class="edit-hint">用现有 refresh_token 立即刷新 access_token 并更新过期时间。自动刷新未生效时可在此手动恢复。</p>
        </div>
      </NSpace>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="editModal = false">取消</NButton>
          <NButton type="primary" :loading="editLoading" @click="handleSaveEdit">保存</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 重置额度信用弹窗 -->
    <NModal v-model:show="resetCreditModal" title="重置额度信用" preset="card" style="width: 480px; max-width: 92vw">
      <NSpin :show="resetCreditLoading">
        <div v-if="resetCreditInfo">
          <p v-if="!resetCreditInfo.success" class="reset-credit-error">
            {{ resetCreditInfo.error || '重置信用加载失败' }}
          </p>
          <template v-else>
            <p class="reset-credit-count">
              可用重置次数：<strong>{{ resetCreditInfo.availableCount }}</strong>
            </p>
            <div v-if="resetCreditInfo.credits.length > 0" class="reset-credit-list">
              <p class="reset-credit-list-title">各次重置过期时间（北京时间 GMT+8）：</p>
              <div
                v-for="(credit, idx) in resetCreditInfo.credits"
                :key="credit.id || idx"
                class="reset-credit-item"
              >
                <div>
                  <strong>第 {{ idx + 1 }} 次重置</strong>
                  <span>发放时间：{{ formatBeijingTime(credit.grantedAt) }}</span>
                </div>
                <div class="reset-credit-expiry">
                  <span>过期时间</span>
                  <strong>{{ formatBeijingTime(credit.expiresAt) }}</strong>
                </div>
              </div>
            </div>
          </template>
        </div>
      </NSpin>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="resetCreditModal = false">关闭</NButton>
          <NPopconfirm
            v-if="resetCreditInfo?.success && resetCreditInfo.availableCount > 0"
            @positive-click="handleConsumeResetCredit"
          >
            <template #trigger>
              <NButton type="primary" :loading="resetCreditSubmitting">消耗一次重置</NButton>
            </template>
            确认消耗一张手动重置额度并执行真实额度重置？此操作不可撤销。
          </NPopconfirm>
        </NSpace>
      </template>
    </NModal>

    <!-- 拉取/导入模型弹窗 -->
    <NModal v-model:show="modelModal" :title="`拉取模型 - ${modelAccount?.displayName ?? ''}`" preset="card" style="width: 560px; max-width: 92vw">
      <NSpin :show="modelLoading">
        <NEmpty v-if="!modelLoading && modelList.length === 0" description="该账号无可用模型" size="small" />
        <template v-else>
          <div class="codex-model-toolbar">
            <NInput v-model:value="modelSearch" size="small" clearable placeholder="搜索模型" />
            <NCheckbox
              :checked="allVisibleModelsChecked"
              :indeterminate="someVisibleModelsChecked"
              @update:checked="toggleVisibleModels"
            >
              已选 {{ visibleCheckedModelCount }} / {{ filteredModelList.length }} 个
            </NCheckbox>
          </div>
          <div class="codex-model-list">
            <div
              v-for="m in filteredModelList"
              :key="m.remoteModelName"
              class="codex-model-row"
            >
              <NCheckbox
                :checked="checkedModels.includes(m.remoteModelName)"
                @update:checked="(checked: boolean) => checked
                  ? checkedModels.push(m.remoteModelName)
                  : (checkedModels = checkedModels.filter(name => name !== m.remoteModelName))"
              />
              <code :title="m.remoteModelName">{{ m.remoteModelName }}</code>
              <NInput v-model:value="m.alias" size="small" placeholder="显示别名" />
              <NTag
                size="tiny"
                :type="m.existingMappingId ? (m.isEnabled ? 'success' : 'default') : 'info'"
                :bordered="false"
              >
                {{ m.existingMappingId ? (m.isEnabled ? '已启用' : '已禁用') : '新' }}
              </NTag>
            </div>
          </div>
        </template>
      </NSpin>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="modelModal = false">取消</NButton>
          <NButton type="primary" :disabled="checkedModels.length === 0" :loading="modelLoading" @click="handleImportModels">
            导入选中（{{ checkedModels.length }}）
          </NButton>
        </NSpace>
      </template>
    </NModal>
  </div>
</template>

<style scoped>
.codex-model-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 12px;
}

.codex-model-toolbar :deep(.n-input) {
  flex: 1;
}

.codex-model-list {
  max-height: 420px;
  overflow: auto;
}

.codex-model-row {
  display: grid;
  grid-template-columns: 30px minmax(140px, 1fr) 200px auto;
  align-items: center;
  gap: 8px;
  padding: 8px 0;
  border-bottom: 1px solid var(--border-color-global);
}

.codex-model-row code {
  overflow: hidden;
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.oauth-form-group {
  margin-bottom: 12px;
}

.oauth-form-label {
  margin: 14px 0 8px;
  color: var(--text-primary);
  font-size: 14px;
  font-weight: 600;
}

.oauth-auth-area {
  display: grid;
  gap: 10px;
  margin-top: 16px;
}

.oauth-steps {
  margin: 8px 0 0;
  padding-left: 20px;
  font-size: 13px;
  line-height: 1.8;
}

.oauth-url-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  gap: 8px;
}

.credential-import-label {
  margin: 0 0 8px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.credential-file-input {
  width: 100%;
  padding: 9px 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 8px;
  background: var(--bg-input);
  color: var(--text-primary);
  font-size: 13px;
}

.credential-file-summary {
  margin-top: 7px;
  color: var(--status-success-text);
  font-size: 13px;
}

.credential-import-divider {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 14px 0;
  color: var(--text-color-secondary);
  font-size: 12px;
}

.credential-import-divider::before,
.credential-import-divider::after {
  height: 1px;
  flex: 1;
  background: var(--border-color-global);
  content: '';
}

.credential-import-failures {
  display: grid;
  gap: 5px;
  margin-top: 12px;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--status-danger-text) 30%, transparent);
  border-radius: 8px;
  background: var(--status-danger-bg);
  color: var(--status-danger-text);
  font-size: 13px;
}

.reset-credit-count {
  margin: 0 0 12px;
}

.reset-credit-error {
  margin: 0;
  color: var(--status-danger-text);
}

.edit-label {
  margin: 0 0 6px;
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.edit-hint {
  margin: 6px 0 0;
  font-size: 12px;
  color: var(--text-color-secondary);
  line-height: 1.5;
}

.codex-token-expired {
  color: var(--status-danger-text, #e03131);
  font-weight: 600;
}

.codex-token-warning {
  color: var(--status-danger-text, #e03131);
  font-weight: 600;
}

:global([data-theme='dark']) .codex-token-expired { color: #f87171; }
:global([data-theme='dark']) .codex-token-warning { color: #f87171; }

.reset-credit-list-title {
  margin: 0 0 8px;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.reset-credit-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 8px;
  padding: 12px 16px;
  border: 1px solid var(--border-color-global);
  border-radius: 6px;
  background: var(--bg-page);
  font-size: 13px;
}

.reset-credit-item > div {
  display: grid;
  gap: 4px;
}

.reset-credit-item span {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.reset-credit-expiry {
  text-align: right;
}

.reset-credit-expiry strong {
  color: var(--status-warning-text);
}

.codex-export-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 16px;
  padding: 12px;
  border: 1px solid color-mix(in srgb, var(--status-warning-text) 35%, transparent);
  border-radius: 8px;
  background: var(--status-warning-bg);
  color: var(--status-warning-text);
}

.codex-stack {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-width: 0;
}

.inspection-card {
  min-width: 0;
}

.inspection-content {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}

.inspection-main {
  display: flex;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.inspection-title {
  color: var(--text-primary);
  font-weight: 700;
}

.inspection-actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-wrap: wrap;
}

.inspection-empty-state {
  display: grid;
  justify-items: center;
  gap: 12px;
  padding: 32px 0;
}

.inspection-workspace {
  display: grid;
  gap: 16px;
  min-width: 0;
  padding-top: 8px;
}

.inspection-summary-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 10px;
}

.inspection-summary-grid > div {
  display: grid;
  gap: 4px;
  padding: 12px;
  border-radius: 8px;
  background: var(--bg-page);
}

.inspection-summary-grid span,
.inspection-run-meta,
.inspection-log-row > span,
.inspection-reason {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.inspection-summary-grid strong {
  color: var(--text-primary);
  font-size: 18px;
}

.inspection-run-meta {
  margin: 12px 0;
}

.inspection-table-scroll {
  width: 100%;
  max-width: 100%;
  overflow-x: auto;
}

.inspection-table {
  min-width: 820px;
}

.inspection-table-head,
.inspection-table-row {
  display: grid;
  grid-template-columns: minmax(150px, 1fr) 80px 80px 72px 72px minmax(220px, 1.4fr);
  gap: 12px;
  align-items: center;
  padding: 11px 12px;
  border-bottom: 1px solid var(--border-color-global);
  font-size: 13px;
}

.inspection-table-head {
  color: var(--text-color-secondary);
  background: var(--bg-page);
  font-weight: 600;
}

.inspection-log-list {
  display: grid;
  max-height: 360px;
  overflow: auto;
}

.inspection-log-row {
  display: grid;
  grid-template-columns: 170px 90px minmax(0, 1fr);
  gap: 12px;
  align-items: center;
  padding: 10px 0;
  border-bottom: 1px solid var(--border-color-global);
  font-size: 13px;
}

.inspection-meta,
.account-subtitle,
.quota-label,
.quota-empty {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.codex-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
  gap: 18px;
  margin-top: 16px;
}

.codex-card {
  display: flex;
  position: relative;
  flex-direction: column;
  min-width: 0;
  padding: 20px;
  border: 1px solid var(--border-color-global);
  border-radius: 12px;
  background: var(--bg-card);
  box-shadow: 0 2px 8px rgba(15, 23, 42, 0.06);
  transition: border-color 0.2s ease, box-shadow 0.2s ease, background 0.2s ease;
}

.codex-card:hover {
  border-color: color-mix(in srgb, var(--primary-color, #3b82f6) 42%, var(--border-color-global));
  box-shadow: 0 4px 16px rgba(15, 23, 42, 0.1);
}

.codex-card.disabled {
  opacity: 0.64;
}

.export-mode .codex-card {
  padding-left: 48px;
  cursor: pointer;
}

.codex-card.selected {
  border-color: var(--primary-color, #3b82f6);
  background: color-mix(in srgb, var(--primary-color, #3b82f6) 10%, var(--bg-card));
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--primary-color, #3b82f6) 20%, transparent);
}

.codex-export-checkbox {
  position: absolute;
  top: 18px;
  left: 16px;
}

.codex-card-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--border-color-global);
}

.codex-card-header-main {
  min-width: 0;
  flex: 1;
}

.codex-account-name {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  color: var(--text-primary);
  font-size: 15px;
  font-weight: 700;
  line-height: 1.45;
  word-break: break-all;
}

.codex-account-email {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 4px;
  color: var(--text-color-secondary);
  font-size: 12px;
  word-break: break-all;
}

.codex-badges {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  margin-top: 8px;
}

.codex-reset-credits-hint {
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--text-color-secondary);
  cursor: pointer;
  font: inherit;
  text-decoration: underline dotted;
}

.codex-reset-credits-hint:hover {
  color: var(--status-info-text);
}

.codex-plan {
  flex-shrink: 0;
  padding: 3px 10px;
  border-radius: 12px;
  background: color-mix(in srgb, var(--status-warning-text) 16%, transparent);
  color: var(--status-warning-text);
  font-size: 11px;
  font-weight: 500;
}

.codex-token-expiry {
  flex-shrink: 0;
  font-size: 11px;
  color: var(--text-color-secondary);
}

.codex-token-expiry.codex-token-expired {
  color: var(--status-danger-text, #e03131);
  font-weight: 600;
}

.codex-token-expiry.codex-token-warning {
  color: var(--status-danger-text, #e03131);
  font-weight: 600;
}

:global([data-theme='dark']) .codex-token-expiry.codex-token-expired { color: #f87171; }
:global([data-theme='dark']) .codex-token-expiry.codex-token-warning { color: #f87171; }
:global([data-theme='dark']) .codex-token-expiry { color: rgba(255, 255, 255, 0.5); }

.codex-windows-container {
  min-height: 60px;
  margin: 16px 0;
}

.codex-window {
  display: grid;
  grid-template-columns: 92px minmax(0, 1fr) 56px;
  align-items: center;
  column-gap: 12px;
  row-gap: 6px;
  padding: 10px 0 12px;
  border-bottom: 1px solid var(--border-color-soft);
}

.codex-window:last-child {
  border-bottom: none;
}

.codex-window-label {
  min-width: 0;
  overflow: hidden;
  color: var(--text-color-secondary);
  font-size: 13px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.codex-window-percent {
  min-width: 56px;
  color: var(--text-primary);
  font-size: 13px;
  font-weight: 600;
  text-align: right;
}

.codex-window-reset {
  grid-column: 2 / 4;
  min-width: 0;
  overflow: hidden;
  color: var(--text-color-secondary);
  font-size: 11px;
  line-height: 1.5;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.codex-window-placeholder {
  padding: 20px;
  border: 1px dashed var(--border-color-global);
  border-radius: 8px;
  background: var(--bg-surface-soft);
  color: var(--text-color-secondary);
  font-size: 13px;
  text-align: center;
}

.codex-card-meta {
  margin-top: 12px;
  padding-top: 16px;
  border-top: 1px solid var(--border-color-global);
}

.codex-source-meta {
  min-height: 16px;
  margin-bottom: 12px;
  color: var(--text-color-secondary);
  font-size: 11px;
  line-height: 1.5;
}

.codex-card-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.codex-icon-button {
  width: 36px;
  height: 36px;
}

.codex-icon-button :deep(.n-button__content) {
  line-height: 0;
}

.codex-icon-button svg {
  width: 16px;
  height: 16px;
  fill: none;
  stroke: currentColor;
  stroke-width: 2;
  stroke-linecap: round;
  stroke-linejoin: round;
}

.codex-icon-button.primary { color: var(--primary-color, #3b82f6); }
.codex-icon-button.info { color: var(--status-info-text, #3b82f6); }
.codex-icon-button.danger { color: var(--status-danger-text, #e03131); }

/* 暗色模式下确保图标按钮在深色背景上清晰可见 */
:global([data-theme='dark']) .codex-icon-button.primary { color: #60a5fa; }
:global([data-theme='dark']) .codex-icon-button.info { color: #38bdf8; }
:global([data-theme='dark']) .codex-icon-button.danger { color: #f87171; }

:global([data-theme='dark']) .codex-card {
  border-color: var(--border-color-global);
  background: var(--bg-card);
}

:global([data-theme='dark']) .codex-card-header,
:global([data-theme='dark']) .codex-card-meta {
  border-color: var(--border-color-global);
}

:global([data-theme='dark']) .codex-account-name {
  color: var(--text-primary);
}

:global([data-theme='dark']) .codex-window-placeholder {
  border-color: var(--border-color-global);
  background: var(--bg-input);
}

:global([data-theme='dark']) .codex-export-toolbar {
  border-color: color-mix(in srgb, var(--status-warning-text) 35%, transparent);
  background: var(--status-warning-bg);
  color: var(--status-warning-text);
}

@media (max-width: 720px) {
  .codex-grid,
  .account-kpi-row,
  .account-meta-grid,
  .codex-window,
  .inspection-summary-grid,
  .inspection-log-row {
    grid-template-columns: 1fr;
  }

  .codex-window-reset {
    grid-column: 1;
  }

  .codex-card-header,
  .inspection-content,
  .reset-credit-item {
    align-items: stretch;
    flex-direction: column;
  }

  .codex-export-toolbar,
  .codex-card-actions {
    flex-wrap: wrap;
  }

  .oauth-url-row {
    grid-template-columns: 1fr;
  }

  .codex-model-row {
    grid-template-columns: 30px minmax(0, 1fr) auto;
  }

  .codex-model-row :deep(.n-input) {
    grid-column: 2 / -1;
  }

  .reset-credit-expiry {
    text-align: left;
  }
}
</style>
