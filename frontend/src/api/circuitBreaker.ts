import { httpGet, httpPost } from './http'

export interface CircuitBreakerRoute {
  routeId: string
  entryName: string
  upstreamModelName: string
  siteName: string
  isBlocked: boolean
  failureCount: number
  blockedUntil: string | null
  remainingSeconds: number | null
}

export async function getCircuitBreakerStates(): Promise<{ routes: CircuitBreakerRoute[] }> {
  return httpGet<{ routes: CircuitBreakerRoute[] }>('/api/admin/developer/invocations/circuit-breaker')
}

export async function resetCircuitBreaker(routeId: string): Promise<void> {
  await httpPost(`/api/admin/developer/invocations/circuit-breaker/${routeId}/reset`)
}

export async function resetAllCircuitBreakers(): Promise<{ resetCount: number }> {
  return httpPost<{ resetCount: number }>('/api/admin/developer/invocations/circuit-breaker/reset-all')
}
