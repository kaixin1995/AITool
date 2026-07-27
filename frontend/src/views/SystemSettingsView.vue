<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import {
  NCard, NForm, NFormItem, NInputNumber, NSwitch, NButton, NSpace,
  NDivider, NPopconfirm, NTag, useMessage
} from 'naive-ui'
import * as systemApi from '@/api/system'
import type { SystemSettings } from '@/api/system'

const message = useMessage()
const loading = ref(false)
const saving = ref(false)

const form = reactive<SystemSettings>({
  proxyRequestTimeoutSeconds: 60,
  proxyRetryCount: 1,
  detectionRequestTimeoutSeconds: 60,
  detectionRetryCount: 0,
  detectionConcurrency: 1,
  circuitBreakerFailureThreshold: 5,
  circuitBreakerRecoveryMinutes: 2,
  usageLogRetentionDays: 7,
  usageLogAutoCleanupEnabled: true,
  developerFeaturesEnabled: false,
  conversationLogEnabled: true,
  concurrencyMode: 0,
  concurrencyQueueTimeoutSeconds: 120,
  codexFeaturesEnabled: false,
  codexInspectionEnabled: false,
  codexInspectionIntervalMinutes: 30,
  codexQuotaMaxCacheHours: 6,
  codexAutoDisableThresholdPercent: 95,
  lastUsageLogPrunedAt: null,
  lastUsageLogPrunedCount: 0
})

async function loadSettings(): Promise<void> {
  loading.value = true
  try {
    const s = await systemApi.getSystemSettings()
    Object.assign(form, s)
  } finally {
    loading.value = false
  }
}

async function handleSave(): Promise<void> {
  saving.value = true
  try {
    await systemApi.updateSystemSettings(form)
    message.success('设置已保存')
  } finally {
    saving.value = false
  }
}

async function handleClearAllLogs(): Promise<void> {
  const result = await systemApi.clearUsageLogs(true)
  message.success(`已清空 ${result.deletedCount} 条日志`)
  await loadSettings()
}

onMounted(loadSettings)
</script>

<template>
  <div class="page-container">
    <NCard :loading="loading">
      <NSpace vertical :size="0">
        <!-- 代理配置 -->
        <h3 class="section-title">代理转发</h3>
        <NForm label-placement="left" :label-width="200">
          <NFormItem label="请求超时（秒）">
            <NInputNumber v-model:value="form.proxyRequestTimeoutSeconds" :min="5" :step="5" />
          </NFormItem>
          <NFormItem label="重试次数">
            <NInputNumber v-model:value="form.proxyRetryCount" :min="0" :max="5" />
          </NFormItem>
          <NFormItem label="并发打满策略">
            <NSpace>
              <NSwitch
                :value="form.concurrencyMode === 1"
                @update:value="(v: boolean) => (form.concurrencyMode = v ? 1 : 0)"
              />
              {{ form.concurrencyMode === 1 ? '排队等待' : '跳到下一顺位' }}
            </NSpace>
          </NFormItem>
          <NFormItem v-if="form.concurrencyMode === 1" label="排队超时（秒）">
            <NInputNumber v-model:value="form.concurrencyQueueTimeoutSeconds" :min="10" :step="30" />
          </NFormItem>
        </NForm>

        <NDivider />

        <!-- 熔断 -->
        <h3 class="section-title">熔断保护</h3>
        <NForm label-placement="left" :label-width="200">
          <NFormItem label="连续失败阈值">
            <NInputNumber v-model:value="form.circuitBreakerFailureThreshold" :min="1" />
          </NFormItem>
          <NFormItem label="恢复时间（分钟）">
            <NInputNumber v-model:value="form.circuitBreakerRecoveryMinutes" :min="1" />
          </NFormItem>
        </NForm>

        <NDivider />

        <!-- 检测 -->
        <h3 class="section-title">模型检测</h3>
        <NForm label-placement="left" :label-width="200">
          <NFormItem label="检测超时（秒）">
            <NInputNumber v-model:value="form.detectionRequestTimeoutSeconds" :min="5" :step="5" />
          </NFormItem>
          <NFormItem label="检测重试次数">
            <NInputNumber v-model:value="form.detectionRetryCount" :min="0" :max="5" />
          </NFormItem>
          <NFormItem label="检测并发数">
            <NInputNumber v-model:value="form.detectionConcurrency" :min="1" :max="20" />
          </NFormItem>
        </NForm>

        <NDivider />

        <!-- 日志 -->
        <h3 class="section-title">日志与清理</h3>
        <NForm label-placement="left" :label-width="200">
          <NFormItem label="日志保留天数">
            <NInputNumber v-model:value="form.usageLogRetentionDays" :min="1" :max="365" />
          </NFormItem>
          <NFormItem label="自动清理">
            <NSwitch v-model:value="form.usageLogAutoCleanupEnabled" />
          </NFormItem>
          <NFormItem label="对话记录">
            <NSwitch v-model:value="form.conversationLogEnabled" />
          </NFormItem>
          <NFormItem label="上次清理">
            <span v-if="form.lastUsageLogPrunedAt">
              {{ form.lastUsageLogPrunedAt }} · 删除 {{ form.lastUsageLogPrunedCount }} 条
            </span>
            <NTag v-else size="small" :bordered="false">未执行过</NTag>
          </NFormItem>
          <NFormItem label="清空全部日志">
            <NPopconfirm @positive-click="handleClearAllLogs">
              <template #trigger>
                <NButton type="error" quaternary>立即清空</NButton>
              </template>
              确认清空全部使用日志？此操作不可恢复。
            </NPopconfirm>
          </NFormItem>
        </NForm>

        <NDivider />

        <!-- 功能开关 -->
        <h3 class="section-title">功能开关</h3>
        <NForm label-placement="left" :label-width="200">
          <NFormItem label="开发者功能">
            <NSwitch v-model:value="form.developerFeaturesEnabled" />
          </NFormItem>
          <NFormItem label="Codex 功能">
            <NSwitch v-model:value="form.codexFeaturesEnabled" />
          </NFormItem>
          <NFormItem v-if="form.codexFeaturesEnabled" label="Codex 巡检">
            <NSwitch v-model:value="form.codexInspectionEnabled" />
          </NFormItem>
          <NFormItem v-if="form.codexFeaturesEnabled" label="巡检周期（分钟）">
            <NInputNumber v-model:value="form.codexInspectionIntervalMinutes" :min="5" :step="5" />
          </NFormItem>
          <NFormItem v-if="form.codexFeaturesEnabled" label="自动禁用阈值（%）">
            <NInputNumber v-model:value="form.codexAutoDisableThresholdPercent" :min="1" :max="100" />
          </NFormItem>
        </NForm>

        <NDivider />

        <NSpace justify="end">
          <NButton type="primary" :loading="saving" @click="handleSave">保存设置</NButton>
        </NSpace>
      </NSpace>
    </NCard>
  </div>
</template>

<style scoped>
.section-title {
  margin: 0 0 16px;
  font-size: 16px;
  font-weight: 600;
}
</style>
