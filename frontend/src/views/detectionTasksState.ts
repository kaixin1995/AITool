export const ALL_MODELS_VALUE = '__all_models__'

export function normalizeDetectionTaskModelId(value: string | null): string | null {
  return !value || value === ALL_MODELS_VALUE ? null : value
}
