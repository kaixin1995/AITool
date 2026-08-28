import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet } from './http'
import { getDeveloperDetail } from './developer'

vi.mock('./http', () => ({
  httpGet: vi.fn()
}))

const mockedHttpGet = vi.mocked(httpGet)

beforeEach(() => {
  vi.clearAllMocks()
})

describe('Developer API 客户端', () => {
  it('把取消信号传给详情请求', async () => {
    mockedHttpGet.mockResolvedValueOnce({})
    const controller = new AbortController()

    await getDeveloperDetail('trace-id', true, controller.signal)

    expect(mockedHttpGet).toHaveBeenCalledWith(
      '/api/admin/developer/invocations/trace-id?summarize=true',
      { signal: controller.signal }
    )
  })
})
