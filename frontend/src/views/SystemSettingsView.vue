<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import {
  NCard, NForm, NFormItem, NInputNumber, NSwitch, NButton, NSpace,
  NDivider, NPopconfirm, NTag, NTooltip, NSpin, NGrid, NGi, useMessage
} from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
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

async function handleClearAllLogs(): Promise<void> {
  const result = await systemApi.clearUsageLogs(true)
  message.success(`已清空 ${result.deletedCount} 条日志`)
  await loadSettings()
}

onMounted(loadSettings)
</script>

<template>
  <div class="page-container">
    <PageHeader title="系统设置" subtitle="配置检测、代理、日志保留与危险操作" />
    <NSpin :show="loading">
      <NCard>
        <NSpace vertical :size="0">
          <!-- 代理配置（2列） -->
          <h3 class="section-title">代理转发</h3>
          <NForm label-placement="left" :label-width="200">
            <NGrid :cols="2" :x-gap="24" responsive="screen" item-responsive>
              <NGi span="2 l:1">
                <NFormItem>
                  <template #label>代理超时（秒）<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>单次代理转发请求等待上游响应的最长时间。值过小容易让慢响应被截断，值过大则会拉长失败请求的等待时间。</NTooltip></template>
                  <NInputNumber v-model:value="form.proxyRequestTimeoutSeconds" :min="5" :step="5" />
                </NFormItem>
              </NGi>
              <NGi span="2 l:1">
                <NFormItem>
                  <template #label>代理重试次数<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>代理请求在当前路由失败后允许重新尝试的次数。值越大，越有机会在临时故障时自动切到其他路由。</NTooltip></template>
                  <NInputNumber v-model:value="form.proxyRetryCount" :min="0" :max="5" />
                </NFormItem>
              </NGi>
            </NGrid>
            <NFormItem>
              <template #label>并发打满策略<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>"跳过"直接尝试下一顺位路由，适合重视速度的场景；"排队等待"先等待并发槽位释放，适合重视命中当前站点的场景。</NTooltip></template>
              <NSpace>
                <NSwitch :value="form.concurrencyMode === 1" @update:value="(v: boolean) => (form.concurrencyMode = v ? 1 : 0)" />
                {{ form.concurrencyMode === 1 ? '排队等待' : '跳到下一顺位' }}
              </NSpace>
            </NFormItem>
            <NFormItem v-if="form.concurrencyMode === 1">
              <template #label>排队超时（秒）<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>仅在"排队等待"模式下生效。决定请求最多等待多久获取并发槽位，超时后顺延到下一顺位。</NTooltip></template>
              <NInputNumber v-model:value="form.concurrencyQueueTimeoutSeconds" :min="10" :step="30" />
            </NFormItem>
          </NForm>

          <NDivider />

          <!-- 熔断（2列） -->
          <h3 class="section-title">熔断保护</h3>
          <NForm label-placement="left" :label-width="200">
            <NGrid :cols="2" :x-gap="24" responsive="screen" item-responsive>
              <NGi span="2 l:1">
                <NFormItem>
                  <template #label>熔断失败阈值<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>同一路由连续失败多少次后进入熔断。数值越小越敏感，越容易快速绕开故障站点。</NTooltip></template>
                  <NInputNumber v-model:value="form.circuitBreakerFailureThreshold" :min="1" />
                </NFormItem>
              </NGi>
              <NGi span="2 l:1">
                <NFormItem>
                  <template #label>恢复时间（分钟）<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>路由熔断后等待多久才允许再次尝试恢复。时间越短恢复越快。</NTooltip></template>
                  <NInputNumber v-model:value="form.circuitBreakerRecoveryMinutes" :min="1" />
                </NFormItem>
              </NGi>
            </NGrid>
          </NForm>

          <NDivider />

          <!-- 检测（3列） -->
          <h3 class="section-title">模型检测</h3>
          <NForm label-placement="left" :label-width="200">
            <NGrid :cols="3" :x-gap="24" responsive="screen" item-responsive>
              <NGi span="3 m:1 l:1">
                <NFormItem>
                  <template #label>检测超时（秒）<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>单次检测请求在上游等待响应的最长时间。数值过小可能把慢站点误判为失败。</NTooltip></template>
                  <NInputNumber v-model:value="form.detectionRequestTimeoutSeconds" :min="5" :step="5" />
                </NFormItem>
              </NGi>
              <NGi span="3 m:1 l:1">
                <NFormItem>
                  <template #label>检测重试次数<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>单个检测映射失败后重新尝试的次数。值越大越容易排除偶发网络抖动。</NTooltip></template>
                  <NInputNumber v-model:value="form.detectionRetryCount" :min="0" :max="5" />
                </NFormItem>
              </NGi>
              <NGi span="3 m:1 l:1">
                <NFormItem>
                  <template #label>检测并发数<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>同一时刻并行执行的检测数量。值越大整轮检测越快完成。</NTooltip></template>
                  <NInputNumber v-model:value="form.detectionConcurrency" :min="1" :max="20" />
                </NFormItem>
              </NGi>
            </NGrid>
          </NForm>

          <NDivider />

          <!-- 日志 -->
          <h3 class="section-title">日志与清理</h3>
          <NForm label-placement="left" :label-width="200">
            <NGrid :cols="2" :x-gap="24" responsive="screen" item-responsive>
              <NGi span="2 l:1">
                <NFormItem label="保留天数">
                  <NInputNumber v-model:value="form.usageLogRetentionDays" :min="1" :max="365" />
                </NFormItem>
              </NGi>
              <NGi span="2 l:1">
                <NFormItem label="自动清理">
                  <NSwitch v-model:value="form.usageLogAutoCleanupEnabled" />
                </NFormItem>
              </NGi>
            </NGrid>
            <NFormItem label="上次清理">
              <span v-if="form.lastUsageLogPrunedAt">{{ form.lastUsageLogPrunedAt }} · 删除 {{ form.lastUsageLogPrunedCount }} 条</span>
              <NTag v-else size="small" :bordered="false">未执行过</NTag>
            </NFormItem>
            <NFormItem label="清空全部日志">
              <NPopconfirm @positive-click="handleClearAllLogs">
                <template #trigger><NButton type="error" quaternary>立即清空</NButton></template>
                确认清空全部使用日志？此操作不可恢复。
              </NPopconfirm>
            </NFormItem>
          </NForm>

          <NDivider />

          <!-- 功能开关 -->
          <h3 class="section-title">功能开关</h3>
          <NForm label-placement="left" :label-width="200">
            <NFormItem>
              <template #label>开发者功能<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>开启后显示调试工具入口，保留最近100条内存中的调用轨迹。</NTooltip></template>
              <NSwitch v-model:value="form.developerFeaturesEnabled" />
            </NFormItem>
            <NFormItem>
              <template #label>对话记录功能<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>开启后显示对话记录界面，并允许写入结构化对话记录。</NTooltip></template>
              <NSwitch v-model:value="form.conversationLogEnabled" />
            </NFormItem>
            <NFormItem>
              <template #label>Codex 功能<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>总开关，控制 Codex OAuth 账号、凭证导入、额度与巡检功能。关闭后隐藏 Codex 页面并把所有 Codex 托管站点置为禁用。</NTooltip></template>
              <NSwitch v-model:value="form.codexFeaturesEnabled" />
            </NFormItem>
          </NForm>

          <template v-if="form.codexFeaturesEnabled">
            <NDivider />
            <h3 class="section-title">Codex 巡检<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>仅在 Codex 功能总开关开启时生效。巡检周期性检查各账号额度并自动禁用超额账号。</NTooltip></h3>
            <NForm label-placement="left" :label-width="200">
              <NGrid :cols="3" :x-gap="24" responsive="screen" item-responsive>
                <NGi span="3 m:1 l:1">
                  <NFormItem label="启用巡检">
                    <NSwitch v-model:value="form.codexInspectionEnabled" />
                  </NFormItem>
                </NGi>
                <NGi span="3 m:1 l:1">
                  <NFormItem>
                    <template #label>巡检周期（分钟）<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>每隔多少分钟执行一轮自动巡检，下限5分钟。</NTooltip></template>
                    <NInputNumber v-model:value="form.codexInspectionIntervalMinutes" :min="5" :step="5" />
                  </NFormItem>
                </NGi>
                <NGi span="3 m:1 l:1">
                  <NFormItem>
                    <template #label>额度缓存（小时）<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>未使用的账号可命中缓存，但超过该小时数后会强制真实刷新一次额度。</NTooltip></template>
                    <NInputNumber v-model:value="form.codexQuotaMaxCacheHours" :min="1" :max="72" />
                  </NFormItem>
                </NGi>
              </NGrid>
              <NFormItem>
                <template #label>自动禁用阈值（%）<NTooltip trigger="hover" placement="top"><template #trigger><span class="tip-icon">?</span></template>当账号额度达到该使用百分比时自动禁用。建议95。</NTooltip></template>
                <NInputNumber v-model:value="form.codexAutoDisableThresholdPercent" :min="1" :max="100" />
              </NFormItem>
            </NForm>
          </template>

          <NDivider />

          <NSpace justify="end">
            <NButton type="primary" :loading="saving" @click="handleSave">保存设置</NButton>
          </NSpace>
        </NSpace>
      </NCard>
    </NSpin>
  </div>
</template>

<style scoped>
.section-title {
  margin: 0 0 16px;
  font-size: 16px;
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 6px;
}
.tip-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  background: #E2E8F0;
  color: #64748B;
  font-size: 11px;
  cursor: help;
  line-height: 1;
  font-weight: 400;
  flex-shrink: 0;
  margin-left: 2px;
}
[data-theme='dark'] .tip-icon {
  background: #2D2D33;
  color: rgba(255, 255, 255, 0.45);
}
</style>
