import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet, httpPost } from './http'
import {
  fetchCodexModels,
  exportCredentials,
  getCodexInspectionLastRun,
  getCodexInspectionLogs,
  getCodexInspectionStatus,
  getResetCredits,
  importCredential,
  importCredentialFiles,
  importSelectedCodexModels,
  runCodexInspection,
  startCodexOAuth,
  type CodexModelSelection
} from './codex'

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

describe('Codex OAuth 与凭证合同', () => {
  it('启动 OAuth 时发送后端要求的 JSON 请求体', async () => {
    mockedHttpPost.mockResolvedValueOnce({
      url: 'https://example.test/oauth',
      state: 'state-1'
    })

    await startCodexOAuth()

    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/codex/start-oauth',
      {}
    )
  })

  it('返回凭证导入的成功和失败明细', async () => {
    const result = {
      successes: [],
      failures: [{
        fileName: 'imported.json',
        error: '缺少 refresh_token'
      }]
    }
    mockedHttpPost.mockResolvedValueOnce(result)

    await expect(importCredential('{"type":"codex"}')).resolves.toEqual(result)
    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/codex/import-credential?name=imported.json',
      { type: 'codex' }
    )
  })

  it('成功响应省略 failures 时补为空数组', async () => {
    mockedHttpPost.mockResolvedValueOnce({
      successes: [{ id: 'account-1' }]
    })

    await expect(importCredential('{"type":"codex"}')).resolves.toMatchObject({
      successes: [{ id: 'account-1' }],
      failures: []
    })
  })

  it('使用 multipart 表单批量导入凭证文件', async () => {
    const files = [
      new File(['{}'], 'a.json', { type: 'application/json' }),
      new File(['{}'], 'b.json', { type: 'application/json' })
    ]
    mockedHttpPost.mockResolvedValueOnce({ successes: [], failures: [] })

    await importCredentialFiles(files)

    const [url, body, config] = mockedHttpPost.mock.calls[0]
    expect(url).toBe('/api/admin/codex/import-credential')
    expect(body).toBeInstanceOf(FormData)
    expect((body as FormData).getAll('files')).toHaveLength(2)
    expect(config).toEqual({
      headers: { 'Content-Type': 'multipart/form-data' }
    })
  })

  it('返回选中账号的导出凭证，不在 API 层合并下载', async () => {
    const result = {
      credentials: [{ email: 'admin@example.test', access_token: 'token' }]
    }
    mockedHttpPost.mockResolvedValueOnce(result)

    await expect(exportCredentials(['account-1'])).resolves.toEqual(result)
    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/codex/accounts/export-credentials',
      { accountIds: ['account-1'] }
    )
  })
})

describe('Codex 模型 API 合同', () => {
  it('保留后端返回的模型名称、别名与现有映射状态', async () => {
    const models = [{
      remoteModelName: 'gpt-5-codex',
      displayName: 'GPT-5 Codex',
      existingMappingId: 'mapping-1',
      isEnabled: false,
      existingDisplayName: 'Codex 生产模型'
    }]
    mockedHttpGet.mockResolvedValueOnce(models)

    await expect(fetchCodexModels('account-1')).resolves.toEqual(models)
    expect(mockedHttpGet).toHaveBeenCalledWith(
      '/api/admin/codex/accounts/account-1/fetch-models'
    )
  })

  it('按 selections 合同提交显示名称和选择状态', async () => {
    mockedHttpPost.mockResolvedValueOnce({ importedCount: 1 })
    const selections: CodexModelSelection[] = [{
      remoteModelName: 'gpt-5-codex',
      displayName: 'Codex 生产模型',
      selected: true
    }]

    await importSelectedCodexModels('account-1', selections)

    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/codex/accounts/account-1/import-selected-models',
      { selections }
    )
  })
})

describe('Codex 巡检 API 合同', () => {
  it('使用正确的巡检状态路径', async () => {
    mockedHttpGet.mockResolvedValueOnce({ isRunning: false })

    await getCodexInspectionStatus()

    expect(mockedHttpGet).toHaveBeenCalledWith(
      '/api/admin/codex/inspection/status',
      { skipErrorNotify: true }
    )
  })

  it('区分普通巡检和强制真实巡检', async () => {
    const result = {
      isRunning: false,
      forcedRefresh: true,
      startedAt: '2026-07-30T00:00:00Z',
      finishedAt: '2026-07-30T00:00:01Z',
      accounts: [],
      keepCount: 0,
      disableCount: 0,
      enableCount: 0,
      cacheCount: 0,
      realRefreshCount: 0,
      autoTriggered: false
    }
    mockedHttpPost.mockResolvedValueOnce(result)

    await expect(runCodexInspection(true)).resolves.toEqual(result)
    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/codex/inspection/run?force=true'
    )
  })

  it('分别加载上次巡检和巡检日志', async () => {
    mockedHttpGet
      .mockResolvedValueOnce(null)
      .mockResolvedValueOnce([])

    await expect(getCodexInspectionLastRun()).resolves.toBeNull()
    await expect(getCodexInspectionLogs()).resolves.toEqual([])
    expect(mockedHttpGet).toHaveBeenNthCalledWith(
      1,
      '/api/admin/codex/inspection/last-run',
      { skipErrorNotify: true }
    )
    expect(mockedHttpGet).toHaveBeenNthCalledWith(
      2,
      '/api/admin/codex/inspection/logs',
      { skipErrorNotify: true }
    )
  })
})

describe('Codex 重置信用 API 合同', () => {
  it('返回 credits 明细及领域级成功状态', async () => {
    const info = {
      availableCount: 1,
      credits: [{
        id: 'credit-1',
        status: 'available',
        grantedAt: '2026-07-01T00:00:00Z',
        expiresAt: '2026-08-01T00:00:00Z'
      }],
      success: true,
      error: null,
      rawJson: '{}'
    }
    mockedHttpGet.mockResolvedValueOnce(info)

    await expect(getResetCredits('account-1')).resolves.toEqual(info)
  })
})
