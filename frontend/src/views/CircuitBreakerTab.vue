<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { NCard, NButton, NSpace, NTag, NEmpty, NPopconfirm, useMessage } from 'naive-ui'
import * as api from '@/api/circuitBreaker'
import type { CircuitBreakerRoute } from '@/api/circuitBreaker'

const message = useMessage()
const loading = ref(false)
const routes = ref<CircuitBreakerRoute[]>([])
let timer: ReturnType<typeof setInterval> | null = null

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.getCircuitBreakerStates()
    routes.value = resp.routes ?? []
  } catch {
    // 功能开关关闭时静默
  } finally { loading.value = false }
}

async function handleReset(routeId: string): Promise<void> {
  try {
    await api.resetCircuitBreaker(routeId)
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

    <NEmpty v-if="routes.length === 0" description="当前无熔断或失败记录" />

    <div v-else>
      <div v-for="r in routes" :key="r.routeId" class="circuit-row">
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
        <NPopconfirm v-if="r.isBlocked || r.failureCount > 0" @positive-click="handleReset(r.routeId)">
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
