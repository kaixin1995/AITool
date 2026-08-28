<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import ChatTestPane from './ChatTestPane.vue'
</script>

<template>
  <div class="page-container chat-page">
    <PageHeader title="对话" subtitle="选择站点与模型直接发起对话测试" />
    <ChatTestPane />
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

.chat-page :deep(.chat-admin-shell) {
  flex: 1;
  min-height: 0;
}

@media (max-width: 1200px) {
  .chat-page {
    overflow: visible;
  }
}
</style>
