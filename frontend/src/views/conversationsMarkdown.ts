import { Marked, Renderer } from 'marked'
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdownLanguage from 'highlight.js/lib/languages/markdown'
import plaintext from 'highlight.js/lib/languages/plaintext'
import python from 'highlight.js/lib/languages/python'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import { FilterXSS } from 'xss'

const SAFE_LINK_RE = /^(https?:|mailto:)/i
const LANGUAGE_ALIASES: Record<string, string> = {
  bash: 'bash',
  csharp: 'csharp',
  cs: 'csharp',
  css: 'css',
  html: 'xml',
  javascript: 'javascript',
  js: 'javascript',
  json: 'json',
  markdown: 'markdown',
  md: 'markdown',
  plaintext: 'plaintext',
  python: 'python',
  py: 'python',
  shell: 'bash',
  sh: 'bash',
  sql: 'sql',
  text: 'plaintext',
  ts: 'typescript',
  typescript: 'typescript',
  xml: 'xml'
}

hljs.registerLanguage('bash', bash)
hljs.registerLanguage('csharp', csharp)
hljs.registerLanguage('css', css)
hljs.registerLanguage('javascript', javascript)
hljs.registerLanguage('json', json)
hljs.registerLanguage('markdown', markdownLanguage)
hljs.registerLanguage('plaintext', plaintext)
hljs.registerLanguage('python', python)
hljs.registerLanguage('sql', sql)
hljs.registerLanguage('typescript', typescript)
hljs.registerLanguage('xml', xml)

export function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

function normalizeMarkdown(value: string): string {
  return value.replace(
    /```([A-Za-z0-9_+#.-]+)(?=[{[])/g,
    '```$1\n'
  )
}

function renderCode(code: string, info: string | undefined): string {
  const requestedLanguage = info?.trim().split(/\s+/, 1)[0] || 'text'
  const normalizedLanguage = LANGUAGE_ALIASES[requestedLanguage.toLowerCase()]
  const highlighted = normalizedLanguage
    ? hljs.highlight(code, { language: normalizedLanguage }).value
    : escapeHtml(code)

  return [
    '<div class="conversation-code-block">',
    '<div class="conversation-code-header">',
    `<span>${escapeHtml(requestedLanguage)}</span>`,
    '<button type="button" class="conversation-code-copy" data-conversation-copy-code>复制</button>',
    '</div>',
    `<pre><code class="hljs language-${escapeHtml(requestedLanguage)}">${highlighted}</code></pre>`,
    '</div>'
  ].join('')
}

const renderer = new Renderer()
renderer.code = renderCode
renderer.html = () => ''
renderer.link = (href, title, text) => {
  if (!SAFE_LINK_RE.test(href)) return text
  const titleAttribute = title
    ? ` title="${escapeHtml(title)}"`
    : ''
  return `<a href="${escapeHtml(href)}"${titleAttribute} target="_blank" rel="noopener noreferrer">${text}</a>`
}

const markdownParser = new Marked({
  breaks: true,
  gfm: true,
  renderer
})

const sanitizer = new FilterXSS({
  allowList: {
    a: ['href', 'title', 'target', 'rel'],
    blockquote: [],
    br: [],
    button: ['type', 'class', 'data-conversation-copy-code'],
    code: ['class'],
    del: [],
    div: ['class'],
    em: [],
    h1: [],
    h2: [],
    h3: [],
    h4: [],
    h5: [],
    h6: [],
    hr: [],
    input: ['type', 'checked', 'disabled'],
    li: ['class'],
    ol: [],
    p: [],
    pre: [],
    span: ['class'],
    strong: [],
    table: [],
    tbody: [],
    td: [],
    th: [],
    thead: [],
    tr: [],
    ul: ['class']
  },
  css: false,
  stripIgnoreTag: true,
  stripIgnoreTagBody: ['script', 'style', 'iframe', 'object', 'embed']
})

export function renderSafeMarkdown(value: string): string {
  if (!value) return '<p>(空)</p>'
  const rendered = markdownParser.parse(normalizeMarkdown(value))
  return sanitizer.process(
    typeof rendered === 'string' ? rendered : ''
  )
}
