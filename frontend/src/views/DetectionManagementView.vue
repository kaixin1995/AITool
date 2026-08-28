<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NTabPane, NTabs } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import DetectionView from './DetectionView.vue'
import DetectionTasksView from './DetectionTasksView.vue'

export type DetectionTab = 'matrix' | 'tasks'

const route = useRoute()
const router = useRouter()
const activeTab = ref<DetectionTab>(getTabFromHash())

function getTabFromHash(): DetectionTab {
  const hash = route.hash.replace(/^#/, '').toLowerCase()
  if (hash === 'tasks' || route.query.tab === 'tasks') {
    return 'tasks'
  }
  return 'matrix'
}

function handleTabChange(value: string): void {
  const nextTab: DetectionTab = value === 'tasks' ? 'tasks' : 'matrix'
  activeTab.value = nextTab
  const nextHash = nextTab === 'tasks' ? '#tasks' : '#matrix'
  if (route.hash !== nextHash) {
    void router.replace({ hash: nextHash })
  }
}

watch(() => route.hash, () => {
  activeTab.value = getTabFromHash()
})
watch(() => route.query.tab, () => {
  activeTab.value = getTabFromHash()
})
</script>

<template>
  <div class="page-container detection-management-page">
    <PageHeader
      title="模型检测"
      subtitle="按模型分组查看各站点的可用性和响应状态，并配置自动化巡检任务"
    />

    <NTabs v-model:value="activeTab" type="line" animated @update:value="handleTabChange">
      <NTabPane name="matrix" tab="实时可用性检测">
        <DetectionView embedded />
      </NTabPane>
      <NTabPane name="tasks" tab="定时巡检任务">
        <DetectionTasksView embedded />
      </NTabPane>
    </NTabs>
  </div>
</template>

<style scoped>
.detection-management-page {
  min-width: 0;
}
</style>
