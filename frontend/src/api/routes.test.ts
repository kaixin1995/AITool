import { beforeEach, describe, expect, it, vi } from 'vitest'
import { httpGet, httpPost } from './http'
import {
  getRouteSiteInstances,
  getRouteReferenceTargets,
  deleteRouteEntry,
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
  it('loads reference targets for the encoded parent entry', async () => {
    mockedHttpGet.mockResolvedValueOnce([])
    await expect(getRouteReferenceTargets('chat / prod')).resolves.toEqual([])
    expect(mockedHttpGet).toHaveBeenCalledWith('/api/admin/route-rules/reference-targets?parentEntryName=chat%20%2F%20prod')
  })

  it('never confirms referenced deletion implicitly', async () => {
    await deleteRouteEntry('entry')
    expect(mockedHttpPost).toHaveBeenLastCalledWith(
      '/api/admin/route-rules/entries/delete', { entryName: 'entry' }, { skipErrorNotify: true }
    )
    await deleteRouteEntry('entry', true)
    expect(mockedHttpPost).toHaveBeenLastCalledWith(
      '/api/admin/route-rules/entries/delete', { entryName: 'entry', confirmReferenced: true }, { skipErrorNotify: true }
    )
  })

  it('propagates the initial deletion conflict without retrying or confirming', async () => {
    const conflict = { status: 409, message: 'referenced' }
    mockedHttpPost.mockRejectedValueOnce(conflict)
    await expect(deleteRouteEntry('entry')).rejects.toBe(conflict)
    expect(mockedHttpPost).toHaveBeenCalledTimes(1)
  })

  it('sends reference-only payloads and advertises support', async () => {
    const rules = [{
      targetKind: 'RouteEntry' as const,
      targetRouteEntryId: 'entry-id',
      targetRouteEntryName: 'pool model',
      isEnabled: true,
      availabilityMode: 'AllDay' as const,
      timeRangesJson: ''
    }]
    await saveRouteRules('auto', rules)
    expect(mockedHttpPost).toHaveBeenCalledWith('/api/admin/route-rules/save', {
      externalModelName: 'auto', rules, supportsRouteReferences: true
    })
  })
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
        rules,
        supportsRouteReferences: true
      }
    )
  })
})
