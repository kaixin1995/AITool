import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet, httpPost } from './http'
import {
  fetchOAuthModels,
  exportCredentials,
  getOAuthInspectionLastRun,
  getOAuthInspectionLogs,
  getOAuthInspectionStatus,
  getResetCredits,
  importCredential,
  importCredentialFiles,
  importSelectedGoogleModels,
  importSelectedOAuthModels,
  runOAuthInspection,
  startOAuth,
  startGoogleOAuth,
  startKimiDeviceFlow,
  startXaiDeviceFlow,
  pollKimiToken,
  pollXaiToken,
  importGoogleCredential,
  importKimiCredential,
  importKimiCredentialFiles,
  importXaiCredential,
  type OAuthModelSelection
} from './oauth'

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

describe('OAuth 与凭证合同', () => {
  it('保留专用设备码登录的 serverSessionId 并在轮询时发送，不混用通用 sessionId', async () => {
    mockedHttpPost.mockResolvedValueOnce({ deviceCode: 'kimi-code', serverSessionId: 'kimi-server' })
    const kimi = await startKimiDeviceFlow(undefined, { mode: 1 })
    await pollKimiToken(kimi.deviceCode, 'device-id', 'Kimi', kimi.serverSessionId)
    expect(mockedHttpPost).toHaveBeenLastCalledWith('/api/admin/kimi-accounts/poll-token', { deviceCode: 'kimi-code', deviceId: 'device-id', displayName: 'Kimi', serverSessionId: 'kimi-server' })
    mockedHttpPost.mockResolvedValueOnce({ deviceCode: 'xai-code', serverSessionId: 'xai-server' })
    const xai = await startXaiDeviceFlow({ mode: 0 })
    await pollXaiToken(xai.deviceCode, 'Grok', xai.serverSessionId)
    expect(mockedHttpPost).toHaveBeenLastCalledWith('/api/admin/xai-accounts/poll-token', { deviceCode: 'xai-code', displayName: 'Grok', serverSessionId: 'xai-server' })
  })
  it('旧设备码轮询省略 serverSessionId 时仍保留原合同', async () => {
    await pollKimiToken('old-kimi', 'device', 'Kimi')
    expect(mockedHttpPost).toHaveBeenLastCalledWith('/api/admin/kimi-accounts/poll-token', { deviceCode: 'old-kimi', deviceId: 'device', displayName: 'Kimi' })
    await pollXaiToken('old-xai', 'Grok')
    expect(mockedHttpPost).toHaveBeenLastCalledWith('/api/admin/xai-accounts/poll-token', { deviceCode: 'old-xai', displayName: 'Grok' })
  })
  it('所有登录入口使用精确 networkSelection 数字枚举合同', async () => {
    const networkSelection = { mode: 2 as const, proxyProfileId: 'profile-id' }
    await startOAuth(networkSelection)
    await startGoogleOAuth('Antigravity', networkSelection)
    await startKimiDeviceFlow(undefined, networkSelection)
    await startXaiDeviceFlow(networkSelection)
    for (const [, body] of mockedHttpPost.mock.calls) expect(body).toMatchObject({ networkSelection })
  })

  it('所有原始凭证导入通过查询传网络选择，不改写凭证内容', async () => {
    mockedHttpPost.mockResolvedValue({ successes: [], failures: [] })
    const networkSelection = { mode: 1 as const }
    await importCredential('{"refresh_token":"secret"}', networkSelection)
    await importKimiCredential('{"refresh_token":"secret"}', networkSelection)
    await importGoogleCredential('Antigravity', '{"refresh_token":"secret"}', networkSelection)
    await importXaiCredential('{"refresh_token":"secret"}', networkSelection)
    for (const [url, body] of mockedHttpPost.mock.calls) {
      expect(url).toContain('networkMode=1')
      expect(typeof body === 'string' ? JSON.parse(body) : body).toEqual({ refresh_token: 'secret' })
    }
  })

  it('文件导入保留 multipart 且网络策略不写入凭证文件', async () => {
    mockedHttpPost.mockResolvedValue({ successes: [], failures: [] })
    await importCredentialFiles([new File(['{}'], 'a.json')], { mode: 2, proxyProfileId: 'stable-id' })
    await importKimiCredentialFiles([new File(['{}'], 'b.json')], { mode: 0 })
    for (const [url, body] of mockedHttpPost.mock.calls) {
      expect(url).toContain('networkMode=')
      expect(body).toBeInstanceOf(FormData)
      expect((body as FormData).get('networkSelection')).toBeNull()
    }
  })

  it('启动 OAuth 时发送后端要求的 JSON 请求体', async () => {
    mockedHttpPost.mockResolvedValueOnce({
      url: 'https://example.test/oauth',
      state: 'state-1'
    })

    await startOAuth()

    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/oauth/start-oauth',
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
      '/api/admin/oauth/import-credential?name=imported.json',
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
    expect(url).toBe('/api/admin/oauth/import-credential')
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
      '/api/admin/oauth/accounts/export-credentials',
      { accountIds: ['account-1'] }
    )
  })
})

describe('OAuth 模型 API 合同', () => {
  it('保留后端返回的模型名称、别名与现有映射状态（裸数组兼容）', async () => {
    const models = [{
      remoteModelName: 'gpt-5-codex',
      displayName: 'GPT-5 Codex',
      existingMappingId: 'mapping-1',
      isEnabled: false,
      existingDisplayName: 'Codex 生产模型'
    }]
    mockedHttpGet.mockResolvedValueOnce(models)

    await expect(fetchOAuthModels('account-1')).resolves.toEqual({ models })
    expect(mockedHttpGet).toHaveBeenCalledWith(
      '/api/admin/oauth/accounts/account-1/fetch-models'
    )
  })

  it('透传 Codex 端点新合同中的远端目录刷新信息', async () => {
    const payload = {
      catalogRefreshed: true,
      catalogChanged: true,
      catalogNote: '远端模型目录已刷新',
      models: [{
        remoteModelName: 'gpt-6-astra',
        displayName: 'gpt-6-astra',
        existingMappingId: null,
        isEnabled: false,
        existingDisplayName: null
      }]
    }
    mockedHttpGet.mockResolvedValueOnce(payload)

    await expect(fetchOAuthModels('account-1')).resolves.toEqual(payload)
  })

  it('按 selections 合同提交显示名称和选择状态', async () => {
    mockedHttpPost.mockResolvedValueOnce({ importedCount: 1 })
    const selections: OAuthModelSelection[] = [{
      remoteModelName: 'gpt-5-codex',
      displayName: 'Codex 生产模型',
      selected: true
    }]

    await importSelectedOAuthModels('account-1', selections)

    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/oauth/accounts/account-1/import-selected-models',
      { selections }
    )
  })

  it('Google 模型同步提交完整清单，包含未勾选状态', async () => {
    mockedHttpPost.mockResolvedValueOnce({ imported: 1, disabled: 1 })
    const selections: OAuthModelSelection[] = [
      { remoteModelName: 'gemini-3-pro', displayName: 'Gemini 3 Pro', selected: true },
      { remoteModelName: 'claude-sonnet-4-6', displayName: 'Claude Sonnet 4.6', selected: false }
    ]

    await importSelectedGoogleModels('google-account-1', selections)

    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/google-accounts/accounts/google-account-1/import-selected-models',
      { models: selections }
    )
  })
})

describe('OAuth 巡检 API 合同', () => {
  it('使用正确的巡检状态路径', async () => {
    mockedHttpGet.mockResolvedValueOnce({ isRunning: false })

    await getOAuthInspectionStatus()

    expect(mockedHttpGet).toHaveBeenCalledWith(
      '/api/admin/oauth/inspection/status',
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

    await expect(runOAuthInspection(true)).resolves.toEqual(result)
    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/oauth/inspection/run?force=true'
    )
  })

  it('分别加载上次巡检和巡检日志', async () => {
    mockedHttpGet
      .mockResolvedValueOnce(null)
      .mockResolvedValueOnce([])

    await expect(getOAuthInspectionLastRun()).resolves.toBeNull()
    await expect(getOAuthInspectionLogs()).resolves.toEqual([])
    expect(mockedHttpGet).toHaveBeenNthCalledWith(
      1,
      '/api/admin/oauth/inspection/last-run',
      { skipErrorNotify: true }
    )
    expect(mockedHttpGet).toHaveBeenNthCalledWith(
      2,
      '/api/admin/oauth/inspection/logs',
      { skipErrorNotify: true }
    )
  })
})

describe('OAuth 重置信用 API 合同', () => {
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
