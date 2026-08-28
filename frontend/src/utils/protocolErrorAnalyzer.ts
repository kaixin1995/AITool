import type { CompatibilityRuleForm } from '@/views/compatibilityState'

export interface ProtocolErrorDiagnosis {
  category: 'unsupported_field' | 'missing_field' | 'reasoning' | 'auth' | 'rate_limit' | 'other'
  title: string
  detail: string
  suggestedAction: string
  recommendedRule?: CompatibilityRuleForm
}

/**
 * 分析上游报错信息并给出常见错误的快速归因与建议规则
 */
export function analyzeProtocolError(
  errorMessage: string | undefined | null,
  statusCode?: number
): ProtocolErrorDiagnosis | null {
  if (!errorMessage && !statusCode) return null
  const err = (errorMessage || '').trim()

  // 1. Google Gemini / Antigravity 参数 schema 校验失败
  // 例: schema at properties.meta.properties.phases.items requires unspecified property 'title'
  // 例: Invalid value at 'contents[0].parts[0]'
  const geminiSchemaMatch = err.match(/schema at ([a-zA-Z0-9_.]+) requires unspecified property ['"`]?([a-zA-Z0-9_.]+)['"`]?/i)
    || err.match(/requires unspecified property ['"`]?([a-zA-Z0-9_.]+)['"`]?/i)

  if (geminiSchemaMatch) {
    const missingProp = geminiSchemaMatch[2] || geminiSchemaMatch[1]
    return {
      category: 'missing_field',
      title: `Gemini Schema 校验失败: 缺少 ${missingProp}`,
      detail: `Google Antigravity/Gemini 上游在解析 tools 参数定义时，Schema 声明了 required 但属性列表中缺少 \`${missingProp}\` 的定义。`,
      suggestedAction: `建议检查客户端传入的 tools parameters JSON Schema，或使用【🤖 AI 深度诊断】分析该参数结构。`
    }
  }

  // 2. 思维链 / Reasoning 签名或内容丢失 (DeepSeek / Anthropic 等，先于常规 missing_field 匹配)
  // 例: 'reasoning_content' is required when tool calls are present
  // 例: missing reasoning_content
  if (/reasoning_content.*required|missing.*reasoning_content|thought.*signature/i.test(err)) {
    return {
      category: 'reasoning',
      title: '思维链 (Reasoning) 回传丢失',
      detail: '上游模型（如 DeepSeek）在工具调用场景下要求必须回传上一轮的思考过程 `reasoning_content`。',
      suggestedAction: '建议启用 `keep_reasoning` 规则，在跨协议转换时保留 assistant 思维链。',
      recommendedRule: {
        op: 'keep_reasoning',
        scope: 'bridge'
      }
    }
  }

  // 2. 字段不支持 / Extra inputs not permitted (OpenAI/DeepSeek 等)
  // 例: Extra inputs are not permitted: 'reasoning_effort'
  // 例: Unrecognized request argument supplied: stream_options
  // 例: Unsupported parameter: 'store'
  const unsupportedFieldMatch = err.match(/(?:Extra inputs are not permitted|Unrecognized request argument(?: supplied)?|Unsupported parameter|Unknown field|Unexpected parameter|does not support(?: parameter)?)\s*[:']*\s*[`'"]?([a-zA-Z0-9_.]+)['"`]?/i)
    || err.match(/[`'"]([a-zA-Z0-9_.]+)['"`]\s*(?:is not supported|is not permitted|is extra|was unexpected)/i)

  if (unsupportedFieldMatch && unsupportedFieldMatch[1]) {
    const fieldName = unsupportedFieldMatch[1]
    return {
      category: 'unsupported_field',
      title: `上游不支持字段: ${fieldName}`,
      detail: `上游模型拒绝了请求中的 \`${fieldName}\` 字段（HTTP ${statusCode || 400}）。`,
      suggestedAction: `建议为此模型添加规则：剔除字段 (strip) \`${fieldName}\`。`,
      recommendedRule: {
        op: 'strip',
        target: fieldName,
        scope: 'bridge'
      }
    }
  }

  // 3. 缺少必填字段 / Missing required parameter
  // 例: missing required field: max_tokens
  // 例: 'messages' is a required property
  const missingFieldMatch = err.match(/(?:missing required field|is a required (?:property|field)|Field required|Missing required parameter)\s*[:']*\s*[`'"]?([a-zA-Z0-9_.]+)['"`]?/i)
    || err.match(/[`'"]([a-zA-Z0-9_.]+)['"`]\s*(?:is missing|is required)/i)

  if (missingFieldMatch && missingFieldMatch[1]) {
    const fieldName = missingFieldMatch[1]
    const defaultValue = fieldName === 'max_tokens' || fieldName === 'max_completion_tokens' ? '4096' : ''
    return {
      category: 'missing_field',
      title: `上游缺少必填字段: ${fieldName}`,
      detail: `上游接口要求必须提供 \`${fieldName}\` 字段。`,
      suggestedAction: `建议为此模型添加规则：补充默认值 (default) \`${fieldName}\`。`,
      recommendedRule: {
        op: 'default',
        key: fieldName,
        value: defaultValue,
        scope: 'bridge'
      }
    }
  }

  // 4. 权限与认证
  if (statusCode === 401 || /invalid_api_key|unauthorized|invalid token/i.test(err)) {
    return {
      category: 'auth',
      title: '上游 API 认证失败 (401)',
      detail: '站点配置的 API Key 无效、已过期或已被上游撤销。',
      suggestedAction: '请检查站点管理中配置的站点 Key 是否正确可用。'
    }
  }

  // 5. 限流或欠费
  if (statusCode === 429 || /rate_limit|quota_exceeded|insufficient_quota|balance/i.test(err)) {
    return {
      category: 'rate_limit',
      title: '上游频率超限或额度不足 (429/402)',
      detail: '上游账号额度已耗尽、欠费，或触发了并发/RPM 限流。',
      suggestedAction: '建议在站点管理中补充可用 Key，或检查上游账户余额。'
    }
  }

  // 其他通用 400/422 错误
  if (statusCode === 400 || statusCode === 422) {
    return {
      category: 'other',
      title: `上游请求参数校验失败 (${statusCode})`,
      detail: err.length > 150 ? err.slice(0, 150) + '...' : err,
      suggestedAction: '建议使用【🤖 AI 深度诊断】分析具体报错原因并生成修复规则。'
    }
  }

  return null
}
