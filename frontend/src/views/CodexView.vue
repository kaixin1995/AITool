<script setup lang="ts">
import { computed, h, onMounted, onUnmounted, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NGrid, NGi, NEmpty, NSpin, NModal, NInput, NPopconfirm, NUpload, useMessage } from 'naive-ui'
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
const oauthLoading = ref(false)

// 凭证导入弹窗
const importModal = ref(false)
const importJsonText = ref('')
const importLoading = ref(false)

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
    oauthModal.value = true
  } catch (e) { message.error((e as Error).message) }
}

async function handleCompleteOAuth(): Promise<void> {
  if (!oauthCallbackInput.value.trim()) { message.warning('请粘贴回调 URL'); return }
  oauthLoading.value = true
  try {
    await api.completeCodexOAuth(oauthCallbackInput.value.trim())
    message.success('OAuth 登录成功')
    oauthModal.value = false
    await load()
  } catch (e) { message.error((e as Error).message) } finally { oauthLoading.value = false }
}

async function handleToggle(acc: CodexAccount): Promise<void> {
  await api.toggleCodexAccount(acc.id)
  acc.isEnabled = !acc.isEnabled
}
async function handleRefreshQuota(acc: CodexAccount): Promise<void> {
  await api.refreshCodexQuota(acc.id)
  message.success('已刷新额度')
  await load()
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
              <NSpace :size="4">
                <NButton size="tiny" quaternary @click="handleRefreshQuota(acc)">刷新额度</NButton>
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
    <NModal v-model:show="oauthModal" title="Codex OAuth 登录" preset="card" style="width: 600px" :mask-closable="false">
      <div style="margin-bottom: 12px">
        <p style="margin: 0 0 8px; font-weight: 600">第 1 步：打开授权链接</p>
        <NInput :value="oauthUrl" readonly type="textarea" :autosize="{ minRows: 2 }" />
        <NButton size="small" style="margin-top: 8px" tag="a" :href="oauthUrl" target="_blank">在新标签打开</NButton>
      </div>
      <div>
        <p style="margin: 0 0 8px; font-weight: 600">第 2 步：完成授权后，粘贴回调后的完整 URL</p>
        <NInput v-model:value="oauthCallbackInput" placeholder="https://chatgpt.com/auth/callback?code=..." type="textarea" :autosize="{ minRows: 2 }" />
      </div>
      <template #footer>
        <NSpace justify="end">
          <NButton @click="oauthModal = false">取消</NButton>
          <NButton type="primary" :loading="oauthLoading" @click="handleCompleteOAuth">完成登录</NButton>
        </NSpace>
      </template>
    </NModal>

    <!-- 导入凭证弹窗 -->
    <NModal v-model:show="importModal" title="导入 Codex 凭证" preset="card" style="width: 600px" :mask-closable="false">
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
  </div>
</template>
