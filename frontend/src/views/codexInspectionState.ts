export function isInspectionDisabledError(error: unknown): boolean {
  return typeof error === 'object'
    && error !== null
    && 'status' in error
    && error.status === 404
}

export function inspectionActionLabel(action: string): string {
  const labels: Record<string, string> = {
    keep: '保留',
    disable: '禁用',
    enable: '启用'
  }
  return labels[action] ?? action
}
