import { httpGet, httpPost } from './http'

export interface CircuitBreakerRoute {
  routeId: string
  circuitKey: string
  entryName: string
  upstreamModelName: string
  siteName: string
  siteKeyId?: string | null
  isBlocked: boolean
  failureCount: number
  blockedUntil: string | null
  remainingSeconds: number | null
}

export async function getCircuitBreakerStates(): Promise<{ routes: CircuitBreakerRoute[] }> {
  return httpGet<{ routes: CircuitBreakerRoute[] }>('/api/admin/developer/invocations/circuit-breaker')
}

// 解除熔断使用 circuitKey（多 Key 候选为合成 Guid，兼容候选等同 RouteId）。
export async function resetCircuitBreaker(circuitKey: string): Promise<void> {
  await httpPost(`/api/admin/developer/invocations/circuit-breaker/${circuitKey}/reset`)
}

export async function resetAllCircuitBreakers(): Promise<{ resetCount: number }> {
  return httpPost<{ resetCount: number }>('/api/admin/developer/invocations/circuit-breaker/reset-all')
}
