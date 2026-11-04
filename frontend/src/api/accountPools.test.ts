import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpPost, notify } from './http'
import { syncAllAccountPools } from './accountPools'

vi.mock('./http', () => ({ httpGet: vi.fn(), httpPost: vi.fn(), httpPut: vi.fn(), httpDelete: vi.fn(), notify: vi.fn() }))

beforeEach(() => vi.resetAllMocks())

describe('syncAllAccountPools', () => {
  it('does not warn when synchronization succeeds', async () => {
    vi.mocked(httpPost).mockResolvedValueOnce(undefined)
    await expect(syncAllAccountPools()).resolves.toBeUndefined()
    expect(httpPost).toHaveBeenCalledWith('/api/admin/account-pools/sync-all')
    expect(notify).not.toHaveBeenCalled()
  })

  it('warns with a manual retry path and preserves the rejection on failure', async () => {
    const error = new Error('network error')
    vi.mocked(httpPost).mockRejectedValueOnce(error)
    await expect(syncAllAccountPools()).rejects.toBe(error)
    expect(notify).toHaveBeenCalledWith('warning', expect.stringContaining('号池同步失败'))
    expect(notify).toHaveBeenCalledWith('warning', expect.stringContaining('路由管理'))
    expect(notify).toHaveBeenCalledWith('warning', expect.stringContaining('逐个点击「同步」'))
    expect(notify).toHaveBeenCalledWith('warning', expect.stringContaining('此前操作的结果不受影响'))
  })
})
