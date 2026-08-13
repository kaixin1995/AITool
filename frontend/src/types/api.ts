// 后端统一响应格式（新增 API 使用）。老 API 直接返回数据对象，由 http.ts 适配层统一解包。
export interface ApiResponse<T = unknown> {
  success: boolean
  data: T
  message?: string | null
  errorCode?: string | null
}

// 认证相关
export interface LoginRequest {
  password: string
}
export interface SetupRequest {
  password: string
  confirmPassword: string
}
export interface TokenPair {
  accessToken: string
  refreshToken: string
  accessTokenExpiresAt: string
  refreshTokenExpiresAt: string
}
export interface AuthStatus {
  hasPassword: boolean
  isAuthenticated: boolean
  // 应用版本号（对应后端 Program.cs applicationVersion）
  version?: string
  // 程序集编译时间（ISO 字符串），用于确认运行的程序是否是最新版本
  buildTime?: string
  features: {
    codexEnabled: boolean
    codexInspectionEnabled: boolean
    developerEnabled: boolean
  }
}
