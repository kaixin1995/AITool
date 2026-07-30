import { describe, expect, it } from 'vitest'
import { renderSafeMarkdown } from './conversationsMarkdown'

describe('renderSafeMarkdown', () => {
  it('renders richer markdown blocks while escaping unsafe html', () => {
    const html = renderSafeMarkdown([
      '# 标题',
      '',
      '- 项目 **一**',
      '- 项目 `二`',
      '',
      '> 引用内容',
      '',
      '| 名称 | 值 |',
      '| --- | --- |',
      '| A | [链接](https://example.com) |',
      '',
      '<img src=x onerror=alert(1)>',
      '[坏链接](javascript:alert(1))'
    ].join('\n'))

    expect(html).toContain('<h1>标题</h1>')
    expect(html).toContain('<ul><li>项目 <strong>一</strong></li><li>项目 <code>二</code></li></ul>')
    expect(html).toContain('<blockquote>引用内容</blockquote>')
    expect(html).toContain('<table>')
    expect(html).toContain('href="https://example.com"')
    expect(html).toContain('&lt;img src=x onerror=alert(1)&gt;')
    expect(html).not.toContain('<img')
    expect(html).not.toContain('javascript:')
  })
})
