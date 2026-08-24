<script setup lang="ts">
import { ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NTabPane, NTabs } from 'naive-ui'
import PageHeader from '@/components/PageHeader.vue'
import RoutesView from './RoutesView.vue'
import CompatibilityView from './CompatibilityView.vue'

type RouteManagementTab = 'routes' | 'compatibility'

const route = useRoute()
const router = useRouter()
const activeTab = ref<RouteManagementTab>(getTabFromHash())

function getTabFromHash(): RouteManagementTab {
  const hash = route.hash.replace(/^#/, '').toLowerCase()
  if (hash === 'compatibility' || route.query.tab === 'compatibility') {
    return 'compatibility'
  }
  return 'routes'
}

function handleTabChange(value: string): void {
  const nextTab: RouteManagementTab = value === 'compatibility' ? 'compatibility' : 'routes'
  activeTab.value = nextTab
  const nextHash = nextTab === 'compatibility' ? '#compatibility' : '#routes'
  if (route.hash !== nextHash) {
    void router.replace({ hash: nextHash })
  }
}

// 支持浏览器前进/后退，以及从旧兼容规则集地址进入时同步当前页签。
watch(() => route.hash, () => {
  activeTab.value = getTabFromHash()
})
watch(() => route.query.tab, () => {
  activeTab.value = getTabFromHash()
})
</script>

<template>
  <div class="page-container route-management-page">
    <PageHeader title="路由管理" subtitle="统一管理路由规则与兼容规则集" />

    <NTabs v-model:value="activeTab" type="line" animated @update:value="handleTabChange">
      <NTabPane name="routes" tab="路由规则">
        <RoutesView embedded />
      </NTabPane>
      <NTabPane name="compatibility" tab="兼容规则集">
        <CompatibilityView embedded />
      </NTabPane>
    </NTabs>
  </div>
</template>
