<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NTabPane, NTabs } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import ModelHealthView from './ModelHealthView.vue'
import RouteFallbackView from './RouteFallbackView.vue'

type ModelHealthTab = 'health' | 'fallback'

const route = useRoute()
const router = useRouter()
const activeTab = ref<ModelHealthTab>(getTabFromQuery())

function getTabFromQuery(): ModelHealthTab {
  return route.query.tab === 'fallback' ? 'fallback' : 'health'
}

function handleTabChange(value: string): void {
  const nextTab: ModelHealthTab = value === 'fallback' ? 'fallback' : 'health'
  activeTab.value = nextTab
  const query = { ...route.query }
  if (nextTab === 'fallback') {
    query.tab = 'fallback'
  } else {
    delete query.tab
  }
  void router.replace({ query })
}

// 支持浏览器前进/后退，以及从旧路由回退地址进入时同步当前页签。
watch(() => route.query.tab, () => {
  activeTab.value = getTabFromQuery()
})
</script>

<template>
  <div class="page-container model-health-management-page">
    <PageHeader title="模型健康" subtitle="监控模型健康状态，并查看路由回退事件" />

    <NTabs v-model:value="activeTab" type="line" animated @update:value="handleTabChange">
      <NTabPane name="health" tab="模型健康">
        <ModelHealthView embedded />
      </NTabPane>
      <NTabPane name="fallback" tab="路由回退">
        <RouteFallbackView embedded />
      </NTabPane>
    </NTabs>
  </div>
</template>
