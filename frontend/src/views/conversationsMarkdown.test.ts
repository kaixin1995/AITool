import { describe, expect, it } from 'vitest'
import { renderSafeMarkdown } from './conversationsMarkdown'

describe('renderSafeMarkdown', () => {
  it('renders GFM blocks while sanitizing unsafe html', () => {
    const html = renderSafeMarkdown([
      '# 标题',
      '',
      '- 项目 **一**',
      '- [x] 已完成',
      '',
      '> 引用内容',
      '',
      '| 名称 | 值 |',
      '| --- | --- |',
      '| A | [链接](https://example.com) |',
      '',
      '~~已删除~~',
      '<img src=x onerror=alert(1)>',
      '<svg onload=alert(1)></svg>',
      '[坏链接](javascript:alert(1))'
    ].join('\n'))

    expect(html).toContain('<h1>标题</h1>')
    expect(html).toContain('<strong>一</strong>')
    expect(html).toContain('type="checkbox"')
    expect(html).toContain('checked')
    expect(html).toContain('<blockquote>')
    expect(html).toContain('<table>')
    expect(html).toContain('<del>已删除</del>')
    expect(html).toContain('href="https://example.com"')
    expect(html).not.toContain('<img')
    expect(html).not.toContain('<svg')
    expect(html).not.toContain('onerror')
    expect(html).not.toContain('onload')
    expect(html).not.toContain('javascript:')
  })

  it('highlights code, exposes a copy action and preserves code whitespace', () => {
    const html = renderSafeMarkdown('```js\n\n  const value = 1\n\n```')

    expect(html).toContain('data-conversation-copy-code')
    expect(html).toContain('class="hljs language-js"')
    expect(html).toContain('hljs-keyword')
    expect(html).toContain('\n  <span class="hljs-keyword">const</span> value = <span class="hljs-number">1</span>\n')
  })

  it('falls back to escaped plain text for unknown code languages', () => {
    const html = renderSafeMarkdown('```unknown-lang\n<unsafe>& value\n```')

    expect(html).toContain('language-unknown-lang')
    expect(html).toContain('&lt;unsafe&gt;&amp; value')
    expect(html).not.toContain('<unsafe>')
  })
})
