import { computed, ref, watch } from 'vue'

/**
 * 展示货币切换（金额计价基准恒为 USD，人民币按汇率换算展示）。
 * 偏好持久化到 localStorage，模式与 useTheme 一致；
 * 汇率从模型价格表（model-pricing.json 的 usdToCny）读取，默认 6.74。
 */
const CURRENCY_KEY = 'aitool.currency'

export type CurrencyCode = 'USD' | 'CNY'

const DEFAULT_USD_TO_CNY = 6.74

const usdToCny = ref<number>(DEFAULT_USD_TO_CNY)

const currency = ref<CurrencyCode>(loadInitialCurrency())

function loadInitialCurrency(): CurrencyCode {
  const stored = localStorage.getItem(CURRENCY_KEY)
  return stored === 'CNY' ? 'CNY' : 'USD'
}

watch(currency, (value) => {
  localStorage.setItem(CURRENCY_KEY, value)
})

export function setUsdToCny(rate: number | undefined | null): void {
  usdToCny.value = rate && Number.isFinite(rate) && rate > 0 ? rate : DEFAULT_USD_TO_CNY
}

export function useCurrency() {
  /** 设置页切换展示货币（非法值回退 USD）。立即生效并持久化。 */
  function setCurrencyDisplay(code: string | null | undefined): void {
    currency.value = code === 'CNY' ? 'CNY' : 'USD'
  }

  /** 当前展示货币对应的金额格式化：USD 保留 4 位小数（单次请求金额常在毫厘级），CNY 保留 2 位。 */
  function formatCost(costUsd: number | null | undefined): string {
    if (costUsd === null || costUsd === undefined) return '—'
    if (currency.value === 'CNY') {
      return `¥${(costUsd * usdToCny.value).toFixed(2)}`
    }
    return `$${costUsd.toFixed(4)}`
  }

  /** 大额汇总金额：自动压缩小数位（<0.01 时保留 4 位避免显示 $0.00）。 */
  function formatTotalCost(costUsd: number | null | undefined): string {
    if (costUsd === null || costUsd === undefined) return '—'
    const value = currency.value === 'CNY' ? costUsd * usdToCny.value : costUsd
    const prefix = currency.value === 'CNY' ? '¥' : '$'
    const digits = value >= 0.01 ? 2 : 4
    return `${prefix}${value.toFixed(digits)}`
  }

  const symbol = computed(() => (currency.value === 'CNY' ? '¥' : '$'))
  const isCny = computed(() => currency.value === 'CNY')

  return { currency, usdToCny, symbol, isCny, formatCost, formatTotalCost, setUsdToCny, setCurrencyDisplay }
}
