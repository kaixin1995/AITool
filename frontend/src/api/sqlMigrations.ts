import { httpGet, httpPost } from './http'

export interface SqlMigrationScript {
  fileName: string
  sizeBytes: number
  fileHash: string
  contentPreview: string
  contentTruncated: boolean
  totalExecutions: number
  successExecutions: number
  lastExecutedAt: string | null
  lastSuccess: boolean | null
  lastDryRun: boolean
  lastErrorMessage: string | null
}

export interface SqlMigrationListResult {
  directory: string
  directoryExists: boolean
  scripts: SqlMigrationScript[]
}

export interface SqlMigrationExecutionResult {
  fileName: string
  fileHash: string
  dryRun: boolean
  success: boolean
  statementCount: number
  rowsAffected: number
  durationMs: number
  errorMessage: string | null
}

export async function listSqlMigrations(): Promise<SqlMigrationListResult> {
  return httpGet<SqlMigrationListResult>('/api/admin/sql-migrations')
}

export async function executeSqlMigration(
  fileName: string,
  payload: { password: string; dryRun: boolean }
): Promise<SqlMigrationExecutionResult> {
  return httpPost<SqlMigrationExecutionResult>(
    `/api/admin/sql-migrations/${encodeURIComponent(fileName)}/execute`,
    payload
  )
}
