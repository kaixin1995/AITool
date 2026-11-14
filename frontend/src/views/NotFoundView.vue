<script setup lang="ts">
import { NButton, NResult } from 'naive-ui'
import { RouterLink } from 'vue-router'
</script>

<template>
  <div class="page-container">
    <NResult status="404" title="404" description="页面不存在">
      <template #footer>
        <RouterLink :to="{ name: 'dashboard' }"><NButton>返回首页</NButton></RouterLink>
      </template>
    </NResult>
  </div>
</template>
