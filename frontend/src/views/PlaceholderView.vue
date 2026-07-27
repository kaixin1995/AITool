<script setup lang="ts">
import { useRoute } from 'vue-router'
import { NEmpty, NCard } from 'naive-ui'
const route = useRoute()
</script>

<template>
  <div class="page-container">
    <NCard>
      <NEmpty :description="`「${route.meta.title}」页面待迁移（阶段 4 实现）`" />
    </NCard>
  </div>
</template>
