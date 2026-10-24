<template>
  <div class="device-oauth-tab">
    <NAlert type="info" :show-icon="true" style="margin-bottom: 12px">
      通用轮询式 OAuth 账号（MiniMax Code / Qoder / ZCode / GitHub Copilot / Claude 订阅）：
      登录后自动创建托管账号，接入统一的额度巡检与自动启停；支持聊天转发的厂商会自动创建隐藏站点并进入路由。
    </NAlert>

    <div class="vendor-row">
      <NButton v-for="vendor in vendors" :key="vendor.vendorKey" size="small" @click="startSignIn(vendor)">
        登录 {{ vendor.label }}
      </NButton>
      <NButton size="small" quaternary @click="load">刷新</NButton>
    </div>

    <NSpin :show="loading">
      <NEmpty v-if="accounts.length === 0" description="暂无通用 OAuth 账号" style="padding: 32px 0" />
      <div v-else class="account-grid">
        <NCard v-for="account in accounts" :key="account.id" size="small" :title="account.displayName">
          <template #header-extra>
            <NSpace size="small">
              <NTag size="small" :bordered="false">{{ account.vendorLabel }}</NTag>
              <NTag v-if="account.requiresReauth" size="small" type="warning" :bordered="false">需重新登录</NTag>
              <NTag v-else-if="!account.isEnabled" size="small" type="error" :bordered="false">已禁用</NTag>
              <NTag v-else size="small" type="success" :bordered="false">启用</NTag>
            </NSpace>
          </template>
          <div class="account-meta">
            <span v-if="account.email">{{ account.email }}</span>
            <span v-if="account.planType">{{ account.planType }}</span>
          </div>
          <div v-if="account.windows.length > 0" class="window-list">
            <div v-for="w in account.windows" :key="w.id" class="window-row">
              <span class="window-label">{{ w.label }}</span>
              <NProgress
                type="line"
                :percentage="Math.min(100, Math.max(0, w.usedPercent ?? 0))"
                :height="8"
                :show-indicator="false"
                :color="(w.usedPercent ?? 0) >= 95 ? '#e88080' : undefined"
                style="flex: 1"
              />
              <span class="window-percent">{{ (w.usedPercent ?? 0).toFixed(0) }}%</span>
            </div>
          </div>
          <template #action>
            <NSpace size="small">
              <NButton size="tiny" @click="refreshQuota(account)">刷新额度</NButton>
              <NButton size="tiny" @click="toggle(account, !account.isEnabled)">
                {{ account.isEnabled ? '禁用' : '启用' }}
              </NButton>
              <NPopconfirm @positive-click="remove(account)">
                <template #trigger><NButton size="tiny" type="error">删除</NButton></template>
                确认删除该账号（含关联隐藏站点）？
              </NPopconfirm>
            </NSpace>
          </template>
        </NCard>
      </div>
    </NSpin>

    <NModal v-model:show="signInVisible" preset="card" title="登录通用 OAuth 账号" style="max-width: 560px">
      <div v-if="signInState">
        <NAlert type="info" :show-icon="false" style="margin-bottom: 10px">{{ signInState.instructions }}</NAlert>
        <p class="authorize-url">
          <a :href="signInState.authorizeUrl" target="_blank" rel="noopener">{{ signInState.authorizeUrl }}</a>
        </p>
        <NInput
          v-model:value="callbackUrl"
          placeholder="粘贴回调流（如 Claude）请在此粘贴回跳地址；轮询流留空即可"
          style="margin-top: 10px"
        />
        <NSpace style="margin-top: 12px">
          <NButton size="small" :loading="polling" @click="poll(true)">检查登录状态</NButton>
          <NButton size="small" quaternary @click="cancelSignIn">取消</NButton>
        </NSpace>
      </div>
    </NModal>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { NAlert, NButton, NCard, NEmpty, NInput, NModal, NPopconfirm, NProgress, NSpace, NSpin, NTag, useMessage } from 'naive-ui'
import {
  deleteDeviceOAuthAccount,
  fetchDeviceOAuthAccounts,
  fetchDeviceOAuthVendors,
  pollDeviceOAuthSignIn,
  refreshDeviceOAuthQuota,
  startDeviceOAuthSignIn,
  toggleDeviceOAuthAccount,
  type DeviceOAuthAccount,
  type DeviceOAuthStartResult,
  type DeviceOAuthVendor
} from '../api/deviceOauth'

const message = useMessage()
const vendors = ref<DeviceOAuthVendor[]>([])
const accounts = ref<DeviceOAuthAccount[]>([])
const loading = ref(false)
const signInVisible = ref(false)
const signInState = ref<DeviceOAuthStartResult | null>(null)
const callbackUrl = ref('')
const polling = ref(false)
let autoPollTimer: ReturnType<typeof setInterval> | null = null

async function load() {
  loading.value = true
  try {
    ;[vendors.value, accounts.value] = await Promise.all([fetchDeviceOAuthVendors(), fetchDeviceOAuthAccounts()])
  } catch (error) {
    message.error(`加载失败：${(error as Error).message}`)
  } finally {
    loading.value = false
  }
}

async function startSignIn(vendor: DeviceOAuthVendor) {
  try {
    signInState.value = await startDeviceOAuthSignIn(vendor.vendorKey)
    callbackUrl.value = ''
    signInVisible.value = true
    // 轮询流自动每 3 秒检查（粘贴回调流自动轮询无害：后端返回 pending）。
    autoPollTimer = setInterval(() => poll(false), 3000)
  } catch (error) {
    message.error(`发起登录失败：${(error as Error).message}`)
  }
}

async function poll(manual: boolean) {
  if (!signInState.value) return
  polling.value = true
  try {
    const result = await pollDeviceOAuthSignIn(
      signInState.value.sessionId,
      undefined,
      callbackUrl.value.trim() || undefined
    )
    if (result.status === 'pending' && result.nextUrl) {
      signInState.value = { ...signInState.value!, authorizeUrl: result.nextUrl, instructions: result.message || '请继续完成下一段授权并粘贴新的回跳地址' }
      callbackUrl.value = ''
      message.info('请继续完成第二段授权')
      return
    }
    if (result.status === 'success') {
      message.success('登录成功，账号已创建')
      finishSignIn()
      await load()
    } else if (result.status === 'failed') {
      message.error(result.error || '登录失败')
      finishSignIn()
    } else if (manual) {
      message.info('尚未完成授权，请在浏览器继续')
    }
  } catch (error) {
    if (manual) message.error(`检查失败：${(error as Error).message}`)
  } finally {
    polling.value = false
  }
}

function finishSignIn() {
  if (autoPollTimer) {
    clearInterval(autoPollTimer)
    autoPollTimer = null
  }
  signInVisible.value = false
  signInState.value = null
}

function cancelSignIn() {
  finishSignIn()
}

async function refreshQuota(account: DeviceOAuthAccount) {
  try {
    await refreshDeviceOAuthQuota(account.id)
    message.success('额度已刷新')
    await load()
  } catch (error) {
    message.error(`刷新失败：${(error as Error).message}`)
  }
}

async function toggle(account: DeviceOAuthAccount, enabled: boolean) {
  try {
    await toggleDeviceOAuthAccount(account.id, enabled)
    await load()
  } catch (error) {
    message.error(`操作失败：${(error as Error).message}`)
  }
}

async function remove(account: DeviceOAuthAccount) {
  try {
    await deleteDeviceOAuthAccount(account.id)
    message.success('已删除')
    await load()
  } catch (error) {
    message.error(`删除失败：${(error as Error).message}`)
  }
}

onMounted(load)
onUnmounted(() => {
  if (autoPollTimer) clearInterval(autoPollTimer)
})
</script>

<style scoped>
.vendor-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 14px;
}
.account-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
  gap: 12px;
}
.account-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  color: var(--n-text-color-3, #888);
  font-size: 12px;
  margin-bottom: 8px;
}
.window-list {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.window-row {
  display: flex;
  align-items: center;
  gap: 8px;
}
.window-label {
  width: 130px;
  font-size: 12px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.window-percent {
  width: 38px;
  text-align: right;
  font-size: 12px;
}
.authorize-url {
  word-break: break-all;
  font-size: 12px;
}
</style>
