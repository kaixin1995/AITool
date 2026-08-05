<script setup lang="ts">
/**
 * 页面级页头：复刻原 Razor Pages 的 .page-header 设计。
 * 标题（22px/700）+ 副标题（13.5px/次要色）在左，操作区在右。
 * 内容容器（NCard/table）应放在 PageHeader 之后，而非嵌套其内。
 */
defineProps<{
  title: string
  subtitle?: string
}>()
</script>

<template>
  <div class="page-header">
    <div class="page-header-main">
      <h2 class="page-title">{{ title }}</h2>
      <p v-if="subtitle" class="page-subtitle">{{ subtitle }}</p>
    </div>
    <div v-if="$slots.actions || $slots.default" class="page-header-actions">
      <slot name="actions">
        <slot />
      </slot>
    </div>
  </div>
</template>

<style scoped>
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 24px;
}
.page-header-main {
  min-width: 0;
}
.page-title {
  font-size: 22px;
  font-weight: 700;
  color: var(--text-primary);
  margin: 0;
  line-height: 1.3;
}
.page-subtitle {
  font-size: 13.5px;
  color: var(--text-color-secondary);
  margin: 4px 0 0;
  line-height: 1.5;
}
.page-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
  flex-wrap: wrap;
  justify-content: flex-end;
}

/* 小屏：标题与操作区上下排列 */
@media (max-width: 768px) {
  .page-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 12px;
  }
  .page-header-actions {
    justify-content: flex-start;
  }
}
</style>
