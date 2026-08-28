<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NTabPane, NTabs } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import ModelHealthView from './ModelHealthView.vue'
import CircuitBreakerTab from './CircuitBreakerTab.vue'
import ConcurrencyMonitorTab from './ConcurrencyMonitorTab.vue'

export type ModelHealthTab = 'health' | 'circuit-breaker' | 'concurrency'

const route = useRoute()
const router = useRouter()
const activeTab = ref<ModelHealthTab>(getTabFromHash())

function getTabFromHash(): ModelHealthTab {
  const hash = route.hash.replace(/^#/, '').toLowerCase()
  if (hash === 'circuit-breaker' || hash === 'concurrency') {
    return hash as ModelHealthTab
  }
  // 兼容旧 query.tab
  const queryTab = route.query.tab as string
  if (queryTab === 'circuit-breaker' || queryTab === 'concurrency') {
    return queryTab as ModelHealthTab
  }
  return 'health'
}

function handleTabChange(value: string): void {
  const nextTab = value as ModelHealthTab
  activeTab.value = nextTab
  const nextHash = nextTab === 'health' ? '#health' : `#${nextTab}`
  if (route.hash !== nextHash) {
    void router.replace({ hash: nextHash })
  }
}

// 支持浏览器前进/后退，以及从外部/旧地址跳转时同步当前页签。
watch(() => route.hash, () => {
  activeTab.value = getTabFromHash()
})
watch(() => route.query.tab, () => {
  activeTab.value = getTabFromHash()
})
</script>

<template>
  <div class="page-container model-health-management-page">
    <PageHeader
      title="模型健康"
      subtitle="监控模型健康可用性、实时熔断状态与站点并发负载"
    />

    <NTabs v-model:value="activeTab" type="line" animated @update:value="handleTabChange">
      <NTabPane name="health" tab="模型健康">
        <ModelHealthView embedded />
      </NTabPane>
      <NTabPane name="circuit-breaker" tab="熔断监控">
        <CircuitBreakerTab />
      </NTabPane>
      <NTabPane name="concurrency" tab="实时并发">
        <ConcurrencyMonitorTab embedded />
      </NTabPane>
    </NTabs>
  </div>
</template>

<style scoped>
.model-health-management-page {
  min-width: 0;
}
</style>
