<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NGrid, NGi, NEmpty, NSpin, NModal, NInput, NPopconfirm, NProgress, NCheckbox, useMessage } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as api from '@/api/codex'
import type { CodexAccount, CodexInspectionStatus } from '@/api/codex'

const message = useMessage()
const loading = ref(false)
const accounts = ref<CodexAccount[]>([])
const inspection = ref<CodexInspectionStatus | null>(null)

// OAuth 弹窗
const oauthModal = ref(false)
const oauthUrl = ref('')
const oauthCallbackInput = ref('')
const oauthDisplayName = ref('')
const oauthLoading = ref(false)

// 凭证导入弹窗
const importModal = ref(false)
const importJsonText = ref('')
const importLoading = ref(false)

// 编辑账号（重命名）弹窗
const editModal = ref(false)
const editAccount = ref<CodexAccount | null>(null)
const editDisplayName = ref('')
const editLoading = ref(false)

// 重置额度信用弹窗
const resetCreditModal = ref(false)
const resetCreditAccount = ref<CodexAccount | null>(null)
const resetCreditInfo = ref<{ availableCount: number; items: Array<{ count: number; expiresAt: string }> } | null>(null)
const resetCreditLoading = ref(false)

// 拉取/导入模型弹窗
const modelModal = ref(false)
const modelAccount = ref<CodexAccount | null>(null)
const modelList = ref<Array<{ id: string; name: string }>>([])
const checkedModels = ref<string[]>([])
const modelLoading = ref(false)

let pollTimer: ReturnType<typeof setInterval> | null = null

async function load(): Promise<void> {
  loading.value = true
  try {
    const [accs, insp] = await Promise.all([api.listCodexAccounts(), api.getCodexInspectionStatus().catch(() => null)])
    accounts.value = accs
    inspection.value = insp
  } finally { loading.value = false }
}

async function handleStartOAuth(): Promise<void> {
  try {
    const result = await api.startCodexOAuth()
    oauthUrl.value = result.url
    oauthCallbackInput.value = ''
    oauthDisplayName.value = ''
    oauthModal.value = true
  } catch (e) { message.error((e as Error).message) }
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
async function handleRefreshToken(acc: CodexAccount): Promise<void> {
  try {
    await api.refreshCodexToken(acc.id)
    message.success('已刷新 Token')
    await load()
  } catch (e) { message.error((e as Error).message) }
}
async function handleDelete(acc: CodexAccount): Promise<void> {
  await api.deleteCodexAccount(acc.id)
  message.success('已删除账号')
  await load()
}
async function handleRunInspection(): Promise<void> {
  await api.runCodexInspection()
  message.success('已触发巡检')
  setTimeout(load, 2000)
}

// 编辑（重命名）
function openEdit(acc: CodexAccount): void {
  editAccount.value = acc
  editDisplayName.value = acc.displayName
  editModal.value = true
}
async function handleSaveEdit(): Promise<void> {
  if (!editAccount.value || !editDisplayName.value.trim()) { message.warning('名称不能为空'); return }
  editLoading.value = true
  try {
    await api.updateCodexAccount(editAccount.value.id, editDisplayName.value.trim())
    message.success('已更新')
    editModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { editLoading.value = false }
}

// 重置额度信用
async function openResetCredit(acc: CodexAccount): Promise<void> {
  resetCreditAccount.value = acc
  resetCreditInfo.value = null
  resetCreditModal.value = true
  resetCreditLoading.value = true
  try {
    resetCreditInfo.value = await api.getResetCredits(acc.id)
  } catch (e) { message.error((e as Error).message) } finally { resetCreditLoading.value = false }
}
async function handleConsumeResetCredit(): Promise<void> {
  if (!resetCreditAccount.value) return
  try {
    await api.consumeResetCredit(resetCreditAccount.value.id)
    message.success('已消耗一次重置信用')
    resetCreditInfo.value = await api.getResetCredits(resetCreditAccount.value.id)
    await load()
  } catch (e) { message.error((e as Error).message) }
}

// 拉取/导入模型
async function openFetchModels(acc: CodexAccount): Promise<void> {
  modelAccount.value = acc
  modelList.value = []
  checkedModels.value = []
  modelModal.value = true
  modelLoading.value = true
  try {
    modelList.value = await api.fetchCodexModels(acc.id)
  } catch (e) { message.error((e as Error).message) } finally { modelLoading.value = false }
}
async function handleImportModels(): Promise<void> {
  if (!modelAccount.value || checkedModels.value.length === 0) { message.warning('请选择要导入的模型'); return }
  modelLoading.value = true
  try {
    await api.importSelectedCodexModels(modelAccount.value.id, checkedModels.value)
    message.success(`已导入 ${checkedModels.value.length} 个模型`)
    modelModal.value = false
  } catch (e) { message.error((e as Error).message) } finally { modelLoading.value = false }
}

async function handleImportCredential(): Promise<void> {
  if (!importJsonText.value.trim()) { message.warning('请粘贴凭证 JSON'); return }
  importLoading.value = true
  try {
    await api.importCredential(importJsonText.value.trim())
    message.success('凭证导入成功')
    importModal.value = false
    importJsonText.value = ''
    await load()
  } catch (e) { message.error((e as Error).message) } finally { importLoading.value = false }
}

async function handleExportCredentials(): Promise<void> {
  const ids = accounts.value.map((a) => a.id)
  if (ids.length === 0) { message.warning('没有可导出的账号'); return }
  try {
    await api.exportCredentials(ids)
    message.success('凭证已导出')
  } catch (e) { message.error((e as Error).message) }
}

// 额度进度条颜色
function quotaColor(percent: number | null | undefined): 'success' | 'warning' | 'error' {
  if (percent == null) return 'success'
  if (percent >= 95) return 'error'
  if (percent >= 80) return 'warning'
  return 'success'
}

onMounted(() => {
  load()
  pollTimer = setInterval(() => {
    if (document.visibilityState === 'visible') load()
  }, 10000)
})
onUnmounted(() => { if (pollTimer) clearInterval(pollTimer) })
</script>

<template>
  <div class="page-container">
    <PageHeader title="OAuth 管理" subtitle="管理 Codex OAuth 登录账号、凭证导入、额度、巡检与自动禁用">
      <template #actions>
        <NTag v-if="accounts.length" round :bordered="false" size="small">{{ accounts.length }} 个</NTag>
        <NButton v-if="inspection" size="small" @click="handleRunInspection">触发巡检</NButton>
        <NButton size="small" quaternary @click="importModal = true">导入凭证</NButton>
        <NButton size="small" quaternary :disabled="accounts.length === 0" @click="handleExportCredentials">导出凭证</NButton>
        <NButton size="small" type="primary" @click="handleStartOAuth">OAuth 登录</NButton>
      </template>
    </PageHeader>
    <NSpin :show="loading">
      <NCard>

        <!-- 巡检状态 -->
        <NCard v-if="inspection" size="small" style="margin-bottom: 16px">
          <NSpace :size="24" align="center">
            <NTag :type="inspection.isRunning ? 'warning' : 'success'" :bordered="false">
              {{ inspection.isRunning ? '巡检中' : '空闲' }}
            </NTag>
            <span v-if="inspection.lastRun" style="font-size: 13px; color: var(--text-color-secondary)">
              上次：{{ inspection.lastRun.totalAccounts }} 账号，禁用 {{ inspection.lastRun.disabledAccounts }} 个
            </span>
          </NSpace>
        </NCard>

        <NEmpty v-if="accounts.length === 0" description="暂无 Codex 账号，点击右上角 OAuth 登录" />

        <NGrid v-else :cols="3" :x-gap="16" :y-gap="16" responsive="screen" item-responsive>
          <NGi v-for="acc in accounts" :key="acc.id" span="3 m:1 l:1">
            <NCard size="small">
              <template #header>
                <NSpace align="center" :size="8">
                  <span style="font-weight: 600">{{ acc.displayName }}</span>
                  <NTag v-if="acc.email" size="tiny" :bordered="false">{{ acc.email }}</NTag>
                  <NTag v-if="acc.planType" size="tiny" type="info" :bordered="false">{{ acc.planType }}</NTag>
                  <NTag size="tiny" :type="acc.isEnabled ? 'success' : 'default'" :bordered="false">
                    {{ acc.isEnabled ? '启用' : '禁用' }}
                  </NTag>
                  <NTag v-if="acc.isQuotaCooling" size="tiny" type="warning" :bordered="false">冷却中</NTag>
                </NSpace>
              </template>
              <div style="font-size: 12px; color: var(--text-color-secondary); margin-bottom: 8px">
                上次额度检查：{{ acc.lastQuotaCheckedAt ? new Date(acc.lastQuotaCheckedAt).toLocaleString('zh-CN') : '从未' }}
              </div>

              <!-- 额度窗口进度条 -->
              <div v-if="acc.windows && acc.windows.length > 0" class="quota-windows">
                <div v-for="w in acc.windows" :key="w.id" class="quota-window">
                  <div class="quota-label">
                    <span>{{ w.label }}</span>
                    <span v-if="w.resetLabel" style="font-size: 11px; color: var(--text-color-secondary)">重置于 {{ w.resetLabel }}</span>
                  </div>
                  <NProgress
                    :percentage="Math.round(w.usedPercent)"
                    :status="quotaColor(w.usedPercent)"
                    :show-indicator="false"
                    :height="8"
                    :border-radius="4"
                  />
                  <span class="quota-percent">{{ Math.round(w.usedPercent) }}%</span>
                </div>
              </div>
              <div v-if="acc.resetCreditsAvailableCount != null && acc.resetCreditsAvailableCount > 0" style="font-size: 12px; margin: 6px 0; color: #6C9EFF">
                剩余 {{ acc.resetCreditsAvailableCount }} 次手动重置
              </div>

              <NSpace :size="4" wrap>
                <NButton size="tiny" quaternary @click="handleRefreshQuota(acc)">刷新额度</NButton>
                <NButton size="tiny" quaternary @click="handleRefreshToken(acc)">刷新Token</NButton>
                <NButton size="tiny" quaternary @click="openEdit(acc)">编辑</NButton>
                <NButton size="tiny" quaternary @click="openFetchModels(acc)">拉取模型</NButton>
                <NButton v-if="acc.resetCreditsAvailableCount != null && acc.resetCreditsAvailableCount > 0" size="tiny" quaternary @click="openResetCredit(acc)">重置额度</NButton>
                <NButton size="tiny" quaternary @click="handleToggle(acc)">{{ acc.isEnabled ? '禁用' : '启用' }}</NButton>
                <NPopconfirm @positive-click="handleDelete(acc)">
                  <template #trigger><NButton size="tiny" quaternary type="error">删除</NButton></template>
                  删除账号「{{ acc.displayName }}」？关联站点和路由会一并清理。
                </NPopconfirm>
              </NSpace>
            </NCard>
          </NGi>
        </NGrid>
      </NCard>
    </NSpin>

    <!-- OAuth 弹窗 -->
    <NModal v-model:show="oauthModal" title="Codex OAuth 登录" preset="card" style="width: 600px; max-width: 92vw" :mask-closable="false">
      <div style="margin-bottom: 12px">
        <p style="margin: 0 0 8px; font-weight: 600">第 1 步：打开授权链接</p>
        <NInput :value="oauthUrl" readonly type="textarea" :autosize="{ minRows: 2 }" />
        <NButton size="small" style="margin-top: 8px" tag="a" :href="oauthUrl" target="_blank">在新标签打开</NButton>
      </div>
      <div>
        <p style="margin: 0 0 8px; font-weight: 600">第 2 步：完成授权后，粘贴回调后的完整 URL</p>
        <NInput v-model:value="oauthCallbackInput" placeholder="https://chatgpt.com/auth/callback?code=..." type="textarea" :autosize="{ minRows: 2 }" />
      </div>
      <div style="margin-top: 12px">
        <p style="margin: 0 0 8px; font-weight: 600">显示名称（可选）</p>
        <NInput v-model:value="oauthDisplayName" placeholder="给这个账号起个好认的名字" />
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="oauthModal = false">取消</NButton>
          <NButton type="primary" :loading="oauthLoading" @click="handleCompleteOAuth">完成登录</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 导入凭证弹窗 -->
    <NModal v-model:show="importModal" title="导入 Codex 凭证" preset="card" style="width: 600px; max-width: 92vw" :mask-closable="false">
      <p style="margin: 0 0 8px; color: var(--text-color-secondary)">粘贴 CPA 格式的凭证 JSON（含 access_token / refresh_token / id_token）：</p>
      <NInput
        v-model:value="importJsonText"
        type="textarea"
        :autosize="{ minRows: 8, maxRows: 20 }"
        placeholder='{"access_token":"...","refresh_token":"...","id_token":"..."}'
        style="font-family: monospace"
      />
      <template #footer>
        <NSpace justify="end">
          <NButton @click="importModal = false">取消</NButton>
          <NButton type="primary" :loading="importLoading" @click="handleImportCredential">导入</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 编辑账号（重命名）弹窗 -->
    <NModal v-model:show="editModal" title="编辑账号" preset="card" style="width: 420px; max-width: 92vw">
      <NInput v-model:value="editDisplayName" placeholder="显示名称" />
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
          <p style="margin: 0 0 12px">可用重置次数：<strong>{{ resetCreditInfo.availableCount }}</strong></p>
          <div v-if="resetCreditInfo.items.length > 0">
            <p style="margin: 0 0 8px; color: var(--text-color-secondary); font-size: 13px">信用明细：</p>
            <div v-for="(item, idx) in resetCreditInfo.items" :key="idx" style="font-size: 13px; margin-bottom: 4px">
              {{ item.count }} 次 · 过期 {{ new Date(item.expiresAt).toLocaleString('zh-CN') }}
            </div>
          </div>
        </div>
      </NSpin>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="resetCreditModal = false">关闭</NButton>
          <NButton
            v-if="resetCreditInfo && resetCreditInfo.availableCount > 0"
            type="primary"
            @click="handleConsumeResetCredit"
          >消耗一次重置</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 拉取/导入模型弹窗 -->
    <NModal v-model:show="modelModal" :title="`拉取模型 - ${modelAccount?.displayName ?? ''}`" preset="card" style="width: 560px; max-width: 92vw">
      <NSpin :show="modelLoading">
        <NEmpty v-if="!modelLoading && modelList.length === 0" description="该账号无可用模型" size="small" />
        <NSpace v-else vertical :size="6">
          <NCheckbox
            v-for="m in modelList"
            :key="m.id"
            :checked="checkedModels.includes(m.id)"
            @update:checked="(v: boolean) => v ? checkedModels.push(m.id) : (checkedModels = checkedModels.filter(x => x !== m.id))"
          >
            {{ m.name }}
          </NCheckbox>
        </NSpace>
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
.quota-windows { display: flex; flex-direction: column; gap: 6px; margin-bottom: 10px; }
.quota-window { display: grid; grid-template-columns: 1fr auto; align-items: center; gap: 4px 8px; }
.quota-window .n-progress { grid-column: 1 / 2; }
.quota-label { grid-column: 1 / 3; display: flex; justify-content: space-between; font-size: 12px; color: var(--text-color-secondary); }
.quota-percent { grid-column: 2 / 3; font-size: 12px; font-weight: 600; min-width: 36px; text-align: right; }
</style>
