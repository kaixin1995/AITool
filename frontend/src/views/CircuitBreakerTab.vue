<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NEmpty, NAlert, NPopconfirm, useMessage } from 'naive-ui'
import * as api from '@/api/circuitBreaker'
import type { CircuitBreakerRoute } from '@/api/circuitBreaker'

const message = useMessage()
const loading = ref(false)
const routes = ref<CircuitBreakerRoute[]>([])
const loadError = ref('')
let timer: ReturnType<typeof setInterval> | null = null

async function load(): Promise<void> {
  if (loading.value) return
  loading.value = true
  try {
    const resp = await api.getCircuitBreakerStates()
    routes.value = resp.routes ?? []
    loadError.value = ''
  } catch (e) {
    loadError.value = (e as Error).message || '加载熔断状态失败，请检查开发者功能和登录状态'
  } finally { loading.value = false }
}

async function handleReset(circuitKey: string): Promise<void> {
  try {
    await api.resetCircuitBreaker(circuitKey)
    message.success('已解除熔断')
    await load()
  } catch (e) { message.error((e as Error).message) }
}

async function handleResetAll(): Promise<void> {
  try {
    const resp = await api.resetAllCircuitBreakers()
    message.success(`已解除 ${resp.resetCount} 条路由的熔断`)
    await load()
  } catch (e) { message.error((e as Error).message) }
}

function formatRemaining(seconds: number | null): string {
  if (seconds == null) return '-'
  if (seconds < 60) return `${seconds}s`
  return `${Math.floor(seconds / 60)}m ${seconds % 60}s`
}

onMounted(() => {
  load()
  timer = setInterval(() => {
    if (document.visibilityState === 'visible') load()
  }, 5000)
})
onUnmounted(() => { if (timer) clearInterval(timer) })
</script>

<template>
  <NCard>
    <template #header>
      <NSpace justify="space-between" align="center">
        <span>熔断状态监控</span>
        <NSpace :size="8">
          <NButton size="small" @click="load">刷新</NButton>
          <NPopconfirm v-if="routes.some(r => r.isBlocked)" @positive-click="handleResetAll">
            <template #trigger><NButton size="small" type="warning">解除全部熔断</NButton></template>
            确认解除所有路由的熔断状态？
          </NPopconfirm>
        </NSpace>
      </NSpace>
    </template>

    <NAlert v-if="loadError" type="error" class="mb-3">加载失败：{{ loadError }}。当前显示可能不是最新数据。</NAlert>
    <NAlert type="info" class="mb-3">这里只显示当前熔断、冷却或连续失败计数，不是历史失败列表。同一熔断键成功后连续失败计数会清零；进程重启也会清空内存状态。历史中断请查看使用日志和诊断抓包。</NAlert>
    <NEmpty v-if="!loadError && routes.length === 0" description="当前没有熔断、冷却或正在累计的连续失败" />

    <div v-else-if="routes.length > 0">
      <div v-for="r in routes" :key="r.circuitKey" class="circuit-row">
        <NSpace align="center" :size="8" style="flex: 1">
          <NTag size="small" :type="r.isBlocked ? 'error' : 'warning'" :bordered="false">
            {{ r.isBlocked ? '已熔断' : '失败累计' }}
          </NTag>
          <span style="min-width: 120px; font-weight: 600">{{ r.entryName }}</span>
          <NTag size="small" :bordered="false">{{ r.upstreamModelName }}</NTag>
          <span style="font-size: 13px; color: var(--text-color-secondary)">{{ r.siteName }}</span>
          <span style="font-size: 13px;">失败 {{ r.failureCount }} 次</span>
          <span v-if="r.isBlocked && r.remainingSeconds != null" style="font-size: 13px; color: #F87171">
            剩余 {{ formatRemaining(r.remainingSeconds) }}
          </span>
        </NSpace>
        <NPopconfirm v-if="r.isBlocked || r.failureCount > 0" @positive-click="handleReset(r.circuitKey)">
          <template #trigger><NButton size="tiny" quaternary type="warning">解除</NButton></template>
          确认解除该路由的熔断/失败计数？
        </NPopconfirm>
      </div>
    </div>
  </NCard>
</template>

<style scoped>
.circuit-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 10px 12px;
  border-radius: 8px;
  margin-bottom: 4px;
}
.circuit-row:hover { background: rgba(108, 158, 255, 0.06); }
</style>
