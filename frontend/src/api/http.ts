import axios, { type AxiosInstance, type AxiosRequestConfig, type AxiosResponse } from 'axios'
import type { ApiResponse, TokenPair } from '@/types/api'

const ACCESS_TOKEN_KEY = 'aitool.accessToken'
const REFRESH_TOKEN_KEY = 'aitool.refreshToken'

// token 读写（localStorage，刷新页面后仍保留）
export function getAccessToken(): string {
  return localStorage.getItem(ACCESS_TOKEN_KEY) ?? ''
}
export function getRefreshToken(): string {
  return localStorage.getItem(REFRESH_TOKEN_KEY) ?? ''
}
export function saveTokens(tokens: TokenPair): void {
  localStorage.setItem(ACCESS_TOKEN_KEY, tokens.accessToken)
  localStorage.setItem(REFRESH_TOKEN_KEY, tokens.refreshToken)
}
export function clearTokens(): void {
  localStorage.removeItem(ACCESS_TOKEN_KEY)
  localStorage.removeItem(REFRESH_TOKEN_KEY)
}

// 全局消息回调（由 App.vue 注入，避免这里直接依赖 naive-ui 的 useMessage）。
// 注入失败时降级到 console.warn。
let messageHandler: ((type: 'success' | 'error' | 'warning', content: string) => void) | null = null
export function setMessageHandler(fn: typeof messageHandler): void {
  messageHandler = fn
}
function notify(type: 'success' | 'error' | 'warning', content: string): void {
  if (messageHandler) {
    messageHandler(type, content)
  } else {
    console.warn(`[${type}] ${content}`)
  }
}

const instance: AxiosInstance = axios.create({
  baseURL: '',
  timeout: 30000,
  headers: { 'Content-Type': 'application/json' }
})

// 请求拦截器：自动注入 Authorization Bearer
instance.interceptors.request.use((config) => {
  const token = getAccessToken()
  if (token && !config.headers.Authorization) {
    config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// 刷新 token 的并发保护：同一时刻只允许一个刷新请求，其他请求排队等待结果。
let refreshPromise: Promise<boolean> | null = null
async function refreshAccessToken(): Promise<boolean> {
  if (refreshPromise) return refreshPromise
  const refreshToken = getRefreshToken()
  if (!refreshToken) return false

  refreshPromise = (async () => {
    try {
      const resp = await axios.post<TokenPair & { success: boolean }>('/api/auth/refresh', { refreshToken })
      if (resp.data?.success && resp.data.accessToken) {
        saveTokens({
          accessToken: resp.data.accessToken,
          refreshToken: resp.data.refreshToken,
          accessTokenExpiresAt: resp.data.accessTokenExpiresAt,
          refreshTokenExpiresAt: resp.data.refreshTokenExpiresAt
        })
        return true
      }
      return false
    } catch {
      return false
    } finally {
      refreshPromise = null
    }
  })()

  return refreshPromise
}

// 响应拦截器：统一解包 ApiResponse + 401 自动刷新重试 + 错误提示
instance.interceptors.response.use(
  // 成功响应（HTTP 2xx）
  async (response: AxiosResponse) => {
    // 非标准 ApiResponse（老 API 直接返回数据对象，或导出等纯数据端点）：原样返回。
    // 判据：response.data 没有 success 字段，或 success 字段不是 boolean。
    const data = response.data
    if (data === null || typeof data !== 'object' || typeof data.success !== 'boolean') {
      return response
    }

    // 标准 ApiResponse：业务失败时抛错（让调用方走 catch），成功时返回 data 字段。
    const apiResp = data as ApiResponse
    if (!apiResp.success) {
      const err = new ApiError(apiResp.message ?? '操作失败', apiResp.errorCode ?? '', response.status)
      notify('error', err.message)
      return Promise.reject(err)
    }

    // 成功：有 message 时弹成功提示（除非调用方关闭）。
    // 注意：这里把整个 response 返回，调用方通过 .then(r => r.data.data) 取业务数据。
    // 为了简化调用方代码，我们包装一层：见下方 request 辅助函数。
    return response
  },
  // 错误响应（HTTP 4xx/5xx）
  async (error) => {
    const originalRequest = error.config as (AxiosRequestConfig & { _retried?: boolean }) | undefined
    const status = error.response?.status

    // 401 且未重试过：尝试刷新 token 后重试一次。
    if (status === 401 && originalRequest && !originalRequest._retried) {
      originalRequest._retried = true
      const ok = await refreshAccessToken()
      if (ok) {
        return instance(originalRequest)
      }
      // 刷新失败：清 token，跳登录。
      clearTokens()
      // 避免在登录页本身触发跳转死循环。
      if (!window.location.pathname.startsWith('/login')) {
        const returnUrl = encodeURIComponent(window.location.pathname + window.location.search)
        window.location.href = `/login?returnUrl=${returnUrl}`
      }
      return Promise.reject(new ApiError('登录已过期，请重新登录', 'unauthenticated', 401))
    }

    // 401 已重试或非 401：提取错误信息。
    const respData = error.response?.data
    const message =
      (respData && typeof respData === 'object' && (respData as ApiResponse).message) ||
      respData?.error?.message ||
      error.message ||
      '请求失败'
    const errorCode =
      (respData && typeof respData === 'object' && (respData as ApiResponse).errorCode) ||
      respData?.error?.code ||
      ''
    const apiError = new ApiError(message, errorCode, status ?? 0)
    // 401 不重复弹提示（已在上方处理跳转）。
    // skipErrorNotify：调用方声明自行处理该错误（如可选功能未启用），不弹全局提示。
    if (status !== 401 && !originalRequest?.skipErrorNotify) {
      notify('error', message)
    }
    return Promise.reject(apiError)
  }
)

// 业务错误类型，调用方可按 errorCode 精确分支。
export class ApiError extends Error {
  errorCode: string
  status: number
  constructor(message: string, errorCode: string, status: number) {
    super(message)
    this.name = 'ApiError'
    this.errorCode = errorCode
    this.status = status
  }
}

// 扩展 axios 配置：skipErrorNotify=true 时，响应拦截器不弹全局错误提示，
// 供调用方自行处理「预期内」的错误（如可选功能未启用返回 404）。
declare module 'axios' {
  interface AxiosRequestConfig {
    skipErrorNotify?: boolean
  }
}

// —— 调用便捷函数 ——
// http.get<T>('/api/admin/sites') 返回 Promise<T>（已自动解包 ApiResponse.data）。
// 对于老 API（直接返回数组/对象，非 ApiResponse 包装），同样返回业务数据。
export async function httpGet<T = unknown>(url: string, config?: AxiosRequestConfig): Promise<T> {
  const resp = await instance.get(url, config)
  return unwrap<T>(resp.data)
}
export async function httpPost<T = unknown>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<T> {
  const resp = await instance.post(url, data, config)
  return unwrap<T>(resp.data)
}
export async function httpPut<T = unknown>(url: string, data?: unknown, config?: AxiosRequestConfig): Promise<T> {
  const resp = await instance.put(url, data, config)
  return unwrap<T>(resp.data)
}
export async function httpDelete<T = unknown>(url: string, config?: AxiosRequestConfig): Promise<T> {
  const resp = await instance.delete(url, config)
  return unwrap<T>(resp.data)
}

// 解包：如果是标准 ApiResponse 返回 .data，否则原样返回。
function unwrap<T>(raw: unknown): T {
  if (raw && typeof raw === 'object' && typeof (raw as ApiResponse).success === 'boolean') {
    return (raw as ApiResponse<T>).data as T
  }
  return raw as T
}

export default instance
