import { describe, expect, it } from 'vitest'
import { isApiResponse, isRequestCanceled, unwrapResponseData } from './http'

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
