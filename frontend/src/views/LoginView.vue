<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NCard, NForm, NFormItem, NInput, NButton, NSpace, NAlert, useMessage } from 'naive-ui'
import { useAuthStore } from '@/stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const message = useMessage()

const password = ref('')
const confirmPassword = ref('')
const submitting = ref(false)
const errorMsg = ref('')

// 是否为首次设置模式（未设密码）。
const isSetupMode = computed(() => auth.status?.hasPassword === false)

const returnUrl = computed(() => {
  const r = route.query.returnUrl
  return typeof r === 'string' && r.startsWith('/') ? r : '/'
})

onMounted(async () => {
  if (!auth.status) {
    try {
      await auth.fetchStatus()
    } catch {
      // 忽略，下方逻辑降级为登录模式。
    }
  }
  // 已登录则跳走。
  if (auth.isAuthenticated()) {
    router.replace(returnUrl.value)
  }
})

async function handleSubmit(): Promise<void> {
  errorMsg.value = ''
  if (!password.value) {
    errorMsg.value = '请输入密码'
    return
  }

  if (isSetupMode.value) {
    if (password.value.length < 6) {
      errorMsg.value = '密码长度至少 6 位'
      return
    }
    if (password.value !== confirmPassword.value) {
      errorMsg.value = '两次输入的密码不一致'
      return
    }
  }

  submitting.value = true
  try {
    if (isSetupMode.value) {
      await auth.setup(password.value, confirmPassword.value)
      message.success('密码设置成功')
    } else {
      await auth.login(password.value)
      message.success('登录成功')
    }
    router.replace(returnUrl.value)
  } catch (e) {
    errorMsg.value = (e as Error).message || '操作失败'
  } finally {
    submitting.value = false
  }
}
</script>

<template>
  <div class="login-container">
    <NCard class="login-card" :bordered="false" size="large">
      <div class="login-header">
        <h2 class="login-title">AI Tool 管理后台</h2>
        <p class="login-subtitle">{{ isSetupMode ? '首次使用，请设置管理密码' : '请输入管理密码登录' }}</p>
      </div>

      <NAlert v-if="errorMsg" type="error" :show-icon="true" style="margin-bottom: 16px">
        {{ errorMsg }}
      </NAlert>

      <NForm @keyup.enter="handleSubmit">
        <NFormItem label="密码">
          <NInput
            v-model:value="password"
            type="password"
            show-password-on="click"
            :placeholder="isSetupMode ? '设置管理密码（至少 6 位）' : '请输入密码'"
          />
        </NFormItem>
        <NFormItem v-if="isSetupMode" label="确认密码">
          <NInput
            v-model:value="confirmPassword"
            type="password"
            show-password-on="click"
            placeholder="再次输入密码"
          />
        </NFormItem>
        <NButton
          type="primary"
          block
          :loading="submitting"
          style="margin-top: 8px"
          @click="handleSubmit"
        >
          {{ isSetupMode ? '设置密码并登录' : '登录' }}
        </NButton>
      </NForm>
    </NCard>
  </div>
</template>

<style scoped>
.login-container {
  height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: linear-gradient(135deg, #6C9EFF 0%, #A5B4FC 100%);
}
.login-card {
  width: 400px;
  border-radius: 16px;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.15);
}
.login-header {
  text-align: center;
  margin-bottom: 28px;
}
.login-title {
  margin: 0 0 8px;
  font-size: 24px;
  font-weight: 700;
  color: #1E293B;
}
.login-subtitle {
  margin: 0;
  color: var(--text-color-secondary);
  font-size: 14px;
}
</style>
