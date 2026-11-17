import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('./http', () => ({
  getAuthSessionKey: vi.fn(() => 'session-a'),
  httpGet: vi.fn(), httpPost: vi.fn(), httpPut: vi.fn(), httpDelete: vi.fn()
}))

beforeEach(() => {
  vi.resetAllMocks()
  vi.resetModules()
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-01-01T00:00:00Z'))
})
afterEach(() => { vi.useRealTimers(); vi.clearAllMocks() })

describe('device OAuth vendor requests', () => {
  it('shares a pending request and caches an empty success through account refreshes', async () => {
    const { httpGet } = await import('./http')
    const { fetchDeviceOAuthVendors } = await import('./deviceOauth')
    let resolve!: (value: unknown) => void
    vi.mocked(httpGet).mockReturnValueOnce(new Promise(r => { resolve = r }))

    const mounted = fetchDeviceOAuthVendors()
    const accountRefresh = fetchDeviceOAuthVendors()
    expect(accountRefresh).toBe(mounted)
    expect(httpGet).toHaveBeenCalledTimes(1)
    resolve([])
    await expect(mounted).resolves.toEqual([])
    await expect(accountRefresh).resolves.toEqual([])
    for (let i = 0; i < 5; i++) {
      await vi.advanceTimersByTimeAsync(10_000)
      await expect(fetchDeviceOAuthVendors()).resolves.toEqual([])
    }
    expect(httpGet).toHaveBeenCalledTimes(1)

    const vendors = [{ vendorKey: 'example', label: 'Example', chatSupported: true, modelFetchSupported: true }]
    vi.mocked(httpGet).mockResolvedValueOnce(vendors)
    await vi.advanceTimersByTimeAsync(10_000)
    await expect(fetchDeviceOAuthVendors()).resolves.toEqual(vendors)
    expect(httpGet).toHaveBeenCalledTimes(2)
  })

  it('clears a failed in-flight request so a later account refresh can recover', async () => {
    const { httpGet } = await import('./http')
    const { fetchDeviceOAuthVendors } = await import('./deviceOauth')
    let reject!: (reason: Error) => void
    vi.mocked(httpGet).mockReturnValueOnce(new Promise((_resolve, r) => { reject = r }))
    const mounted = fetchDeviceOAuthVendors()
    const accountRefresh = fetchDeviceOAuthVendors()
    expect(accountRefresh).toBe(mounted)
    reject(new Error('unavailable'))
    await expect(mounted).rejects.toThrow('unavailable')
    await expect(accountRefresh).rejects.toThrow('unavailable')
    expect(httpGet).toHaveBeenCalledTimes(1)

    const vendors = [{ vendorKey: 'example', label: 'Example', chatSupported: true, modelFetchSupported: true }]
    vi.mocked(httpGet).mockResolvedValueOnce(vendors)
    await expect(fetchDeviceOAuthVendors()).resolves.toEqual(vendors)
    expect(httpGet).toHaveBeenCalledTimes(2)
  })

  it('invalidates an empty result when the session changes', async () => {
    const { getAuthSessionKey, httpGet } = await import('./http')
    const { fetchDeviceOAuthVendors } = await import('./deviceOauth')
    vi.mocked(httpGet).mockResolvedValueOnce([]).mockResolvedValueOnce([{ vendorKey: 'new' }])
    await expect(fetchDeviceOAuthVendors()).resolves.toEqual([])
    vi.mocked(getAuthSessionKey).mockReturnValue('session-b')
    await expect(fetchDeviceOAuthVendors()).resolves.toEqual([{ vendorKey: 'new' }])
    expect(httpGet).toHaveBeenCalledTimes(2)
  })

  it('isolates pending requests and ignores an old completion after a session change', async () => {
    const { getAuthSessionKey, httpGet } = await import('./http')
    const { fetchDeviceOAuthVendors } = await import('./deviceOauth')
    let resolveOld!: (value: unknown) => void
    let resolveNew!: (value: unknown) => void
    vi.mocked(httpGet)
      .mockReturnValueOnce(new Promise(r => { resolveOld = r }))
      .mockReturnValueOnce(new Promise(r => { resolveNew = r }))
    const oldRequest = fetchDeviceOAuthVendors()
    vi.mocked(getAuthSessionKey).mockReturnValue('session-b')
    const newRequest = fetchDeviceOAuthVendors()
    expect(newRequest).not.toBe(oldRequest)
    resolveOld([])
    await oldRequest
    expect(fetchDeviceOAuthVendors()).toBe(newRequest)
    resolveNew([{ vendorKey: 'new' }])
    await newRequest
    vi.mocked(httpGet).mockResolvedValueOnce([{ vendorKey: 'later' }])
    await expect(fetchDeviceOAuthVendors()).resolves.toEqual([{ vendorKey: 'later' }])
    expect(httpGet).toHaveBeenCalledTimes(3)
  })

  it('does not cache an old response even if no new session request has started yet', async () => {
    const { getAuthSessionKey, httpGet } = await import('./http')
    const { fetchDeviceOAuthVendors } = await import('./deviceOauth')
    let resolveOld!: (value: unknown) => void
    vi.mocked(httpGet).mockReturnValueOnce(new Promise(r => { resolveOld = r }))
    const oldRequest = fetchDeviceOAuthVendors()
    vi.mocked(getAuthSessionKey).mockReturnValue('session-b')
    resolveOld([])
    await oldRequest
    vi.mocked(httpGet).mockResolvedValueOnce([{ vendorKey: 'new' }])
    await expect(fetchDeviceOAuthVendors()).resolves.toEqual([{ vendorKey: 'new' }])
    expect(httpGet).toHaveBeenCalledTimes(2)
  })

  it('does not cache non-empty results for independent later callers', async () => {
    const { httpGet } = await import('./http')
    const { fetchDeviceOAuthVendors } = await import('./deviceOauth')
    const vendors = [{ vendorKey: 'example', label: 'Example', chatSupported: true, modelFetchSupported: true }]
    vi.mocked(httpGet).mockResolvedValue(vendors)
    await fetchDeviceOAuthVendors()
    await fetchDeviceOAuthVendors()
    expect(httpGet).toHaveBeenCalledTimes(2)
  })
})
