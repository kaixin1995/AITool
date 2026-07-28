<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import {
  NCard, NForm, NFormItem, NInputNumber, NSwitch, NButton, NSpace,
  NDivider, NPopconfirm, NTag, NTooltip, NSpin, useMessage
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
    <NSpin :show="loading">
      <NCard>
        <NSpace vertical :size="0">
          <!-- 代理配置 -->
          <h3 class="section-title">代理转发</h3>
          <NForm label-placement="left" :label-width="240">
            <NFormItem label="代理超时时间（秒）">
              <div class="field-with-tip">
                <NInputNumber v-model:value="form.proxyRequestTimeoutSeconds" :min="5" :step="5" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>单次代理转发请求等待上游响应的最长时间。它会影响外部客户端在发起请求后愿意等待多久，也会影响最终是否被判定为超时失败。值过小容易让慢响应被截断，值过大则会拉长失败请求的等待时间。</NTooltip>
              </div>
            </NFormItem>
            <NFormItem label="代理重试次数">
              <div class="field-with-tip">
                <NInputNumber v-model:value="form.proxyRetryCount" :min="0" :max="5" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>代理请求在当前路由失败后允许重新尝试的次数。它会影响故障转移的深度和最终请求成功率，也会影响一次外部调用的总耗时。值越大，越有机会在临时故障时自动切到其他路由，但请求链路也会更长。</NTooltip>
              </div>
            </NFormItem>
            <NFormItem label="并发打满策略">
              <div class="field-with-tip">
                <NSpace>
                  <NSwitch :value="form.concurrencyMode === 1" @update:value="(v: boolean) => (form.concurrencyMode = v ? 1 : 0)" />
                  {{ form.concurrencyMode === 1 ? '排队等待' : '跳到下一顺位' }}
                </NSpace>
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>当某个站点+模型的并发已经满了时，系统应该如何处理后续请求。选择“跳过”会直接尝试下一顺位路由，适合更重视响应速度的场景；选择“排队等待”会先等待并发槽位释放，适合更重视命中当前站点的场景。</NTooltip>
              </div>
            </NFormItem>
            <NFormItem v-if="form.concurrencyMode === 1" label="排队等待超时（秒）">
              <div class="field-with-tip">
                <NInputNumber v-model:value="form.concurrencyQueueTimeoutSeconds" :min="10" :step="30" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>仅在“排队等待”模式下生效。它决定请求最多愿意等待多久来获取并发槽位。超过这个时间后，系统会放弃当前路由并顺延到下一顺位模型；如果没有可用的下一顺位，则返回失败。这个等待时间不会占用代理请求的总体超时。</NTooltip>
              </div>
            </NFormItem>
          </NForm>

          <NDivider />

          <!-- 熔断 -->
          <h3 class="section-title">熔断保护</h3>
          <NForm label-placement="left" :label-width="240">
            <NFormItem label="熔断失败阈值">
              <div class="field-with-tip">
                <NInputNumber v-model:value="form.circuitBreakerFailureThreshold" :min="1" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>同一路由连续失败多少次后进入熔断。它会影响某个站点被临时屏蔽的触发速度，数值越小越敏感，越容易快速绕开故障站点；数值越大则越保守，更不容易误伤短暂波动的站点。</NTooltip>
              </div>
            </NFormItem>
            <NFormItem label="熔断恢复时间（分钟）">
              <div class="field-with-tip">
                <NInputNumber v-model:value="form.circuitBreakerRecoveryMinutes" :min="1" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>路由进入熔断后，等待多久才允许再次尝试恢复。它会影响被屏蔽站点的恢复速度，以及系统多久后会重新把该路由纳入可选列表。时间越短，恢复越快；时间越长，站点被屏蔽的持续时间越久。</NTooltip>
              </div>
            </NFormItem>
          </NForm>

          <NDivider />

          <!-- 检测 -->
          <h3 class="section-title">模型检测</h3>
          <NForm label-placement="left" :label-width="240">
            <NFormItem label="检测超时时间（秒）">
              <div class="field-with-tip">
                <NInputNumber v-model:value="form.detectionRequestTimeoutSeconds" :min="5" :step="5" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>单次检测请求在上游站点等待响应的最长时间。它会影响每个映射探测何时被判定为超时，也会影响整批检测任务的完成速度。数值过小可能把响应偏慢的站点误判为失败，数值过大则会让异常站点占用更久的检测资源。</NTooltip>
              </div>
            </NFormItem>
            <NFormItem label="检测重试次数">
              <div class="field-with-tip">
                <NInputNumber v-model:value="form.detectionRetryCount" :min="0" :max="5" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>单个检测映射在判定失败后会重新尝试的次数。它会影响检测结果的稳定性和最终成功率，也会影响整轮检测的总耗时。值越大，越容易排除偶发网络抖动；但同时也会增加上游请求次数和检测完成时间。</NTooltip>
              </div>
            </NFormItem>
            <NFormItem label="检测并发数">
              <div class="field-with-tip">
                <NInputNumber v-model:value="form.detectionConcurrency" :min="1" :max="20" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>同一时刻可以并行执行的检测数量。它会直接影响检测任务的吞吐速度，也会影响对上游站点和数据库的压力。值越大，整轮检测越快完成；值越小，单次占用更保守，但总耗时会更长。</NTooltip>
              </div>
            </NFormItem>
          </NForm>

          <NDivider />

          <!-- 日志 -->
          <h3 class="section-title">日志与清理</h3>
          <NForm label-placement="left" :label-width="240">
            <NFormItem label="UsageLogs 保留天数">
              <NInputNumber v-model:value="form.usageLogRetentionDays" :min="1" :max="365" />
            </NFormItem>
            <NFormItem label="自动清理">
              <NSwitch v-model:value="form.usageLogAutoCleanupEnabled" />
            </NFormItem>
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
          <NForm label-placement="left" :label-width="240">
            <NFormItem label="启用开发者功能">
              <div class="field-with-tip">
                <NSwitch v-model:value="form.developerFeaturesEnabled" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>开启后才会显示调试工具入口，包括调用调试、客户端模拟和开发者追踪页面。它也会让系统开始保留最近 100 条内存中的调用轨迹，方便排查代理请求、路由选择和协议转换过程。</NTooltip>
              </div>
            </NFormItem>
            <NFormItem label="启用对话记录功能">
              <div class="field-with-tip">
                <NSwitch v-model:value="form.conversationLogEnabled" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>开启后才会显示对话记录界面，并允许代理请求和对话测试继续写入结构化对话记录。关闭后不会再展示对话记录页签，也不会继续落库新的对话记录。</NTooltip>
              </div>
            </NFormItem>
            <NFormItem label="启用 Codex 功能">
              <div class="field-with-tip">
                <NSwitch v-model:value="form.codexFeaturesEnabled" />
                <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>总开关，控制 Codex OAuth 账号、凭证导入、额度与巡检功能。关闭后会隐藏 Codex 页面入口，并把所有 Codex 托管站点置为禁用（路由/模型/对话测试不再命中 Codex）。重新开启时仅恢复因总开关被禁用的账号，不影响被额度耗尽或手动禁用的账号。</NTooltip>
              </div>
            </NFormItem>
          </NForm>

          <template v-if="form.codexFeaturesEnabled">
            <NDivider />
            <h3 class="section-title">Codex 巡检</h3>
            <NForm label-placement="left" :label-width="240">
              <NFormItem label="启用巡检">
                <NSwitch v-model:value="form.codexInspectionEnabled" />
              </NFormItem>
              <NFormItem label="巡检周期（分钟）">
                <div class="field-with-tip">
                  <NInputNumber v-model:value="form.codexInspectionIntervalMinutes" :min="5" :step="5" />
                  <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>每隔多少分钟执行一轮自动巡检，下限 5 分钟。</NTooltip>
                </div>
              </NFormItem>
              <NFormItem label="额度缓存最大小时数">
                <div class="field-with-tip">
                  <NInputNumber v-model:value="form.codexQuotaMaxCacheHours" :min="1" :max="72" />
                  <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>未被使用的账号可以命中缓存，但超过该小时数后会强制真实刷新一次额度。</NTooltip>
                </div>
              </NFormItem>
              <NFormItem label="自动禁用阈值（%）">
                <div class="field-with-tip">
                  <NInputNumber v-model:value="form.codexAutoDisableThresholdPercent" :min="1" :max="100" />
                  <NTooltip trigger="hover" placement="top" style="margin-left:4px"><template #trigger><span class="tip-icon">?</span></template>当账号额度达到该使用百分比时自动禁用。判断规则：优先看 5 小时窗口，没有 5 小时窗口才看周窗口。建议 95。</NTooltip>
                </div>
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
}
.field-with-tip {
  display: flex;
  align-items: center;
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
}
[data-theme='dark'] .tip-icon {
  background: #2D2D33;
  color: rgba(255, 255, 255, 0.45);
}
</style>
