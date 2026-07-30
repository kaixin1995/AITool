import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet } from './http'
import { getAnalyticsDashboard } from './analytics'

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
})
