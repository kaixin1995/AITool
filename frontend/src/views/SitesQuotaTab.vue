<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { NButton, NEmpty, NProgress, NSpin, NTag, NTooltip, useThemeVars } from 'naive-ui'
import {
  fetchSiteQuotaOverview,
  refreshSiteQuota,
  type SiteQuotaKey,
  type SiteQuotaSite
} from '@/api/siteQuota'
import {
  formatBalance,
  formatBalanceDetail,
  formatCheckedAtAgo,
  formatResetCountdown,
  isStaleQuota,
  quotaBarColor,
  quotaStatusChip,
  remainingPercent
} from './sitesQuotaState'

/**
 * 站点页「额度查询」Tab：按站点分组、按密钥一卡展示套餐额度窗口（如智谱 GLM 的
 * 5 小时 + 每周两桶）。纯手动刷新——进入 Tab 自动刷一次，之后靠全局「刷新」按钮；
 * 无轮询、无后台巡检。打开时先渲染落库缓存值，刷新完成后原地替换。
 */

const sites = ref<SiteQuotaSite[]>([])
const loading = ref(false)
const refreshing = ref(false)
const refreshingSiteIds = ref<Set<string>>(new Set())
const lastRefreshedAt = ref<string | null>(null)
const now = ref(Date.now())
// 主题变量（跟随深浅色）：错误文字色等场景使用，避免硬编码颜色破坏暗色模式。
const themeVars = useThemeVars()
const errorColor = computed(() => themeVars.value.errorColor)

const hasSites = computed(() => sites.value.length > 0)

async function loadOverview(): Promise<void> {
  loading.value = true
  try {
    sites.value = await fetchSiteQuotaOverview()
  } catch {
    // 失败时保留空列表展示空态；错误信息已由 http 层统一 toast。
  } finally {
    loading.value = false
  }
}

async function refreshSite(site: SiteQuotaSite): Promise<void> {
  refreshingSiteIds.value.add(site.siteId)
  refreshingSiteIds.value = new Set(refreshingSiteIds.value)
  try {
    const updated = await refreshSiteQuota(site.siteId)
    const index = sites.value.findIndex(s => s.siteId === site.siteId)
    if (index >= 0) {
      sites.value[index] = updated
    } else {
      sites.value.push(updated)
    }
  } catch {
    // 单站失败不阻塞其他站点的刷新；错误信息已由 http 层统一 toast。
  } finally {
    refreshingSiteIds.value.delete(site.siteId)
    refreshingSiteIds.value = new Set(refreshingSiteIds.value)
  }
}

/** 全局手动刷新（也是进入 Tab 自动刷新的同一入口）；进行中时忽略重复触发。 */
async function refreshAll(): Promise<void> {
  if (refreshing.value || sites.value.length === 0) return
  refreshing.value = true
  try {
    // 站点间并行、站点内由后端并发查询该站全部密钥。
    await Promise.allSettled(sites.value.map(s => refreshSite(s)))
    lastRefreshedAt.value = new Date().toISOString()
  } finally {
    refreshing.value = false
  }
}

function keyTitle(key: SiteQuotaKey): string {
  const remark = key.remark?.trim()
  return remark ? `${remark}（${key.keyValueMasked}）` : key.keyValueMasked
}

// now 按分钟走字：倒计时/相对时间在页面停留期间保持新鲜（仅前端展示，不触发查询）。
let ticker: ReturnType<typeof setInterval> | null = null

onMounted(async () => {
  // 打开 Tab：先用落库缓存值渲染，再自动刷新一次实时数据。
  ticker = setInterval(() => {
    now.value = Date.now()
  }, 60_000)

  await loadOverview()
  void refreshAll()
})

onBeforeUnmount(() => {
  if (ticker) {
    clearInterval(ticker)
    ticker = null
  }
})
</script>

<template>
  <div class="site-quota-tab">
    <div class="site-quota-toolbar">
      <NTooltip trigger="hover" placement="right">
        <template #trigger>
          <span class="site-quota-help-trigger">?</span>
        </template>
        套餐额度按密钥独立计算（一个密钥 = 一份订阅）。<br>
        数据仅在进入本页或点击「刷新全部」时查询，不会自动轮询。<br>
        已禁用的密钥同样会查询额度——禁用只停止转发消耗，不影响额度查看。
      </NTooltip>
      <div class="site-quota-toolbar-actions">
        <span v-if="lastRefreshedAt" class="site-quota-refreshed-at">
          上次刷新：{{ formatCheckedAtAgo(lastRefreshedAt, now) }}
        </span>
        <NButton size="small" type="primary" secondary :loading="refreshing" :disabled="!hasSites" @click="refreshAll">
          刷新全部
        </NButton>
      </div>
    </div>

    <NSpin v-if="loading" class="site-quota-spin" />

    <NEmpty
      v-else-if="!hasSites"
      description="暂无支持额度查询的站点"
      class="site-quota-empty"
    >
      <template #extra>
        <div class="site-quota-empty-hint">
          添加 base_url 为 open.bigmodel.cn / api.z.ai（智谱，套餐额度）或 api.deepseek.com（DeepSeek，账户余额）的站点后，可在此查看。
        </div>
      </template>
    </NEmpty>

    <div v-else class="site-quota-sites">
      <section v-for="site in sites" :key="site.siteId" class="site-quota-site">
        <header class="site-quota-site-header">
          <span class="site-quota-site-name">{{ site.siteName }}</span>
          <NTag size="small" :bordered="false" type="info">{{ site.providerLabel }}</NTag>
          <span class="site-quota-site-url" :title="site.baseUrl">{{ site.baseUrl }}</span>
          <NTag v-if="refreshingSiteIds.has(site.siteId)" size="small" :bordered="false">
            刷新中…
          </NTag>
        </header>

        <div class="site-quota-keys">
          <div
            v-for="key in site.keys"
            :key="key.keyId"
            class="site-quota-key"
            :class="{ 'site-quota-key-stale': isStaleQuota(key.status) }"
          >
            <div class="site-quota-key-head">
              <span class="site-quota-key-remark" :title="keyTitle(key)">{{ key.remark?.trim() || key.keyValueMasked }}</span>
              <span v-if="key.remark?.trim()" class="site-quota-key-masked">{{ key.keyValueMasked }}</span>
              <NTag size="tiny" :bordered="false">P{{ key.priority }}</NTag>
              <NTag v-if="!key.isEnabled" size="tiny" :bordered="false" type="default">已禁用</NTag>
              <NTag size="tiny" :bordered="false" :type="quotaStatusChip(key.status).type">
                {{ quotaStatusChip(key.status).text }}
              </NTag>
              <NTag v-if="key.level" size="tiny" :bordered="false" type="info">{{ key.level }}</NTag>
            </div>

            <div v-if="key.windows.length > 0" class="site-quota-windows">
              <div v-for="w in key.windows" :key="w.id" class="site-quota-window">
                <div class="site-quota-window-label">{{ w.label }}</div>
                <NProgress
                  class="site-quota-window-bar"
                  :percentage="remainingPercent(w.usedPercent)"
                  :status="quotaBarColor(w.usedPercent)"
                  :show-indicator="false"
                  :height="8"
                  :border-radius="4"
                />
                <span class="site-quota-window-remaining">剩 {{ remainingPercent(w.usedPercent) }}%</span>
                <NTooltip v-if="w.resetAtUtc" trigger="hover">
                  <template #trigger>
                    <span class="site-quota-window-reset">{{ formatResetCountdown(w.resetAtUtc, now) }}</span>
                  </template>
                  重置于 {{ w.resetLabel }}（本地时间）
                </NTooltip>
                <span v-else class="site-quota-window-reset">—</span>
              </div>
            </div>

            <div v-if="key.balances.length > 0" class="site-quota-balances">
              <div v-for="b in key.balances" :key="b.currency" class="site-quota-balance">
                <div class="site-quota-balance-row">
                  <span class="site-quota-balance-label">余额</span>
                  <span class="site-quota-balance-total">{{ formatBalance(b.currency, b.totalBalance) }}</span>
                </div>
                <div v-if="formatBalanceDetail(b)" class="site-quota-balance-detail">
                  {{ formatBalanceDetail(b) }}
                </div>
              </div>
            </div>
            <div v-if="key.windows.length === 0 && key.balances.length === 0" class="site-quota-windows-empty">
              {{ key.status === 'never' ? '未查询过，进入本页或点「刷新全部」获取' : '暂无额度数据' }}
            </div>

            <div class="site-quota-key-foot">
              <span v-if="key.error" class="site-quota-key-error" :style="{ color: errorColor }" :title="key.error">{{ key.error }}</span>
              <span v-else-if="key.checkedAtUtc" class="site-quota-key-checked">
                上次查询：{{ formatCheckedAtAgo(key.checkedAtUtc, now) }}
              </span>
              <span v-else class="site-quota-key-checked">从未查询</span>
            </div>
          </div>
        </div>

        <div v-if="site.keys.length === 0" class="site-quota-keys-empty">
          该站点还没有密钥，先在「站点列表」中添加。
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.site-quota-tab {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.site-quota-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

/* 说明文字收纳为 ? 图标，悬停显示（与新建站点弹窗的协议帮助同款视觉）。 */
.site-quota-help-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border: 1px solid var(--border-color-global);
  border-radius: 50%;
  color: var(--text-color-secondary);
  font-size: 11px;
  line-height: 1;
  cursor: help;
  user-select: none;
}

.site-quota-toolbar-actions {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-left: auto;
}

.site-quota-refreshed-at {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.site-quota-spin {
  align-self: center;
  margin: 48px 0;
}

.site-quota-empty {
  margin: 48px 0;
}

.site-quota-empty-hint {
  color: var(--text-color-secondary);
  font-size: 13px;
  max-width: 420px;
}

.site-quota-sites {
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.site-quota-site {
  border: 1px solid var(--border-color-soft);
  border-radius: 10px;
  padding: 12px 14px 14px;
}

.site-quota-site-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding-bottom: 10px;
}

.site-quota-site-name {
  font-weight: 600;
  font-size: 14px;
}

.site-quota-site-url {
  color: var(--text-color-secondary);
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 密钥一卡，自适应列数：宽屏多列、窄屏单列，避免进度条拉满整行。 */
.site-quota-keys {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(380px, 1fr));
  gap: 10px;
}

.site-quota-key {
  border: 1px solid var(--border-color-soft);
  border-radius: 8px;
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

/* 上次查询失败：展示的是旧缓存值，整体置灰。 */
.site-quota-key-stale .site-quota-windows,
.site-quota-key-stale .site-quota-balances {
  opacity: 0.5;
}

.site-quota-key-head {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  min-width: 0;
}

.site-quota-key-remark {
  font-size: 13px;
  font-weight: 600;
  max-width: 140px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.site-quota-key-masked {
  color: var(--text-color-secondary);
  font-size: 12px;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
}

.site-quota-windows {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

/* 余额（余额型供应商如 DeepSeek）：一行总额 + 可选的赠送/充值明细。 */
.site-quota-balances {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.site-quota-balance-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 10px;
}

.site-quota-balance-label {
  font-size: 13px;
}

.site-quota-balance-total {
  font-size: 15px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.site-quota-balance-detail {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.site-quota-window {
  display: grid;
  grid-template-columns: 64px minmax(90px, 1fr) 52px minmax(96px, auto);
  align-items: center;
  column-gap: 10px;
}

.site-quota-window-label {
  font-size: 13px;
  white-space: nowrap;
}

.site-quota-window-remaining {
  font-size: 13px;
  text-align: right;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.site-quota-window-reset {
  font-size: 12px;
  color: var(--text-color-secondary);
  cursor: default;
  white-space: nowrap;
  text-align: right;
}

.site-quota-windows-empty {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.site-quota-key-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  min-height: 16px;
}

.site-quota-key-checked {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.site-quota-key-error {
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.site-quota-keys-empty {
  color: var(--text-color-secondary);
  font-size: 13px;
}
</style>
