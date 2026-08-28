import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet } from './http'
import { getUsageLogSummary, listUsageLogs } from './usageLogs'

vi.mock('./http', () => ({
  httpGet: vi.fn()
}))

const mockedHttpGet = vi.mocked(httpGet)

beforeEach(() => {
  vi.clearAllMocks()
})

describe('Usage Logs API 客户端', () => {
  it('把取消信号传给列表和摘要请求', async () => {
    mockedHttpGet
      .mockResolvedValueOnce({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
      .mockResolvedValueOnce({ totalRequests: 0, failedRequests: 0, successRate: 0, totalTokens: 0, maxDurationMs: 0 })
    const controller = new AbortController()
    const params = { page: 1, pageSize: 20, rangeType: 'day' }

    await listUsageLogs(params, controller.signal)
    await getUsageLogSummary(params, controller.signal)

    expect(mockedHttpGet).toHaveBeenNthCalledWith(
      1,
      '/api/admin/usage-logs/list?page=1&pageSize=20&rangeType=day',
      { signal: controller.signal }
    )
    expect(mockedHttpGet).toHaveBeenNthCalledWith(
      2,
      '/api/admin/usage-logs/summary?page=1&pageSize=20&rangeType=day',
      { signal: controller.signal }
    )
  })
})
