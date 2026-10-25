import { readFileSync, writeFileSync } from 'node:fs'

const p = 'frontend/src/views/OAuthView.vue'
let v = readFileSync(p, 'utf8')
const eol = v.includes('\r\n') ? '\r\n' : '\n'
const patch = (name, o, n) => {
  const os = o.join(eol), ns = n.join(eol)
  if (!v.includes(os)) { console.log('MISS [' + name + ']'); return }
  v = v.split(os).join(ns)
  console.log('ok [' + name + ']')
}

// ① 顶部工具栏：状态筛选前加厂商下拉（label 带计数，默认「全部」）
patch('toolbar-select', [`          <NSelect`,
  `            v-model:value="accountStatusFilter"`],
[`          <NSelect
            v-model:value="providerFilter"
            :options="providerFilterSelectOptions"
            size="small"
            style="width: 170px"
          />
          <NSelect`,
  `            v-model:value="accountStatusFilter"`])

// ② 下拉选项（label 带计数；NSelect 用 label/value 而非 chips 的 key/label/count）
patch('select-options', [`const filteredAccounts = computed(() => providerFilter.value === 'all'`],
[`const providerFilterSelectOptions = computed(() => providerFilterOptions.value.map(option => ({
  label: \`\${option.label}（\${option.count}）\`,
  value: option.key
})))

const filteredAccounts = computed(() => providerFilter.value === 'all'`])

// ③ 移除 chip 行（横向滚动来源）
patch('remove-chips', [`              <div class="oauth-provider-filters" role="tablist" aria-label="账号厂商筛选">`,
  `                <button`,
  `                  v-for="filter in providerFilterOptions"`,
  `                  :key="filter.key"`,
  `                  type="button"`,
  `                  class="oauth-provider-filter"`,
  `                  :class="{ active: providerFilter === filter.key }"`,
  `                  :data-provider="filter.key"`,
  `                  role="tab"`,
  `                  :aria-selected="providerFilter === filter.key"`,
  `                  @click="providerFilter = filter.key"`,
  `                >`,
  `                  <span>{{ filter.label }}</span>`,
  `                  <span class="oauth-provider-filter-count">{{ filter.count }}</span>`,
  `                </button>`,
  `              </div>`,
  ``], [])

writeFileSync(p, v)
console.log('done')
