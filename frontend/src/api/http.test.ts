import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  clearTokens,
  getAuthSessionKey,
  saveTokens,
  isApiResponse,
  isRequestCanceled,
  prepareRequestForTokenRefreshRetry,
  unwrapResponseData
} from './http'

afterEach(() => vi.unstubAllGlobals())

describe('HTTP 会话标识', () => {
  it('changes on login, token refresh and logout, even when tokens are reused', () => {
    const values = new Map<string, string>()
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key)
    })
    const tokens = { accessToken: 'access', refreshToken: 'refresh', accessTokenExpiresAt: '', refreshTokenExpiresAt: '' }
    const before = getAuthSessionKey()
    saveTokens(tokens)
    const login = getAuthSessionKey()
    expect(login).not.toBe(before)
    saveTokens(tokens)
    const refresh = getAuthSessionKey()
    expect(refresh).not.toBe(login)
    clearTokens()
    expect(getAuthSessionKey()).not.toBe(before)
    expect(getAuthSessionKey()).not.toBe(refresh)
    const afterLogout = getAuthSessionKey()
    values.set('aitool.accessToken', 'external')
    expect(getAuthSessionKey()).not.toBe(afterLogout)
  })
})

describe('HTTP 响应识别', () => {
  it('识别标准 ApiResponse 包装', () => {
    expect(isApiResponse({
      success: true,
      data: { id: '1' },
      message: null
    })).toBe(true)
    expect(isApiResponse({
      success: false,
      message: '操作失败',
      errorCode: 'failed'
    })).toBe(true)
  })

  it('识别主动取消的请求错误', () => {
    expect(isRequestCanceled({ code: 'ERR_CANCELED' })).toBe(true)
    expect(isRequestCanceled(new Error('网络失败'))).toBe(false)
  })

  it('刷新 access token 后重试时移除旧 Authorization', () => {
    const request = {
      headers: { Authorization: 'Bearer expired-access-token' }
    }

    prepareRequestForTokenRefreshRetry(request)

    expect(request.headers.Authorization).toBeUndefined()
  })

  it('不把带 success 字段的领域对象误判为 ApiResponse', () => {
    const resetCredits = {
      availableCount: 1,
      credits: [],
      success: true,
      error: null,
      rawJson: '{}'
    }

    expect(isApiResponse(resetCredits)).toBe(false)
    expect(unwrapResponseData(resetCredits)).toEqual(resetCredits)
  })
})
