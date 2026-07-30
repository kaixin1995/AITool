const SAFE_LINK_RE = /^(https?:|mailto:)/i

export function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

function renderInline(value: string): string {
  return escapeHtml(value)
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
    .replace(/\*([^*]+)\*/g, '<em>$1</em>')
    .replace(/\[([^\]]+)\]\(([^\s)]+)\)/g, (_match, text: string, href: string) => {
      const decodedHref = href.replace(/&amp;/g, '&')
      if (!SAFE_LINK_RE.test(decodedHref)) return text
      return `<a href="${escapeHtml(decodedHref)}" target="_blank" rel="noopener noreferrer">${text}</a>`
    })
}

function isTableSeparator(line: string): boolean {
  return /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$/.test(line)
}

function splitTableRow(line: string): string[] {
  return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((cell) => cell.trim())
}

function renderTable(lines: string[], index: number): { html: string; nextIndex: number } | null {
  if (index + 1 >= lines.length || !lines[index].includes('|') || !isTableSeparator(lines[index + 1])) return null
  const header = splitTableRow(lines[index])
  const rows: string[][] = []
  let cursor = index + 2
  while (cursor < lines.length && lines[cursor].includes('|') && lines[cursor].trim()) {
    rows.push(splitTableRow(lines[cursor]))
    cursor += 1
  }
  const headHtml = header.map((cell) => `<th>${renderInline(cell)}</th>`).join('')
  const bodyHtml = rows.map((row) => `<tr>${row.map((cell) => `<td>${renderInline(cell)}</td>`).join('')}</tr>`).join('')
  return { html: `<table><thead><tr>${headHtml}</tr></thead><tbody>${bodyHtml}</tbody></table>`, nextIndex: cursor }
}

function renderList(lines: string[], index: number): { html: string; nextIndex: number } | null {
  const first = lines[index]
  const ordered = /^\s*\d+\.\s+/.test(first)
  const unordered = /^\s*[-*+]\s+/.test(first)
  if (!ordered && !unordered) return null

  const tag = ordered ? 'ol' : 'ul'
  const marker = ordered ? /^\s*\d+\.\s+/ : /^\s*[-*+]\s+/
  const items: string[] = []
  let cursor = index
  while (cursor < lines.length && marker.test(lines[cursor])) {
    items.push(`<li>${renderInline(lines[cursor].replace(marker, '').trim())}</li>`)
    cursor += 1
  }
  return { html: `<${tag}>${items.join('')}</${tag}>`, nextIndex: cursor }
}

export function renderSafeMarkdown(value: string): string {
  if (!value) return '<p>(空)</p>'

  const codeBlocks: string[] = []
  const withoutCode = value.replace(/```([^\n]*)\n?([\s\S]*?)```/g, (_match, lang: string, code: string) => {
    const token = `@@CODE_${codeBlocks.length}@@`
    const label = lang?.trim() || 'code'
    codeBlocks.push(`<div class="conversation-code-block"><div class="conversation-code-header"><span>${escapeHtml(label)}</span></div><pre><code>${escapeHtml(code.trim())}</code></pre></div>`)
    return token
  })

  const lines = withoutCode.split(/\r?\n/)
  const blocks: string[] = []
  let paragraph: string[] = []
  let index = 0

  function flushParagraph(): void {
    if (paragraph.length === 0) return
    blocks.push(`<p>${paragraph.map(renderInline).join('<br>')}</p>`)
    paragraph = []
  }

  while (index < lines.length) {
    const line = lines[index]
    const trimmed = line.trim()

    if (!trimmed) {
      flushParagraph()
      index += 1
      continue
    }

    if (/^@@CODE_\d+@@$/.test(trimmed)) {
      flushParagraph()
      blocks.push(trimmed)
      index += 1
      continue
    }

    const table = renderTable(lines, index)
    if (table) {
      flushParagraph()
      blocks.push(table.html)
      index = table.nextIndex
      continue
    }

    const list = renderList(lines, index)
    if (list) {
      flushParagraph()
      blocks.push(list.html)
      index = list.nextIndex
      continue
    }

    const heading = /^(#{1,4})\s+(.+)$/.exec(trimmed)
    if (heading) {
      flushParagraph()
      blocks.push(`<h${heading[1].length}>${renderInline(heading[2])}</h${heading[1].length}>`)
      index += 1
      continue
    }

    if (/^---+$/.test(trimmed)) {
      flushParagraph()
      blocks.push('<hr>')
      index += 1
      continue
    }

    if (trimmed.startsWith('> ')) {
      flushParagraph()
      const quoteLines: string[] = []
      while (index < lines.length && lines[index].trim().startsWith('> ')) {
        quoteLines.push(lines[index].trim().slice(2))
        index += 1
      }
      blocks.push(`<blockquote>${quoteLines.map(renderInline).join('<br>')}</blockquote>`)
      continue
    }

    paragraph.push(line)
    index += 1
  }

  flushParagraph()
  return blocks.join('').replace(/@@CODE_(\d+)@@/g, (_match, codeIndex: string) => codeBlocks[Number(codeIndex)] ?? '')
}
