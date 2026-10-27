import { LegalBlock, LegalInline, headingId, parseInline, parseLegalDocument } from './legal-document';
import { LEGAL_PAGES } from './legal-pages';

const textOf = (spans: readonly LegalInline[]): string => spans.map((span) => span.text).join('');

function allText(blocks: readonly LegalBlock[]): string {
  return blocks
    .map((block) => {
      switch (block.kind) {
        case 'heading':
        case 'paragraph':
          return textOf(block.content);
        case 'list':
          return block.items.map((item) => [textOf(item.content), ...item.children.map(textOf)].join(' ')).join(' ');
        case 'table':
          return [...block.head, ...block.rows.flat()].map(textOf).join(' ');
      }
    })
    .join(' ');
}

describe('parseInline', () => {
  it('reads bold, italic, code, a link to a sibling document and an email address', () => {
    expect(
      parseInline(
        '**You own it.** See the *policy*, `HttpOnly`, [Terms](terms-of-service.md) or robert@creatorpantry.com.',
      ),
    ).toEqual([
      { kind: 'strong', text: 'You own it.' },
      { kind: 'text', text: ' See the ' },
      { kind: 'em', text: 'policy' },
      { kind: 'text', text: ', ' },
      { kind: 'code', text: 'HttpOnly' },
      { kind: 'text', text: ', ' },
      { kind: 'route', text: 'Terms', path: '/terms' },
      { kind: 'text', text: ' or ' },
      { kind: 'mail', text: 'robert@creatorpantry.com' },
      { kind: 'text', text: '.' },
    ]);
  });

  it('keeps a bracketed placeholder as written, since filling it in is the owner’s', () => {
    expect(parseInline('Operated by [LEGAL ENTITY NAME], [ADDRESS].')).toEqual([
      { kind: 'text', text: 'Operated by [LEGAL ENTITY NAME], [ADDRESS].' },
    ]);
  });

  it('drops the reviewer’s product-fact marker', () => {
    expect(textOf(parseInline('AI features are off *(product fact)*.'))).toBe('AI features are off.');
  });

  it('keeps the words of a link to a file the site does not serve, and not the link', () => {
    expect(parseInline('[notes](internal.md)')).toEqual([{ kind: 'text', text: 'notes' }]);
  });
});

describe('headingId', () => {
  it('names a numbered section by its words', () => {
    expect(headingId('3. Cookies and storage in your browser')).toBe('cookies-and-storage-in-your-browser');
  });
});

describe('parseLegalDocument', () => {
  it('reads headings, paragraphs, nested lists and tables, and leaves out blockquotes', () => {
    const doc = parseLegalDocument(
      [
        '# A Policy',
        '',
        '> **MOCK-UP.** Not for readers.',
        '> Still not.',
        '',
        '## 1. First part',
        '',
        'One paragraph that',
        'runs over two lines.',
        '',
        '- An item that',
        '  continues.',
        '- With children:',
        '  - a child that',
        '    continues;',
        '  - another.',
        '',
        '| What | How long |',
        '| --- | --- |',
        '| Sessions | 8 hours |',
      ].join('\n'),
    );

    expect(doc.title).toBe('A Policy');
    expect(doc.blocks.map((block) => block.kind)).toEqual(['heading', 'paragraph', 'list', 'table']);
    expect(allText(doc.blocks)).not.toContain('MOCK-UP');

    const [heading, paragraph, list, table] = doc.blocks;
    expect(heading).toEqual(jasmine.objectContaining({ level: 2, id: 'first-part' }));
    expect(paragraph.kind === 'paragraph' && textOf(paragraph.content)).toBe('One paragraph that runs over two lines.');
    if (list.kind !== 'list') fail('expected a list');
    else {
      expect(list.items.map((item) => textOf(item.content))).toEqual(['An item that continues.', 'With children:']);
      expect(list.items[1].children.map(textOf)).toEqual(['a child that continues;', 'another.']);
    }
    if (table.kind !== 'table') fail('expected a table');
    else {
      expect(table.label).toBe('First part');
      expect(table.head.map(textOf)).toEqual(['What', 'How long']);
      expect(table.rows.map((row) => row.map(textOf))).toEqual([['Sessions', '8 hours']]);
    }
  });

  /** The real files, as the build imports them: a parse that silently dropped a section would pass the above. */
  for (const page of LEGAL_PAGES) {
    it(`reads every section of the ${page.label} and none of its review notes`, () => {
      const doc = parseLegalDocument(page.markdown);
      const sourceSections = page.markdown.split('\n').filter((line) => line.startsWith('## ')).length;
      const text = allText(doc.blocks);

      expect(doc.title).toBe(`CreatorPantry ${page.label}`);
      expect(doc.blocks.filter((block) => block.kind === 'heading' && block.level === 2).length).toBe(sourceSections);
      expect(sourceSections).toBeGreaterThan(0);
      expect(text).not.toContain('MOCK-UP');
      expect(text).not.toContain('product fact');
      expect(text).not.toMatch(/\*\*|\]\(|\.md\b/);
    });
  }
});
