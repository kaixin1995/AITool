<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { NCard, NGrid, NGi, NStatistic, NSpin, NEmpty } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import { getDashboardStats, type DashboardStats } from '@/api/dashboard'

const stats = ref<DashboardStats | null>(null)
const loading = ref(true)
const error = ref('')

onMounted(async () => {
  try {
    stats.value = await getDashboardStats()
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div class="page-container">
    <PageHeader title="仪表盘" subtitle="系统总览" />
    <NSpin :show="loading">
      <NGrid v-if="stats" :cols="4" :x-gap="16" :y-gap="16" responsive="screen" item-responsive>
        <NGi span="4 m:2 l:1">
          <NCard>
            <NStatistic label="站点数" :value="stats.siteCount" />
          </NCard>
        </NGi>
        <NGi span="4 m:2 l:1">
          <NCard>
            <NStatistic label="模型数" :value="stats.modelCount" />
          </NCard>
        </NGi>
        <NGi span="4 m:2 l:1">
          <NCard>
            <NStatistic label="站点映射数" :value="stats.mappingCount" />
          </NCard>
        </NGi>
        <NGi span="4 m:2 l:1">
          <NCard>
            <NStatistic label="路由规则数" :value="stats.routeCount" />
          </NCard>
        </NGi>
        <NGi span="4 m:2 l:1">
          <NCard>
            <NStatistic label="访问密钥数" :value="stats.accessKeyCount" />
          </NCard>
        </NGi>
        <NGi span="4 m:2 l:1">
          <NCard>
            <NStatistic label="检测任务数" :value="stats.detectionTaskCount" />
          </NCard>
        </NGi>
      </NGrid>
      <NEmpty v-else-if="!loading" :description="error || '暂无数据'" />
    </NSpin>
  </div>
</template>
