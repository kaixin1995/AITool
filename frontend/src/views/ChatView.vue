<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { NTabs, NTabPane } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import ChatTestPane from './ChatTestPane.vue'

const activeTab = ref('chat')

onMounted(() => {
  if (window.location.hash === '#chatTestPane') activeTab.value = 'chat'
})

watch(activeTab, (value) => {
  const hash = '#chatTestPane'
  if (window.location.hash !== hash) history.replaceState(null, '', hash)
})
</script>

<template>
  <div class="page-container chat-page">
    <PageHeader title="对话" subtitle="对话测试" />
    <NTabs v-model:value="activeTab" class="chat-tabs" type="line" animated size="large">
      <NTabPane name="chat" tab="对话测试" display-directive="show" class="chat-tab-pane">
        <ChatTestPane />
      </NTabPane>
    </NTabs>
  </div>
</template>

<style scoped>
.chat-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-width: 0;
  min-height: 0;
  box-sizing: border-box;
  overflow: hidden;
}

.chat-tabs {
  display: flex;
  flex: 1;
  min-width: 0;
  max-width: 100%;
  min-height: 0;
  flex-direction: column;
}

.chat-tabs :deep(.n-tabs-nav-scroll-wrapper) {
  max-width: 100%;
  overflow: hidden;
}

.chat-tabs :deep(.n-tabs-pane-wrapper),
.chat-tabs :deep(.n-tab-pane) {
  flex: 1;
  min-height: 0;
}

.chat-tab-pane {
  height: 100%;
  min-height: 0;
}

@media (max-width: 1200px) {
  .chat-page {
    overflow: visible;
  }
}
</style>
