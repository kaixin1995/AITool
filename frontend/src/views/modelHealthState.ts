export function modelHealthSuccessColor(rate: number): string {
  if (rate >= 0.8) return 'var(--success, #18a058)'
  if (rate >= 0.5) return 'var(--warning, #f0a020)'
  return 'var(--danger, #d03050)'
}
