import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet, httpPost } from './http'
import {
  getRouteSiteInstances,
  routeAvailabilityModes,
  routeAvailabilityOptions,
  saveRouteRules
} from './routes'

vi.mock('./http', () => ({
  httpGet: vi.fn(),
  httpPost: vi.fn()
}))

const mockedHttpGet = vi.mocked(httpGet)
const mockedHttpPost = vi.mocked(httpPost)

beforeEach(() => {
  vi.clearAllMocks()
})

describe('route availability contract', () => {
  it('uses the availability modes accepted by the route rules API', () => {
    expect(routeAvailabilityModes).toEqual([
      'AllDay',
      'AvailableOnly',
      'Unavailable'
    ])
  })

  it('provides labels for every accepted availability mode', () => {
    expect(routeAvailabilityOptions.map((option) => option.value)).toEqual(
      routeAvailabilityModes
    )
  })
})

describe('route rules API client', () => {
  it('loads the complete route site instance pool', async () => {
    mockedHttpGet.mockResolvedValueOnce([])

    await expect(getRouteSiteInstances()).resolves.toEqual([])
    expect(mockedHttpGet).toHaveBeenCalledWith(
      '/api/admin/route-rules/site-instances'
    )
  })

  it('returns the save message and preserves the complete rule payload', async () => {
    mockedHttpPost.mockResolvedValueOnce({
      message: '保存成功，调用中的模型会在当前请求结束后生效'
    })

    const rules = [{
      siteId: 'site-1',
      siteModelName: 'gpt-5.5-a',
      upstreamModelName: 'gpt-5.5',
      isEnabled: false,
      availabilityMode: 'Unavailable' as const,
      timeRangesJson: '[{"start":"09:00","end":"12:00"}]'
    }]

    await expect(saveRouteRules('chat prod', rules)).resolves.toEqual({
      message: '保存成功，调用中的模型会在当前请求结束后生效'
    })
    expect(mockedHttpPost).toHaveBeenCalledWith(
      '/api/admin/route-rules/save',
      {
        externalModelName: 'chat prod',
        rules
      }
    )
  })
})
