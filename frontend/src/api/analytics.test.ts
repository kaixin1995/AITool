import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet } from './http'
import {
  getAnalyticsDashboard,
  type AnalyticsDashboard
} from './analytics'

vi.mock('./http', () => ({
  httpGet: vi.fn()
}))

const mockedHttpGet = vi.mocked(httpGet)

beforeEach(() => {
  vi.clearAllMocks()
})

describe('Analytics API 客户端', () => {
  it('把取消信号传给慢查询请求', async () => {
    mockedHttpGet.mockResolvedValueOnce({ status: 'pending', retryAfterMs: 1000 })
    const controller = new AbortController()

    await getAnalyticsDashboard({ rangeType: 'week' }, controller.signal)

    expect(mockedHttpGet).toHaveBeenCalledWith(
      '/api/admin/analytics/dashboard?rangeType=week',
      expect.objectContaining({
        signal: controller.signal,
        validateStatus: expect.any(Function)
      })
    )
    const config = mockedHttpGet.mock.calls[0][1]
    expect(config?.validateStatus?.(429)).toBe(true)
    expect(config?.validateStatus?.(500)).toBe(false)
  })

  it('把来源筛选序列化到 Analytics 请求参数', async () => {
    mockedHttpGet.mockResolvedValueOnce({ status: 'pending', retryAfterMs: 1000 })

    await getAnalyticsDashboard({ rangeType: 'week', source: 'codex' })

    expect(mockedHttpGet).toHaveBeenCalledWith(
      '/api/admin/analytics/dashboard?rangeType=week&source=codex',
      expect.any(Object)
    )
  })

  it('兼容缺失新增细分字段的旧 Dashboard 响应', async () => {
    const legacyDashboard = {
      appliedFilter: {},
      summary: {},
      requestTrend: [],
      modelDistribution: [],
      siteDistribution: []
    } as unknown as AnalyticsDashboard
    mockedHttpGet.mockResolvedValueOnce(legacyDashboard)

    const result = await getAnalyticsDashboard({ rangeType: 'week' })

    expect(result).toMatchObject({
      resultTrend: [],
      tokenTrend: [],
      durationTrend: [],
      fallbackTrend: [],
      modelCacheRatioDistribution: [],
      sourceBreakdown: [],
      accessKeyBreakdown: [],
      protocolBreakdown: [],
      failureReasonBreakdown: [],
      statusCodeBreakdown: [],
      fallbackChainDistribution: []
    })
    expect(result).not.toHaveProperty('latencyPercentiles')
  })
})
