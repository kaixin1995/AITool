import { defineStore } from 'pinia'
import { ref } from 'vue'
import * as authApi from '@/api/auth'
import { clearTokens, getAccessToken, getRefreshToken, saveTokens } from '@/api/http'
import type { AuthStatus } from '@/types/api'

export const useAuthStore = defineStore('auth', () => {
  const accessToken = ref<string>(getAccessToken())
  const status = ref<AuthStatus | null>(null)
  const loading = ref(false)

  const isAuthenticated = () => !!accessToken.value

  async function fetchStatus(): Promise<AuthStatus> {
    const s = await authApi.getAuthStatus()
    status.value = s
    return s
  }

  async function login(password: string): Promise<void> {
    loading.value = true
    try {
      const tokens = await authApi.login(password)
      saveTokens(tokens)
      accessToken.value = tokens.accessToken
    } finally {
      loading.value = false
    }
  }

  async function setup(password: string, confirmPassword: string): Promise<void> {
    loading.value = true
    try {
      const tokens = await authApi.setup(password, confirmPassword)
      saveTokens(tokens)
      accessToken.value = tokens.accessToken
    } finally {
      loading.value = false
    }
  }

  async function logout(): Promise<void> {
    const refreshToken = getRefreshToken()
    try {
      await authApi.logout(refreshToken)
    } catch {
      // 即使登出请求失败也清除本地 token。
    }
    clearTokens()
    accessToken.value = ''
  }

  return { accessToken, status, loading, isAuthenticated, fetchStatus, login, setup, logout }
})
