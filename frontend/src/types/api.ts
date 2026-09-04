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
    /** 通用 OAuth 账号功能开关。 */
    oauthEnabled?: boolean
    /** 通用账号额度巡检开关。 */
    oauthInspectionEnabled?: boolean
    /** 兼容尚未升级的旧后端。 */
    codexEnabled?: boolean
    codexInspectionEnabled?: boolean
    developerEnabled: boolean
    /** 调试工具各功能页可用性（总闸开启时可单独禁用）；旧后端无此字段时默认全部可用。 */
    developerTabs?: {
      invocations: boolean
      diagnosticDumps: boolean
      simulator: boolean
      protocolDiagnostics: boolean
      sqlMigrations: boolean
    }
  }
}
