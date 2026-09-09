import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet, httpPost } from './http'
import {
  aiFetchLatestVersion,
  createHeaderProfile,
  getHeaderProfile,
  previewHeaders,
  type AiLatestVersionResponse,
  type PreviewHeadersResponse
} from './headerProfiles'

// 回归背景：httpGet/httpPost 内部已把 ApiResponse.data 外层解包，resolve 的就是业务载荷。
// 早期实现写成 `const res = await httpPost<{ data: X }>(...); return res.data`（双重解包），
// 实际拿到 undefined，导致「实时求值 / AI 查最新版」按钮必挂。以下用例锁住正确契约。

vi.mock('./http', () => ({
  httpGet: vi.fn(),
  httpPost: vi.fn(),
  httpDelete: vi.fn(),
  httpPut: vi.fn()
}))

const mockedHttpGet = vi.mocked(httpGet)
const mockedHttpPost = vi.mocked(httpPost)

beforeEach(() => {
  vi.resetAllMocks()
})

describe('请求头方案 API 合同（ApiResponse 已由 http 层解包）', () => {
  it('previewHeaders 直接返回载荷，不再二次取 data', async () => {
    const payload: PreviewHeadersResponse = {
      previewHeaders: { 'User-Agent': 'Codex Desktop/0.153.3' },
      evaluatedCount: 1
    }
    mockedHttpPost.mockResolvedValueOnce(payload)

    await expect(previewHeaders({ headersJson: '{}' })).resolves.toEqual(payload)
  })

  it('aiFetchLatestVersion 返回含业务 success 字段的载荷', async () => {
    const payload: AiLatestVersionResponse = {
      success: true,
      upToDate: false,
      changed: true,
      currentVersion: '0.149.0',
      latestVersion: '0.153.3',
      note: null,
      currentHeadersJson: '{}',
      proposedHeadersJson: '{}'
    }
    mockedHttpPost.mockResolvedValueOnce(payload)

    await expect(aiFetchLatestVersion('profile-1')).resolves.toEqual(payload)
    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/developer/header-profiles/profile-1/ai-latest-version',
      {},
      { timeout: 120000 }
    )
  })

  it('aiFetchLatestVersion 透传业务失败（success=false）载荷', async () => {
    mockedHttpPost.mockResolvedValueOnce({
      success: false,
      error: 'AI 助手服务不可用',
      upToDate: false,
      changed: false,
      currentVersion: '0.149.0'
    } as AiLatestVersionResponse)

    const res = await aiFetchLatestVersion('profile-1')
    expect(res.success).toBe(false)
    expect(res.error).toBe('AI 助手服务不可用')
  })

  it('getHeaderProfile / createHeaderProfile 直接返回载荷', async () => {
    mockedHttpGet.mockResolvedValueOnce({ id: 'p1', key: 'CodexCli', name: 'Codex' })
    await expect(getHeaderProfile('p1')).resolves.toEqual({ id: 'p1', key: 'CodexCli', name: 'Codex' })

    mockedHttpPost.mockResolvedValueOnce({ id: 'p2', key: 'custom' })
    await expect(createHeaderProfile({ key: 'custom', name: 'n', isEnabled: true, sortOrder: 0 })).resolves.toEqual({
      id: 'p2',
      key: 'custom'
    })
  })
})
