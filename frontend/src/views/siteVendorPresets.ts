/**
 * 新建站点的厂商预设目录：选中后自动填充 base_url / 路径模式 / 协议支持，只需补密钥。
 *
 * - 仅作用于「新建站点」表单的预填（纯前端，无后端/数据库改动，升级不影响老数据）；
 *   编辑站点与既有站点完全不受影响。
 * - 「自定义」预设（custom）即原有手填流程，保持不变。
 * - 端点与协议形态参考 cc-switch 预设表（reference-projects/cc-switch/src/config/*ProviderPresets.ts）
 *   与各厂商官方文档；路径模式语义见后端 SiteEndpointPathResolver：
 *   standard-root = 基础地址 + /v1/{端点}，versioned-base = 基础地址已含版本路径，直接 + /{端点}。
 * - 协议开关表示该端点「原生」支持的协议；未勾选的协议由转发链路自动转换，不影响使用。
 */

export type VendorPresetGroup = 'cn' | 'global' | 'local'

export interface SiteVendorPreset {
  /** 预设标识（目录内唯一）。 */
  id: string
  /** 展示名（选中时同步为站点名称的默认值）。 */
  label: string
  /** 厂商 API 基础地址。 */
  baseUrl: string
  /** 接口路径模式：standard-root / versioned-base。 */
  endpointPathMode: 'standard-root' | 'versioned-base'
  supportsOpenAi: boolean
  supportsAnthropic: boolean
  supportsResponses: boolean
  /** 密钥输入框占位提示（如密钥前缀）。 */
  keyPlaceholder?: string
  /** 该端点是否被站点页「额度查询」支持（当前为智谱 GLM 编程套餐）。 */
  quotaAvailable?: boolean
  group: VendorPresetGroup
}

/** 「自定义」预设标识：即原有手填流程。 */
export const CUSTOM_PRESET_ID = 'custom'

export const SITE_VENDOR_PRESETS: readonly SiteVendorPreset[] = [
  // ── 国内厂商 ──────────────────────────────────────────────
  {
    id: 'zhipu-cn',
    label: '智谱 GLM 编程套餐（国内站）',
    baseUrl: 'https://open.bigmodel.cn/api/coding/paas/v4',
    endpointPathMode: 'versioned-base',
    // 编程套餐端点的第一方用法是 OpenAI 兼容（base + /chat/completions，cc-switch 的
    // pi/hermes/opencode 三处预设一致）；Anthropic 格式客户端请用「Anthropic 兼容端点」预设。
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: '智谱 API Key',
    quotaAvailable: true,
    group: 'cn'
  },
  {
    id: 'zhipu-anthropic-cn',
    label: '智谱 GLM（国内站 · Anthropic 兼容）',
    baseUrl: 'https://open.bigmodel.cn/api/anthropic',
    endpointPathMode: 'standard-root',
    supportsOpenAi: false,
    supportsAnthropic: true,
    supportsResponses: false,
    keyPlaceholder: '智谱 API Key',
    // 同域密钥同样可查编程套餐额度（额度检测按 host 匹配）。
    quotaAvailable: true,
    group: 'cn'
  },
  {
    id: 'deepseek',
    label: 'DeepSeek',
    baseUrl: 'https://api.deepseek.com',
    endpointPathMode: 'standard-root',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'sk-...',
    group: 'cn'
  },
  {
    id: 'moonshot',
    label: 'Kimi（月之暗面）',
    baseUrl: 'https://api.moonshot.cn/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'sk-...',
    group: 'cn'
  },
  {
    id: 'qwen-dashscope',
    label: '阿里云百炼（通义千问）',
    baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'sk-...',
    group: 'cn'
  },
  {
    id: 'volcengine-ark',
    label: '火山方舟（豆包）',
    baseUrl: 'https://ark.cn-beijing.volces.com/api/v3',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: '方舟 API Key',
    group: 'cn'
  },
  {
    id: 'minimax',
    label: 'MiniMax',
    baseUrl: 'https://api.minimaxi.com/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'eyJ...（JWT）',
    group: 'cn'
  },
  {
    id: 'siliconflow',
    label: '硅基流动 SiliconFlow',
    baseUrl: 'https://api.siliconflow.cn/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'sk-...',
    group: 'cn'
  },
  {
    id: 'sensenova',
    label: '商汤日日新 SenseNova',
    baseUrl: 'https://token.sensenova.cn',
    endpointPathMode: 'standard-root',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: '商汤 API Key',
    group: 'cn'
  },
  {
    id: 'hunyuan',
    label: '腾讯混元',
    baseUrl: 'https://api.hunyuan.cloud.tencent.com/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: '混源 API Key',
    group: 'cn'
  },
  {
    id: 'modelscope',
    label: '魔搭 ModelScope',
    baseUrl: 'https://api-inference.modelscope.cn/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'ms-...',
    group: 'cn'
  },

  // ── 国际厂商 ──────────────────────────────────────────────
  {
    id: 'zhipu-intl',
    label: '智谱 GLM 编程套餐（国际站）',
    baseUrl: 'https://api.z.ai/api/coding/paas/v4',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: '智谱 API Key',
    quotaAvailable: true,
    group: 'global'
  },
  {
    id: 'zhipu-anthropic-intl',
    label: '智谱 GLM（国际站 · Anthropic 兼容）',
    baseUrl: 'https://api.z.ai/api/anthropic',
    endpointPathMode: 'standard-root',
    supportsOpenAi: false,
    supportsAnthropic: true,
    supportsResponses: false,
    keyPlaceholder: '智谱 API Key',
    quotaAvailable: true,
    group: 'global'
  },
  {
    id: 'openai',
    label: 'OpenAI',
    baseUrl: 'https://api.openai.com/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: true,
    keyPlaceholder: 'sk-...',
    group: 'global'
  },
  {
    id: 'anthropic',
    label: 'Anthropic',
    baseUrl: 'https://api.anthropic.com',
    endpointPathMode: 'standard-root',
    supportsOpenAi: false,
    supportsAnthropic: true,
    supportsResponses: false,
    keyPlaceholder: 'sk-ant-...',
    group: 'global'
  },
  {
    id: 'gemini-openai',
    label: 'Google Gemini（OpenAI 兼容）',
    baseUrl: 'https://generativelanguage.googleapis.com/v1beta/openai',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'AIza...',
    group: 'global'
  },
  {
    id: 'groq',
    label: 'Groq',
    baseUrl: 'https://api.groq.com/openai',
    endpointPathMode: 'standard-root',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'gsk_...',
    group: 'global'
  },
  {
    id: 'nvidia-nim',
    label: 'NVIDIA NIM',
    baseUrl: 'https://integrate.api.nvidia.com',
    endpointPathMode: 'standard-root',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'nvapi-...',
    group: 'global'
  },
  {
    id: 'xai',
    label: 'xAI Grok',
    baseUrl: 'https://api.x.ai/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    // xAI 官方以 /v1/responses 为一等端点（cc-switch 预设实测注释）。
    supportsResponses: true,
    keyPlaceholder: 'xai-...',
    group: 'global'
  },
  {
    id: 'openrouter',
    label: 'OpenRouter',
    baseUrl: 'https://openrouter.ai/api/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'sk-or-v1-...',
    group: 'global'
  },
  {
    id: 'mistral',
    label: 'Mistral AI',
    baseUrl: 'https://api.mistral.ai/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: '...',
    group: 'global'
  },
  {
    id: 'together',
    label: 'Together AI',
    baseUrl: 'https://api.together.xyz/v1',
    endpointPathMode: 'versioned-base',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: '...',
    group: 'global'
  },
  {
    id: 'perplexity',
    label: 'Perplexity',
    baseUrl: 'https://api.perplexity.ai',
    endpointPathMode: 'standard-root',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: 'pplx-...',
    group: 'global'
  },

  // ── 本地部署 ──────────────────────────────────────────────
  {
    id: 'ollama',
    label: 'Ollama（本地）',
    baseUrl: 'http://localhost:11434',
    endpointPathMode: 'standard-root',
    supportsOpenAi: true,
    supportsAnthropic: false,
    supportsResponses: false,
    keyPlaceholder: '本地服务无需真实密钥，填任意非空值即可',
    group: 'local'
  }
] as const

const GROUP_LABELS: Record<VendorPresetGroup, string> = {
  cn: '国内厂商',
  global: '国际厂商',
  local: '本地部署'
}

export function findVendorPreset(id: string | null | undefined): SiteVendorPreset | undefined {
  if (!id || id === CUSTOM_PRESET_ID) return undefined
  return SITE_VENDOR_PRESETS.find(p => p.id === id)
}

// 注意必须用 type 别名而非 interface：naive-ui 的选项类型带字符串索引签名，
// TS 规则下 interface 不满足隐式索引签名，会在模板绑定 NSelect 时报类型不兼容。
export type VendorPresetLeafOption = { label: string; value: string }
export type VendorPresetGroupOption = {
  type: 'group'
  label: string
  key: string
  children: VendorPresetLeafOption[]
}

/** NSelect 选项：自定义在首位，其余按组（国内/国际/本地）聚合。 */
export function buildVendorPresetOptions(): (VendorPresetLeafOption | VendorPresetGroupOption)[] {
  const custom: VendorPresetLeafOption = { label: '自定义', value: CUSTOM_PRESET_ID }
  const groups: VendorPresetGroupOption[] = (['cn', 'global', 'local'] as const).map(group => ({
    type: 'group',
    label: GROUP_LABELS[group],
    key: group,
    children: SITE_VENDOR_PRESETS
      .filter(p => p.group === group)
      .map(p => ({ label: p.label, value: p.id }))
  }))
  return [custom, ...groups]
}

/** 预设可填充的表单字段子集（兼容 SitesView 的 SitePayload 可选字段形态）。 */
export interface VendorPresetFormShape {
  name: string
  baseUrl: string
  endpointPathMode?: string
  supportsOpenAi?: boolean
  supportsAnthropic?: boolean
  supportsResponses?: boolean
}

/**
 * 把预设应用到新建表单：填充地址/路径模式/协议开关；名称仅在为空或上一个预设
 * 自动填入时才覆盖（用户手改过的名称不动）。选「自定义」不改任何字段。
 *
 * @param form 目标表单（就地修改）
 * @param presetId 本次选中的预设 id（可为 custom）
 * @param lastPresetId 上一次选中的预设 id（用于判断名称是否预设自动填入）
 * @returns 应用后应记住的预设 id（供下次调用作为 lastPresetId）
 */
export function applyVendorPreset(
  form: VendorPresetFormShape,
  presetId: string,
  lastPresetId: string | null
): string {
  if (presetId === CUSTOM_PRESET_ID) {
    return CUSTOM_PRESET_ID
  }

  const preset = findVendorPreset(presetId)
  if (!preset) {
    return CUSTOM_PRESET_ID
  }

  const lastPreset = findVendorPreset(lastPresetId)
  // 名称覆盖条件：用户没写过名称，或名称还是上一个预设的默认值。
  if (!form.name.trim() || (lastPreset && form.name.trim() === lastPreset.label)) {
    form.name = preset.label
  }

  form.baseUrl = preset.baseUrl
  form.endpointPathMode = preset.endpointPathMode
  form.supportsOpenAi = preset.supportsOpenAi
  form.supportsAnthropic = preset.supportsAnthropic
  form.supportsResponses = preset.supportsResponses
  return preset.id
}
