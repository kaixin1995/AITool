import { describe, expect, it } from 'vitest'
import {
  ANALYTICS_ANALYSIS_TABS,
  DEFAULT_ANALYTICS_ANALYSIS_DIMENSION,
  buildAnalyticsDefaultCustomRange,
  buildAnalyticsQuery,
  calculateAnalyticsTotalTokens,
  formatAnalyticsTokenSplit,
  removeAnalyticsFilter,
  resetAnalyticsFilters,
  shouldAutoLoadAnalytics,
  sortAnalyticsBreakdown,
  toggleAnalyticsDimensionFilter,
  toggleDimensionFilter
} from './analyticsState'
import { getUsageSourceLabel } from './usageSource'

describe('Analytics 汇总', () => {
  it('输入 Token 已含缓存时不重复累计缓存 Token', () => {
    expect(calculateAnalyticsTotalTokens({
      totalInputTokens: 120,
      totalCachedTokens: 20,
      totalOutputTokens: 30
    })).toBe(150)
  })

  it('优先使用后端返回的总 Token', () => {
    expect(calculateAnalyticsTotalTokens({
      totalTokens: 180,
      totalInputTokens: 120,
      totalCachedTokens: 20,
      totalOutputTokens: 30
    })).toBe(180)
  })

  it('正确格式化 输入 / 缓存 / 输出 三段 Token', () => {
    expect(formatAnalyticsTokenSplit({
      totalInputTokens: 1000,
      totalCachedTokens: 500,
      totalOutputTokens: 200
    }, (v) => `${v}`)).toBe('1000 / 500 / 200')
  })

  it('空汇总时安全回退默认值 0 / 0 / 0', () => {
    expect(formatAnalyticsTokenSplit(undefined, (v) => `${v}`)).toBe('0 / 0 / 0')
  })
})

describe('Analytics 来源筛选', () => {
  it('来源筛选使用与 Usage Logs 相同的稳定值', () => {
    expect(getUsageSourceLabel('claude-code')).toBe('Claude Code')
    expect(getUsageSourceLabel('deepseek-harness')).toBe('DeepSeek Harness')
    expect(getUsageSourceLabel('detection-task')).toBe('定时检测')
  })

  it('未知来源保留原始值，不静默映射为已知来源', () => {
    expect(getUsageSourceLabel('external-client')).toBe('external-client')
    expect(getUsageSourceLabel('')).toBe('-')
  })

  it('Analytics 请求参数包含来源筛选', () => {
    expect(buildAnalyticsQuery({ source: 'codex' })).toMatchObject({ source: 'codex' })
  })
})

describe('Analytics 联动筛选纯逻辑', () => {
  it('点击同一维度项目时切换筛选', () => {
    expect(toggleDimensionFilter({ source: 'codex' }, 'source', 'codex')).toEqual({})
    expect(toggleDimensionFilter({}, 'source', 'codex')).toEqual({ source: 'codex' })
  })

  it('点击同一维度其他项目时替换筛选', () => {
    expect(toggleDimensionFilter({ source: 'chat' }, 'source', 'codex')).toEqual({ source: 'codex' })
  })

  it('删除筛选标签不会影响其他维度', () => {
    expect(removeAnalyticsFilter({ source: 'codex', protocolType: 'openai' }, 'source'))
      .toEqual({ protocolType: 'openai' })
  })

  it('重置筛选返回空对象', () => {
    expect(resetAnalyticsFilters({ source: 'codex', protocolType: 'openai' })).toEqual({})
  })

  it('按请求数降序排序并保持相同值的原始顺序', () => {
    const points = [
      { key: 'first', label: '首个', requestCount: 2 },
      { key: 'second', label: '第二个', requestCount: 5 },
      { key: 'third', label: '第三个', requestCount: 2 }
    ]

    expect(sortAnalyticsBreakdown(points, 'requestCount').map((point) => point.key))
      .toEqual(['second', 'first', 'third'])
    expect(points.map((point) => point.key)).toEqual(['first', 'second', 'third'])
  })
})

describe('Analytics 细分分析状态', () => {
  it('默认打开来源 Tab，并保留完整的细分 Tab 顺序', () => {
    expect(DEFAULT_ANALYTICS_ANALYSIS_DIMENSION).toBe('source')
    expect(ANALYTICS_ANALYSIS_TABS.map((tab) => tab.key)).toEqual([
      'source',
      'accessKey',
      'protocol',
      'failureReason',
      'statusCode',
      'fallbackChain',
      'latencyPercentiles'
    ])
    expect(ANALYTICS_ANALYSIS_TABS.map((tab) => tab.label)).toEqual([
      '来源',
      'Access Key',
      '协议',
      '失败原因',
      'HTTP 状态码',
      '回退链路',
      '延迟分位数'
    ])
  })

  it('细分 Tab 切换不清除已有筛选，且仅可筛选来源、Access Key 和协议', () => {
    const filters = { siteId: 'site-1', modelName: 'model-1' }

    expect(toggleAnalyticsDimensionFilter(filters, 'failureReason', 'timeout')).toEqual(filters)
    expect(toggleAnalyticsDimensionFilter(filters, 'source', 'proxy')).toEqual({
      ...filters,
      source: 'proxy'
    })
    expect(toggleAnalyticsDimensionFilter(filters, 'accessKey', 'key-1')).toEqual({
      ...filters,
      accessKeyId: 'key-1'
    })
    expect(toggleAnalyticsDimensionFilter(filters, 'protocol', 'OpenAI')).toEqual({
      ...filters,
      protocolType: 'OpenAI'
    })
  })

  it('站点和模型联动使用稳定 key，点击分析只读维度不改变筛选', () => {
    expect(toggleAnalyticsDimensionFilter({}, 'site', 'site-1')).toEqual({ siteId: 'site-1' })
    expect(toggleAnalyticsDimensionFilter({}, 'model', 'model-1')).toEqual({ modelName: 'model-1' })
    expect(toggleAnalyticsDimensionFilter({}, 'statusCode', '401')).toEqual({})
  })
})

describe('Analytics 时间筛选', () => {
  it('自定义时间等待用户点击应用，预设范围自动查询', () => {
    expect(shouldAutoLoadAnalytics('custom')).toBe(false)
    expect(shouldAutoLoadAnalytics('week')).toBe(true)
  })

  it('自定义范围默认填充本周一到当天结束', () => {
    const { startTime, endTime } = buildAnalyticsDefaultCustomRange(new Date('2026-07-30T12:34:56'))

    const start = new Date(startTime)
    const end = new Date(endTime)
    expect([start.getFullYear(), start.getMonth(), start.getDate(), start.getHours(), start.getMinutes()])
      .toEqual([2026, 6, 27, 0, 0])
    expect([end.getFullYear(), end.getMonth(), end.getDate(), end.getHours(), end.getMinutes()])
      .toEqual([2026, 6, 30, 23, 59])
  })
})
