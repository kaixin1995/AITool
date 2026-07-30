<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import {
  NCard, NForm, NFormItem, NInputNumber, NSwitch, NButton, NSpace,
  NPopconfirm, NTag, NTooltip, NSpin, NSelect, useMessage
} from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import * as systemApi from '@/api/system'
import type { SystemSettings } from '@/api/system'

const message = useMessage()
const loading = ref(false)
const saving = ref(false)

const clearLogsForm = reactive({
  source: null as string | null,
  startTime: '',
  endTime: ''
})

const sourceOptions = [
  { label: '全部来源', value: '' },
  { label: '代理', value: 'proxy' },
  { label: '对话测试', value: 'chat' },
  { label: 'Claude Code', value: 'claude-code' },
  { label: 'Codex', value: 'codex' },
  { label: 'Open Code', value: 'open-code' },
  { label: 'ZCode', value: 'zcode' },
  { label: '手动检测', value: 'detection-manual' },
  { label: '定时检测', value: 'detection-task' }
]

const clearLogsScopeText = computed(() => {
  const parts = []
  if (clearLogsForm.source) parts.push(`来源 ${clearLogsForm.source}`)
  if (clearLogsForm.startTime) parts.push(`从 ${clearLogsForm.startTime}`)
  if (clearLogsForm.endTime) parts.push(`到 ${clearLogsForm.endTime}`)
  return parts.length ? parts.join('，') : '全部 UsageLogs'
})

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
  } catch (e) {
    message.error((e as Error).message)
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

async function handleClearFilteredLogs(): Promise<void> {
  const result = await systemApi.clearUsageLogs(false, {
    source: clearLogsForm.source || undefined,
    startTime: clearLogsForm.startTime ? new Date(clearLogsForm.startTime).toISOString() : undefined,
    endTime: clearLogsForm.endTime ? new Date(clearLogsForm.endTime).toISOString() : undefined
  })
  message.success(`已清空 ${result.deletedCount} 条日志`)
  await loadSettings()
}

async function handleClearAllLogs(): Promise<void> {
  const result = await systemApi.clearUsageLogs(true)
  message.success(`已清空 ${result.deletedCount} 条日志`)
  await loadSettings()
}

onMounted(loadSettings)
</script>

<template>
  <div class="page-container settings-page">
    <PageHeader title="系统设置" subtitle="配置检测、代理、日志保留与危险操作" />
    <NSpin :show="loading">
      <div class="settings-stack">
        <NCard title="检测设置" class="settings-card">
          <NForm label-placement="top">
            <div class="settings-grid cols-3">
              <NFormItem>
                <template #label><span class="form-label-tip">检测超时时间（秒）<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>单次检测请求在上游站点等待响应的最长时间。数值过小可能把慢站点误判为失败。</NTooltip></span></template>
                <NInputNumber v-model:value="form.detectionRequestTimeoutSeconds" :min="1" :step="5" />
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">检测重试次数<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>单个检测映射失败后重新尝试的次数。值越大越容易排除偶发网络抖动。</NTooltip></span></template>
                <NInputNumber v-model:value="form.detectionRetryCount" :min="0" :max="5" />
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">检测并发数<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>同一时刻并行执行的检测数量。值越大整轮检测越快完成。</NTooltip></span></template>
                <NInputNumber v-model:value="form.detectionConcurrency" :min="1" :max="20" />
              </NFormItem>
            </div>
          </NForm>
        </NCard>

        <NCard title="代理设置" class="settings-card">
          <NForm label-placement="top">
            <div class="settings-grid cols-4">
              <NFormItem>
                <template #label><span class="form-label-tip">代理超时时间（秒）<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>单次代理转发请求等待上游响应的最长时间。值过小容易让慢响应被截断。</NTooltip></span></template>
                <NInputNumber v-model:value="form.proxyRequestTimeoutSeconds" :min="1" :step="5" />
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">代理重试次数<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>当前路由失败后允许重新尝试的次数。值越大越有机会切到其他路由。</NTooltip></span></template>
                <NInputNumber v-model:value="form.proxyRetryCount" :min="0" :max="5" />
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">熔断失败阈值<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>同一路由连续失败多少次后进入熔断。数值越小越敏感。</NTooltip></span></template>
                <NInputNumber v-model:value="form.circuitBreakerFailureThreshold" :min="1" />
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">熔断恢复时间（分钟）<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>路由熔断后等待多久才允许再次尝试恢复。</NTooltip></span></template>
                <NInputNumber v-model:value="form.circuitBreakerRecoveryMinutes" :min="1" />
              </NFormItem>
            </div>
            <div class="settings-grid cols-3 compact-row">
              <NFormItem>
                <template #label><span class="form-label-tip">并发打满策略<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>“跳过”直接尝试下一顺位路由；“排队等待”先等待并发槽位释放。</NTooltip></span></template>
                <NSpace align="center">
                  <NSwitch :value="form.concurrencyMode === 1" @update:value="(v: boolean) => (form.concurrencyMode = v ? 1 : 0)" />
                  <span>{{ form.concurrencyMode === 1 ? '排队等待' : '跳过 → 尝试下一顺位模型' }}</span>
                </NSpace>
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">排队等待超时（秒）<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>仅在“排队等待”模式下生效。超时后顺延到下一顺位。</NTooltip></span></template>
                <NInputNumber v-model:value="form.concurrencyQueueTimeoutSeconds" :min="1" :step="30" />
              </NFormItem>
            </div>
          </NForm>
        </NCard>

        <NCard title="日志设置" class="settings-card">
          <NForm label-placement="top">
            <div class="settings-grid cols-3">
              <NFormItem label="UsageLogs 保留天数">
                <NInputNumber v-model:value="form.usageLogRetentionDays" :min="1" :max="365" />
              </NFormItem>
              <NFormItem label="自动清理">
                <NSpace align="center"><NSwitch v-model:value="form.usageLogAutoCleanupEnabled" />启用自动清理</NSpace>
              </NFormItem>
              <NFormItem label="最近一次自动清理数量">
                <NTag size="small" :bordered="false">{{ form.lastUsageLogPrunedCount }} 条</NTag>
              </NFormItem>
            </div>
          </NForm>
        </NCard>

        <NCard title="开发者功能" class="settings-card">
          <div class="switch-stack">
            <label class="switch-line"><NSwitch v-model:value="form.developerFeaturesEnabled" /><span class="form-label-tip">启用开发者功能<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>开启后显示调试工具入口，并保留最近 100 条调用轨迹。</NTooltip></span></label>
            <label class="switch-line"><NSwitch v-model:value="form.conversationLogEnabled" /><span class="form-label-tip">启用对话记录功能<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>开启后显示对话记录界面，并允许写入结构化对话记录。</NTooltip></span></label>
            <label class="switch-line"><NSwitch v-model:value="form.codexFeaturesEnabled" /><span class="form-label-tip">启用 Codex 功能<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>总开关，控制 Codex OAuth 账号、凭证导入、额度与巡检功能。</NTooltip></span></label>
          </div>
        </NCard>

        <NCard class="settings-card">
          <template #header>
            <span class="form-label-tip section-heading">Codex 巡检<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>仅在 Codex 功能总开关开启时生效。巡检会周期性检查各 Codex 账号额度。</NTooltip></span>
          </template>
          <NForm label-placement="top">
            <div class="settings-grid cols-4">
              <NFormItem label="启用自动巡检">
                <NSwitch v-model:value="form.codexInspectionEnabled" :disabled="!form.codexFeaturesEnabled" />
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">巡检周期（分钟）<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>每隔多少分钟执行一轮自动巡检，下限 5 分钟。</NTooltip></span></template>
                <NInputNumber v-model:value="form.codexInspectionIntervalMinutes" :min="5" :step="5" :disabled="!form.codexFeaturesEnabled" />
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">额度缓存最大小时数<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>未被使用的账号可以命中缓存，但超过该小时数后会强制真实刷新一次额度。</NTooltip></span></template>
                <NInputNumber v-model:value="form.codexQuotaMaxCacheHours" :min="1" :max="72" :disabled="!form.codexFeaturesEnabled" />
              </NFormItem>
              <NFormItem>
                <template #label><span class="form-label-tip">自动禁用阈值（%）<NTooltip trigger="hover"><template #trigger><span class="tip-icon">?</span></template>当账号额度达到该使用百分比时自动禁用，建议 95。</NTooltip></span></template>
                <NInputNumber v-model:value="form.codexAutoDisableThresholdPercent" :min="1" :max="100" :disabled="!form.codexFeaturesEnabled" />
              </NFormItem>
            </div>
          </NForm>
        </NCard>

        <NSpace justify="end">
          <NButton type="primary" :loading="saving" @click="handleSave">保存设置</NButton>
        </NSpace>

        <NCard title="危险操作" class="settings-card danger-card">
          <p class="danger-copy">手动清空 UsageLogs，并同步清空 Analytics 统计缓存。留空表示不限制该条件；来源和日期都不填时会清空全部 UsageLogs。</p>
          <div class="clear-logs-grid">
            <label class="danger-field">
              <span>来源</span>
              <NSelect v-model:value="clearLogsForm.source" :options="sourceOptions" clearable placeholder="全部来源" />
            </label>
            <label class="danger-field">
              <span>开始时间</span>
              <input v-model="clearLogsForm.startTime" class="danger-time-input" type="datetime-local" />
            </label>
            <label class="danger-field">
              <span>结束时间</span>
              <input v-model="clearLogsForm.endTime" class="danger-time-input" type="datetime-local" />
            </label>
          </div>
          <NSpace class="danger-actions">
            <NPopconfirm @positive-click="handleClearFilteredLogs">
              <template #trigger><NButton type="error" secondary>清空 UsageLogs</NButton></template>
              确认清空符合条件的 UsageLogs 吗？范围：{{ clearLogsScopeText }}。
            </NPopconfirm>
            <NPopconfirm @positive-click="handleClearAllLogs">
              <template #trigger><NButton type="error">清空全部</NButton></template>
              确认直接清空全部 UsageLogs 吗？这会同步清空 Analytics 统计缓存。
            </NPopconfirm>
          </NSpace>
        </NCard>
      </div>
    </NSpin>
  </div>
</template>

<style scoped>
.settings-page {
  min-width: 0;
}

.settings-stack {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.settings-card {
  min-width: 0;
}

.settings-grid {
  display: grid;
  gap: 16px 24px;
  align-items: end;
}

.settings-grid.cols-3 {
  grid-template-columns: repeat(3, minmax(0, 1fr));
}

.settings-grid.cols-4 {
  grid-template-columns: repeat(4, minmax(0, 1fr));
}

.compact-row {
  margin-top: 12px;
}

.form-label-tip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
}

.section-heading {
  font-weight: 600;
}

.tip-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border: 1.5px solid #6c757d;
  border-radius: 50%;
  color: #6c757d;
  font-size: 11px;
  font-weight: 700;
  line-height: 1;
  cursor: help;
  flex-shrink: 0;
}

.switch-stack {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.switch-line {
  display: inline-flex;
  align-items: center;
  gap: 10px;
}

.danger-card {
  border-color: rgba(220, 38, 38, 0.45);
}

.danger-copy {
  margin: 0 0 12px;
  color: var(--text-color-secondary);
}

.clear-logs-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 16px;
  margin-bottom: 14px;
}

.danger-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
  color: var(--text-color-secondary);
  font-size: 13px;
}

.danger-time-input {
  height: 34px;
  min-width: 0;
  padding: 0 10px;
  border: 1px solid var(--border-color-global);
  border-radius: 4px;
  background: var(--bg-card);
  color: var(--text-primary);
}

.danger-actions {
  flex-wrap: wrap;
}

[data-theme='dark'] .tip-icon {
  color: rgba(255, 255, 255, 0.55);
  border-color: rgba(255, 255, 255, 0.55);
}

@media (max-width: 1100px) {
  .settings-grid.cols-4,
  .settings-grid.cols-3 {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

@media (max-width: 640px) {
  .settings-grid.cols-4,
  .settings-grid.cols-3,
  .clear-logs-grid {
    grid-template-columns: 1fr;
  }
}
</style>
