import { httpGet, httpPost } from './http'
import type { AuthStatus, SetupRequest, TokenPair } from '@/types/api'

// GET /api/auth/status —— 不需要 token（登录前调用）
export async function getAuthStatus(): Promise<AuthStatus> {
  // 这个端点直接返回数据对象（非 ApiResponse 包装），httpGet 的 unwrap 会原样返回。
  return httpGet<AuthStatus>('/api/auth/status')
}

export async function login(password: string): Promise<TokenPair> {
  return httpPost<TokenPair>('/api/auth/login', { password })
}

export async function setup(password: string, confirmPassword: string): Promise<TokenPair> {
  const req: SetupRequest = { password, confirmPassword }
  return httpPost<TokenPair>('/api/auth/setup', req)
}

export async function logout(refreshToken: string): Promise<void> {
  await httpPost('/api/auth/logout', { refreshToken })
}
