<script setup lang="ts">
import { computed, h, onMounted, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NEmpty,
  NInput,
  NSpace,
  NTag,
  useDialog,
  useMessage
} from 'naive-ui'
import * as api from '@/api/sqlMigrations'
import type { SqlMigrationExecutionResult, SqlMigrationScript } from '@/api/sqlMigrations'

const message = useMessage()
const dialog = useDialog()
const loading = ref(false)
const executing = ref(false)
const directory = ref('')
const directoryExists = ref(true)
const scripts = ref<SqlMigrationScript[]>([])
const selectedFile = ref<string | null>(null)
const passwordInput = ref('')
const lastResult = ref<SqlMigrationExecutionResult | null>(null)

const selected = computed(() =>
  scripts.value.find(s => s.fileName === selectedFile.value) ?? null
)

async function load(): Promise<void> {
  loading.value = true
  try {
    const resp = await api.listSqlMigrations()
    directory.value = resp.directory
    directoryExists.value = resp.directoryExists
    scripts.value = resp.scripts ?? []
    if (selectedFile.value && !scripts.value.some(s => s.fileName === selectedFile.value)) {
      selectedFile.value = null
    }
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    loading.value = false
  }
}

function formatDateTime(value: string | null | undefined): string {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '-'
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`
}

function statusTag(script: SqlMigrationScript): { type: 'default' | 'success' | 'error' | 'warning'; label: string } {
  if (script.totalExecutions === 0) return { type: 'default', label: '未执行' }
  if (script.lastSuccess === true) {
    return script.lastDryRun
      ? { type: 'warning', label: '最近试运行成功' }
      : { type: 'success', label: `已成功执行 ${script.successExecutions} 次` }
  }
  return { type: 'error', label: '最近执行失败' }
}

function confirmExecute(script: SqlMigrationScript, dryRun: boolean): void {
  passwordInput.value = ''
  const executedBefore = script.successExecutions > 0
  dialog.warning({
    title: dryRun ? `试运行：${script.fileName}` : `执行：${script.fileName}`,
    content: () =>
      h('div', { style: 'display:flex;flex-direction:column;gap:10px' }, [
        executedBefore && !dryRun
          ? h(
              'span',
              { style: 'color:#F87171;font-weight:600' },
              `该脚本已成功执行过 ${script.successExecutions} 次。请确认脚本幂等或已备份数据库，否则可能造成重复修改！`
            )
          : h('span', null, dryRun ? '将在事务内执行全部语句后回滚，不落任何数据变更。' : '将在事务内执行全部语句并提交，立即生效。'),
        h(NInput, {
          type: 'password',
          showPasswordOn: 'click',
          placeholder: '请输入管理员密码确认',
          value: passwordInput.value,
          'onUpdate:value': (v: string) => { passwordInput.value = v }
        })
      ]),
    positiveText: dryRun ? '试运行（回滚）' : '输入密码并执行',
    negativeText: '取消',
    onPositiveClick: async () => {
      if (!passwordInput.value.trim()) {
        message.warning('请输入管理员密码')
        return false
      }
      await doExecute(script.fileName, passwordInput.value, dryRun)
    }
  })
}

async function doExecute(fileName: string, password: string, dryRun: boolean): Promise<void> {
  executing.value = true
  try {
    const result = await api.executeSqlMigration(fileName, { password, dryRun })
    lastResult.value = result
    if (result.success) {
      message.success(
        dryRun
          ? `试运行完成：${result.statementCount} 条语句，影响 ${result.rowsAffected} 行（已回滚）`
          : `执行成功：${result.statementCount} 条语句，影响 ${result.rowsAffected} 行`
      )
    } else {
      message.error(`执行失败：${result.errorMessage ?? '未知错误'}（已回滚）`)
    }
    await load()
  } catch (e) {
    message.error((e as Error).message)
  } finally {
    executing.value = false
  }
}

onMounted(load)
</script>

<template>
  <NCard>
    <template #header>
      <NSpace justify="space-between" align="center">
        <span>SQL 迁移脚本</span>
        <NSpace :size="8">
          <NButton size="small" :loading="loading" @click="load">刷新</NButton>
        </NSpace>
      </NSpace>
    </template>

    <NAlert type="info" :show-icon="true" style="margin-bottom: 12px">
      将 .sql 脚本文件放到服务器的
      <code>{{ directory || 'sql-migrations' }}</code>
      目录后点击刷新。接口只执行该目录下已存在的文件（支持多语句，逐条执行、整体事务、失败回滚）；每次执行都需要管理员密码确认。建议先试运行核对影响行数，再正式执行并提前备份数据库。
    </NAlert>
    <NAlert v-if="!directoryExists" type="warning" style="margin-bottom: 12px">
      脚本目录尚不存在，请先在服务器上创建并放入 .sql 文件。
    </NAlert>

    <NEmpty v-if="scripts.length === 0" description="脚本目录暂无 .sql 文件" style="margin: 24px 0" />

    <div v-else class="migration-list">
      <div
        v-for="s in scripts"
        :key="s.fileName"
        class="migration-row"
        :class="{ active: s.fileName === selectedFile }"
        @click="selectedFile = s.fileName"
      >
        <NSpace align="center" :size="8" style="flex: 1; min-width: 0">
          <NTag size="small" :type="statusTag(s).type" :bordered="false">{{ statusTag(s).label }}</NTag>
          <span class="migration-name">{{ s.fileName }}</span>
          <span class="migration-meta">{{ formatSize(s.sizeBytes) }}</span>
          <span v-if="s.fileHash" class="migration-meta">#{{ s.fileHash.slice(0, 8) }}</span>
          <span class="migration-meta">最近执行：{{ formatDateTime(s.lastExecutedAt) }}</span>
          <span v-if="s.lastErrorMessage" class="migration-error" :title="s.lastErrorMessage">上次失败</span>
        </NSpace>
      </div>
    </div>

    <template v-if="selected">
      <div class="migration-preview-header">
        <span>内容预览{{ selected.contentTruncated ? '（超过 64KB 已截断）' : '' }}</span>
        <NSpace :size="8">
          <NButton size="small" :disabled="executing" @click="confirmExecute(selected, true)">试运行</NButton>
          <NButton size="small" type="error" :disabled="executing" @click="confirmExecute(selected, false)">执行</NButton>
        </NSpace>
      </div>
      <pre class="migration-preview">{{ selected.contentPreview || '（文件超过 1MB 上限，无法预览与执行）' }}</pre>
    </template>

    <div v-if="lastResult" class="migration-result">
      <NSpace align="center" :size="10">
        <NTag size="small" :type="lastResult.success ? 'success' : 'error'" :bordered="false">
          {{ lastResult.success ? (lastResult.dryRun ? '试运行成功（已回滚）' : '执行成功') : '执行失败（已回滚）' }}
        </NTag>
        <span class="migration-meta">{{ lastResult.fileName }}</span>
        <span class="migration-meta">{{ lastResult.statementCount }} 条语句</span>
        <span class="migration-meta">影响 {{ lastResult.rowsAffected }} 行</span>
        <span class="migration-meta">耗时 {{ lastResult.durationMs }}ms</span>
      </NSpace>
      <div v-if="lastResult.errorMessage" class="migration-error">{{ lastResult.errorMessage }}</div>
    </div>
  </NCard>
</template>

<style scoped>
.migration-row {
  display: flex;
  align-items: center;
  padding: 10px 12px;
  border-radius: 8px;
  margin-bottom: 4px;
  cursor: pointer;
}
.migration-row:hover { background: rgba(108, 158, 255, 0.06); }
.migration-row.active { background: rgba(108, 158, 255, 0.12); }
.migration-name { font-weight: 600; }
.migration-meta { font-size: 13px; color: var(--text-color-secondary, #888); }
.migration-error { font-size: 13px; color: #F87171; }
.migration-preview-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin: 16px 0 8px;
  font-weight: 600;
}
.migration-preview {
  max-height: 320px;
  overflow: auto;
  padding: 12px;
  border-radius: 8px;
  background: rgba(128, 128, 128, 0.08);
  font-size: 12px;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-all;
}
.migration-result {
  margin-top: 16px;
  padding-top: 12px;
  border-top: 1px solid rgba(128, 128, 128, 0.2);
}
</style>
