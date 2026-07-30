<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { NButton, NCard, NEmpty, NInput, NPopconfirm, NSelect, useMessage } from 'naive-ui'
import * as api from '@/api/modelHealth'
import type { ModelHealthMonitoredModel, ModelHealthDashboard, ModelHealthTimelineSegment } from '@/api/modelHealth'
import PageHeader from '@/components/PageHeader.vue'

const message = useMessage()
const loading = ref(false)
const monitored = ref<ModelHealthMonitoredModel[]>([])
const availableModels = ref<{ id: string; displayName: string }[]>([])
const range = ref('7d')
const rangeOptions = ref<Array<{ value: string; label: string }>>([])
const healthData = ref<ModelHealthDashboard['healthData']>({})
const availableKeyword = ref('')
const healthKeyword = ref('')
const selectedModelId = ref<string | null>(null)
const expandedModelIds = ref<Set<string>>(new Set())

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.getModelHealthDashboard(range.value)
    monitored.value = resp.monitoredModels ?? []
    availableModels.value = resp.availableModels ?? []
    rangeOptions.value = resp.rangeOptions ?? []
    healthData.value = resp.healthData ?? {}
    if (selectedModelId.value && !availableModels.value.some((m) => m.id === selectedModelId.value)) {
      selectedModelId.value = null
    }
  } finally {
    loading.value = false
  }
}

async function handleAdd(): Promise<void> {
  if (!selectedModelId.value) {
    message.warning('请选择要监控的模型')
    return
  }
  await api.addMonitor(selectedModelId.value)
  message.success('已加入监控')
  selectedModelId.value = null
  availableKeyword.value = ''
  await load()
}

async function handleRemove(m: ModelHealthMonitoredModel): Promise<void> {
  await api.removeMonitor(m.modelLibraryItemId)
  message.success('已移除监控')
  await load()
}

function toggleDetail(modelId: string): void {
  const next = new Set(expandedModelIds.value)
  if (next.has(modelId)) next.delete(modelId)
  else next.add(modelId)
  expandedModelIds.value = next
}

function formatDuration(value: number | null | undefined): string {
  const number = Number(value ?? 0)
  if (!Number.isFinite(number) || number <= 0) return '-'
  if (number >= 60000) {
    const minutes = Math.floor(number / 60000)
    const seconds = Math.floor((number % 60000) / 1000)
    return `${minutes}分 ${seconds}秒`
  }
  if (number >= 1000) return `${(number / 1000).toFixed(1)}秒`
  return `${Math.round(number)}ms`
}

function formatNumber(value: number | null | undefined): string {
  const number = Number(value ?? 0)
  return Number.isFinite(number) ? number.toLocaleString('zh-CN') : '-'
}

function formatPercent(value: number | null | undefined): string {
  const number = Number(value ?? 0)
  if (!Number.isFinite(number)) return '-'
  return `${(number * 100).toFixed(1)}%`
}

function statusLabel(status: string): string {
  if (status === 'success') return '正常'
  if (status === 'fail') return '异常'
  return status || '未知'
}

function timelineTitle(segment: ModelHealthTimelineSegment): string {
  return `${new Date(segment.startAt).toLocaleString('zh-CN')} ~ ${new Date(segment.endAt).toLocaleString('zh-CN')} | 总请求 ${segment.count} 次 | 成功 ${segment.successCount} 次 | 失败 ${segment.failureCount} 次`
}

function rangeTitle(): string {
  if (range.value === '1d') return '最近 24 小时'
  if (range.value === '30d') return '最近 30 天'
  return '最近 7 天'
}

function overviewSlots(model: ModelHealthMonitoredModel): Array<{ status: string; title: string }> {
  const slotCount = 12
  const total = model.successCount + model.failureCount
  if (total <= 0) {
    return Array.from({ length: slotCount }, () => ({ status: 'empty', title: '暂无请求记录' }))
  }
  let successSlots = Math.floor(model.successCount * slotCount / total)
  if (model.successCount > 0) successSlots = Math.max(successSlots, 1)
  if (model.failureCount > 0) successSlots = Math.min(successSlots, slotCount - 1)
  return Array.from({ length: slotCount }, (_, index) => ({
    status: index < successSlots ? 'success' : 'fail',
    title: `${formatPercent(model.averageSuccessRate)} | 成功 ${model.successCount} 次 | 失败 ${model.failureCount} 次`
  }))
}

function segmentStyle(segment: ModelHealthTimelineSegment): Record<string, string> {
  if (segment.count <= 0) return { background: '#e2e8f0' }
  const successRate = Math.max(0, Math.min(100, (segment.successCount * 100) / segment.count))
  if (successRate <= 0) return { background: 'var(--danger, #d03050)' }
  if (successRate >= 100) return { background: 'var(--success, #18a058)' }
  return { background: `linear-gradient(to right, var(--success, #18a058) 0 ${successRate.toFixed(2)}%, var(--danger, #d03050) ${successRate.toFixed(2)}% 100%)` }
}

const filteredAvailableOptions = computed(() => {
  const keyword = availableKeyword.value.trim().toLowerCase()
  return availableModels.value
    .filter((m) => !keyword || m.displayName.toLowerCase().includes(keyword))
    .map((m) => ({ label: m.displayName, value: m.id }))
})

const filteredMonitored = computed(() => {
  const keyword = healthKeyword.value.trim().toLowerCase()
  return monitored.value.filter((m) => !keyword || m.displayName.toLowerCase().includes(keyword))
})

watch(range, () => { void load() })
onMounted(load)
</script>

<template>
  <div class="page-container model-health-page">
    <PageHeader title="模型健康看板" subtitle="监控指定模型在各站点的健康状态和检测历史" />

    <NCard class="form-card health-add-card" :bordered="false">
      <div class="form-section-title">添加监控模型</div>
      <div class="health-form-row add-monitor-row">
        <label class="health-field health-field-search">
          <span class="health-label">搜索模型</span>
          <NInput v-model:value="availableKeyword" placeholder="输入模型名称过滤下拉选项..." clearable />
        </label>
        <label class="health-field health-field-select">
          <span class="health-label">选择模型</span>
          <NSelect
            v-model:value="selectedModelId"
            :options="filteredAvailableOptions"
            placeholder="-- 请选择模型 --"
            filterable
            clearable
          />
          <span v-if="availableKeyword && filteredAvailableOptions.length === 0" class="health-help">没有匹配的模型，请调整关键词。</span>
        </label>
        <NButton type="primary" class="add-monitor-button" :disabled="!selectedModelId" @click="handleAdd">添加监控</NButton>
      </div>
    </NCard>

    <NCard v-if="monitored.length > 0" class="form-card health-filter-card" :bordered="false">
      <div class="health-form-row filter-row">
        <label class="health-field health-range-field">
          <span class="health-label">时间范围</span>
          <NSelect v-model:value="range" :options="rangeOptions" />
        </label>
        <label class="health-field health-field-search">
          <span class="health-label">搜索模型</span>
          <NInput v-model:value="healthKeyword" placeholder="输入模型名称过滤..." clearable />
        </label>
      </div>
    </NCard>

    <div v-if="!loading && monitored.length === 0" class="table-wrapper">
      <div class="table-empty">
        <div class="table-empty-icon">💊</div>
        <div class="table-empty-text">暂未配置监控模型，请在上方选择模型添加</div>
      </div>
    </div>

    <div v-else class="table-wrapper health-table-wrapper">
      <table class="health-overview-table">
        <thead>
          <tr>
            <th>模型</th>
            <th>最近状态</th>
            <th>成功率</th>
            <th>成功/失败</th>
            <th>总调用</th>
            <th>平均延迟</th>
            <th class="health-actions-heading">操作</th>
          </tr>
        </thead>
        <tbody>
          <template v-for="model in filteredMonitored" :key="model.modelLibraryItemId">
            <tr class="health-model-group">
              <td class="health-model-cell">
                <div class="health-model-name">{{ model.displayName }}</div>
                <div class="health-model-meta">{{ model.siteCount }} 个关联站点</div>
              </td>
              <td>
                <div class="health-status-bars">
                  <span
                    v-for="(slot, index) in overviewSlots(model)"
                    :key="index"
                    class="health-status-bar-item"
                    :class="slot.status"
                    :title="slot.title"
                  />
                </div>
              </td>
              <td><span class="health-kpi-value success-rate">{{ formatPercent(model.averageSuccessRate) }}</span></td>
              <td>
                <div class="health-kpi-value">{{ formatNumber(model.successCount) }} / {{ formatNumber(model.failureCount) }}</div>
                <div class="health-model-meta">成功 / 失败</div>
              </td>
              <td><span class="health-kpi-value">{{ formatNumber(model.totalRequestCount) }}</span></td>
              <td><span class="health-kpi-value latency">{{ formatDuration(model.averageDurationMs) }}</span></td>
              <td>
                <div class="health-row-actions">
                  <NButton size="small" class="health-action-btn detail" @click="toggleDetail(model.modelLibraryItemId)">
                    {{ expandedModelIds.has(model.modelLibraryItemId) ? '收起明细' : '查看明细' }}
                  </NButton>
                  <NPopconfirm @positive-click="handleRemove(model)">
                    <template #trigger>
                      <NButton size="small" class="health-action-btn remove">移除监控</NButton>
                    </template>
                    确认移除该模型的监控？
                  </NPopconfirm>
                </div>
              </td>
            </tr>
            <tr v-if="expandedModelIds.has(model.modelLibraryItemId)" class="health-detail-row">
              <td colspan="7">
                <div class="health-detail-panel">
                  <div v-if="healthData[model.modelLibraryItemId]?.length" class="health-detail-content">
                    <div class="health-badge-row">
                      <span class="health-badge neutral">{{ model.siteCount }} 个关联站点</span>
                      <span class="health-badge success">{{ model.healthySiteCount }} 个正常站点</span>
                      <span class="health-badge danger">{{ model.unhealthySiteCount }} 个异常站点</span>
                      <span class="health-badge success-soft">成功 {{ formatNumber(model.successCount) }} 次</span>
                      <span class="health-badge danger-soft">失败 {{ formatNumber(model.failureCount) }} 次</span>
                      <span class="health-badge neutral">总请求 {{ formatNumber(model.totalRequestCount) }} 次</span>
                    </div>

                    <article v-for="(site, index) in healthData[model.modelLibraryItemId]" :key="index" class="health-site-card">
                      <div class="health-site-header">
                        <div class="health-site-title">
                          <strong class="health-site-name">{{ site.siteName }}</strong>
                          <code>{{ site.remoteModelName }}</code>
                          <span class="health-badge" :class="site.lastStatus === 'success' ? 'success' : site.lastStatus === 'fail' ? 'danger' : 'neutral'">{{ statusLabel(site.lastStatus) }}</span>
                        </div>
                        <div class="health-site-meta">成功率 {{ formatPercent(site.successRate) }}</div>
                      </div>

                      <div class="health-rate-bar">
                        <div class="health-rate-label">成功率 {{ formatPercent(site.successRate) }}</div>
                        <div class="health-rate-track"><div class="health-rate-fill" :style="{ width: formatPercent(site.successRate) }" /></div>
                      </div>

                      <div v-if="site.timelineSegments.length" class="health-timeline">
                        <div class="health-timeline-label">{{ rangeTitle() }}共 {{ formatNumber(site.timelineSegments.reduce((sum, segment) => sum + segment.count, 0)) }} 次请求（线段按时间段聚合，出现失败即标红）</div>
                        <div class="health-timeline-line">
                          <span
                            v-for="(segment, segmentIndex) in site.timelineSegments"
                            :key="segmentIndex"
                            class="health-timeline-segment"
                            :style="segmentStyle(segment)"
                            :title="timelineTitle(segment)"
                          />
                        </div>
                        <div class="health-timeline-range">
                          <span>{{ new Date(site.timelineSegments[0].startAt).toLocaleDateString('zh-CN', { month: '2-digit', day: '2-digit' }) }}</span>
                          <span>{{ new Date(site.timelineSegments[site.timelineSegments.length - 1].endAt).toLocaleDateString('zh-CN', { month: '2-digit', day: '2-digit' }) }}</span>
                        </div>
                      </div>
                      <div v-else class="health-empty-note">暂无请求记录</div>
                    </article>
                  </div>
                  <NEmpty v-else description="该模型暂无站点映射数据" />
                </div>
              </td>
            </tr>
          </template>
        </tbody>
      </table>
    </div>
  </div>
</template>

<style scoped>
.model-health-page {
  min-width: 0;
}

.form-card {
  margin-bottom: 20px;
}

.form-card :deep(.n-card__content) {
  min-width: 0;
}

.form-section-title {
  margin-bottom: 12px;
  color: var(--text-primary);
  font-size: 15px;
  font-weight: 700;
}

.health-form-row {
  display: grid;
  gap: 12px;
  align-items: end;
}

.add-monitor-row {
  grid-template-columns: minmax(180px, 4fr) minmax(240px, 5fr) auto;
}

.filter-row {
  grid-template-columns: minmax(150px, 3fr) minmax(240px, 4fr);
  justify-content: start;
}

.health-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.health-label {
  color: var(--text-color-secondary);
  font-size: 13px;
  font-weight: 500;
}

.health-help {
  color: var(--text-color-secondary);
  font-size: 12px;
}

.add-monitor-button {
  min-width: 96px;
}

.table-wrapper {
  max-width: 100%;
  overflow-x: auto;
  border-radius: 18px;
}

.table-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  min-height: 180px;
  border: 1px solid var(--border-color-global);
  border-radius: 18px;
  background: var(--bg-card);
  color: var(--text-color-secondary);
}

.table-empty-icon {
  font-size: 36px;
}

.health-overview-table {
  width: 100%;
  min-width: 980px;
  border-collapse: collapse;
  background: var(--bg-card);
  border-radius: 18px;
  overflow: hidden;
  box-shadow: 0 10px 30px rgba(15, 23, 42, 0.06);
}

.health-overview-table th,
.health-overview-table td {
  padding: 22px 18px;
  border-bottom: 1px solid var(--border-color-global);
  vertical-align: middle;
}

.health-overview-table thead th {
  background: var(--bg-card);
  color: var(--text-color-secondary);
  font-size: 14px;
  font-weight: 700;
  white-space: nowrap;
}

.health-overview-table tbody tr:last-child td {
  border-bottom: none;
}

.health-model-cell {
  min-width: 260px;
}

.health-model-name {
  margin-bottom: 6px;
  color: var(--text-primary);
  font-size: 16px;
  font-weight: 700;
}

.health-model-meta,
.health-site-meta,
.health-rate-label,
.health-timeline-label,
.health-timeline-range,
.health-empty-note {
  color: var(--text-color-secondary);
  font-size: 13px;
}

.health-kpi-value {
  color: var(--text-primary);
  font-size: 18px;
  font-weight: 700;
  white-space: nowrap;
}

.health-kpi-value.success-rate {
  color: var(--success, #18a058);
}

.health-kpi-value.latency {
  color: var(--danger, #d03050);
}

.health-status-bars {
  display: flex;
  align-items: stretch;
  gap: 2px;
  width: 118px;
  min-width: 118px;
}

.health-status-bar-item {
  width: 8px;
  height: 36px;
  border-radius: 3px;
  background: var(--success, #18a058);
  flex: 0 0 auto;
}

.health-status-bar-item.fail {
  background: var(--danger, #d03050);
}

.health-status-bar-item.empty {
  background: #e2e8f0;
}

.health-actions-heading {
  text-align: right;
}

.health-row-actions {
  display: flex;
  gap: 10px;
  justify-content: flex-end;
  flex-wrap: wrap;
}

.health-action-btn {
  min-width: 88px;
  border-radius: 999px;
  font-weight: 600;
}

.health-action-btn.detail {
  background: #eff6ff;
  color: var(--primary, #2563eb);
  border-color: rgba(59, 130, 246, 0.18);
}

.health-action-btn.remove {
  background: #fff5f5;
  color: var(--danger, #d03050);
  border-color: rgba(239, 68, 68, 0.16);
}

.health-detail-row > td {
  background: #fbfdff;
  padding-top: 0;
}

.health-detail-panel {
  padding-top: 16px;
  max-width: 100%;
  overflow: hidden;
}

.health-badge-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-bottom: 16px;
}

.health-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 22px;
  padding: 3px 8px;
  border-radius: 999px;
  font-size: 12px;
  font-weight: 700;
  white-space: nowrap;
}

.health-badge.neutral { background: #f1f5f9; color: #334155; }
.health-badge.success { background: #18a058; color: #fff; }
.health-badge.danger { background: #d03050; color: #fff; }
.health-badge.success-soft { background: #e8f7ea; color: #166534; }
.health-badge.danger-soft { background: #fee2e2; color: #b91c1c; }

.health-site-card {
  margin-bottom: 16px;
  padding: 18px;
  border: 1px solid var(--border-color-global);
  border-radius: 16px;
  background: var(--bg-card);
  overflow: hidden;
}

.health-site-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  margin-bottom: 12px;
}

.health-site-title {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  min-width: 0;
}

.health-site-name {
  color: var(--text-primary);
  font-size: 15px;
}

.health-site-title code {
  max-width: 320px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.health-rate-bar {
  margin-bottom: 14px;
}

.health-rate-label {
  margin-bottom: 8px;
}

.health-rate-track {
  height: 8px;
  border-radius: 999px;
  background: #e2e8f0;
  overflow: hidden;
}

.health-rate-fill {
  height: 100%;
  border-radius: inherit;
  background: var(--success, #18a058);
}

.health-timeline {
  min-width: 0;
}

.health-timeline-line {
  display: flex;
  align-items: center;
  gap: 4px;
  width: 100%;
  min-height: 18px;
  margin-top: 8px;
}

.health-timeline-segment {
  height: 10px;
  border-radius: 999px;
  flex: 1 1 0;
  min-width: 8px;
}

.health-timeline-range {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  margin-top: 8px;
  font-size: 12px;
}

.health-empty-note {
  padding: 8px 0;
  font-size: 12.5px;
}

[data-theme='dark'] .health-detail-row > td,
[data-theme='dark'] .health-badge.neutral,
[data-theme='dark'] .health-action-btn.detail,
[data-theme='dark'] .health-action-btn.remove {
  background: rgba(255, 255, 255, 0.05);
}

@media (max-width: 768px) {
  .add-monitor-row,
  .filter-row {
    grid-template-columns: 1fr;
  }

  .add-monitor-button {
    width: 100%;
  }

  .health-overview-table th,
  .health-overview-table td {
    padding: 16px 12px;
  }

  .health-row-actions {
    justify-content: flex-start;
  }

  .health-site-card {
    padding: 14px;
  }
}
</style>
