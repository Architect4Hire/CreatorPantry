/**
 * The site's legal documents, read from the markdown the owner and counsel edit (`docs/legal/`) rather than from
 * a copy of it here. The build imports each file as text, so a page can never say something its source does not.
 *
 * This is not a general markdown renderer. It reads the handful of shapes those three files use — headings,
 * paragraphs, bulleted lists one level deep, tables, bold, italic, code and links — into plain data that a
 * template draws, so nothing in them reaches the page as HTML.
 *
 * Two things in the drafts are notes to their reviewers rather than text for a reader, and are left out:
 * a blockquote (the "mock-up for owner and legal review" banner, and the billing note in the Terms), and the
 * *(product fact)* marker beside a clause that has to be re-checked whenever the software changes. A bracketed
 * placeholder such as `[LEGAL ENTITY NAME]` is text, and shows as written: filling one in is the owner's.
 *
 * Only the legal pages' lazy chunk imports this. The footer's links are `legal-links.ts`.
 */

import { LEGAL_ROUTES } from './legal-links';

export type LegalInline =
  | { readonly kind: 'text'; readonly text: string }
  | { readonly kind: 'strong'; readonly text: string }
  | { readonly kind: 'em'; readonly text: string }
  | { readonly kind: 'code'; readonly text: string }
  | { readonly kind: 'route'; readonly text: string; readonly path: string }
  | { readonly kind: 'mail'; readonly text: string };

export interface LegalListItem {
  readonly content: readonly LegalInline[];
  readonly children: readonly (readonly LegalInline[])[];
}

export type LegalBlock =
  | { readonly kind: 'heading'; readonly level: 2 | 3; readonly id: string; readonly content: readonly LegalInline[] }
  | { readonly kind: 'paragraph'; readonly content: readonly LegalInline[] }
  | { readonly kind: 'list'; readonly items: readonly LegalListItem[] }
  | {
      readonly kind: 'table';
      /** What the table is about — the heading it sits under — for a reader who meets it on its own. */
      readonly label: string;
      readonly head: readonly (readonly LegalInline[])[];
      readonly rows: readonly (readonly (readonly LegalInline[])[])[];
    };

export interface LegalDocument {
  readonly title: string;
  readonly blocks: readonly LegalBlock[];
}

const PRODUCT_FACT = /\s*\*\(product fact\)\*/g;
const INLINE = /\*\*(.+?)\*\*|\*(.+?)\*|`([^`]+)`|\[([^\]]+)\]\(([^)\s]+)\)|([\w.+-]+@[\w-]+(?:\.[\w-]+)+)/g;

export function parseInline(source: string): LegalInline[] {
  const text = source.replace(PRODUCT_FACT, '');
  const spans: LegalInline[] = [];
  let from = 0;

  for (const match of text.matchAll(INLINE)) {
    if (match.index > from) spans.push({ kind: 'text', text: text.slice(from, match.index) });
    const [, strong, em, code, linkText, href, mail] = match;

    if (strong !== undefined) spans.push({ kind: 'strong', text: strong });
    else if (em !== undefined) spans.push({ kind: 'em', text: em });
    else if (code !== undefined) spans.push({ kind: 'code', text: code });
    else if (linkText !== undefined) {
      const path = LEGAL_ROUTES[href.split('#')[0]];
      spans.push(path ? { kind: 'route', text: linkText, path } : { kind: 'text', text: linkText });
    } else spans.push({ kind: 'mail', text: mail });

    from = match.index + match[0].length;
  }

  if (from < text.length) spans.push({ kind: 'text', text: text.slice(from) });
  return spans;
}

/** A heading's anchor, so "see section 3" can be linked to: `## 3. Cookies and storage` is `cookies-and-storage`. */
export function headingId(text: string): string {
  return text
    .toLowerCase()
    .replace(/^\d+\.\s*/, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

const cells = (row: string): string[] =>
  row
    .trim()
    .replace(/^\||\|$/g, '')
    .split('|')
    .map((cell) => cell.trim());

export function parseLegalDocument(markdown: string): LegalDocument {
  const lines = markdown.replace(/\r\n?/g, '\n').split('\n');
  const blocks: LegalBlock[] = [];
  let title = '';
  let section = '';
  let index = 0;

  const isBlank = (line: string) => line.trim().length === 0;
  const startsBlock = (line: string) => /^(#{1,3} |- |\||>)/.test(line);

  while (index < lines.length) {
    const line = lines[index];

    if (isBlank(line)) {
      index++;
    } else if (line.startsWith('# ')) {
      title = line.slice(2).trim();
      index++;
    } else if (/^#{2,3} /.test(line)) {
      const level = line.startsWith('### ') ? 3 : 2;
      const text = line.slice(level + 1).trim();
      section = text.replace(/^\d+\.\s*/, '');
      blocks.push({ kind: 'heading', level, id: headingId(text), content: parseInline(text) });
      index++;
    } else if (line.startsWith('>')) {
      while (index < lines.length && lines[index].startsWith('>')) index++;
    } else if (line.startsWith('|')) {
      const rows: string[][] = [];
      while (index < lines.length && lines[index].startsWith('|')) {
        const row = cells(lines[index]);
        if (!row.every((cell) => /^:?-+:?$/.test(cell))) rows.push(row);
        index++;
      }
      const [head = [], ...body] = rows;
      blocks.push({ kind: 'table', label: section || title, head: head.map(parseInline), rows: body.map((row) => row.map(parseInline)) });
    } else if (line.startsWith('- ')) {
      const items: { text: string; children: string[] }[] = [];
      while (index < lines.length && !isBlank(lines[index]) && (lines[index].startsWith('- ') || lines[index].startsWith(' '))) {
        const current = lines[index];
        const nested = current.match(/^\s+- (.*)$/);
        const last = items.at(-1);
        if (current.startsWith('- ')) items.push({ text: current.slice(2).trim(), children: [] });
        else if (nested && last) last.children.push(nested[1].trim());
        else if (last && last.children.length > 0) last.children[last.children.length - 1] += ` ${current.trim()}`;
        else if (last) last.text += ` ${current.trim()}`;
        index++;
      }
      blocks.push({
        kind: 'list',
        items: items.map((item) => ({ content: parseInline(item.text), children: item.children.map(parseInline) })),
      });
    } else {
      const text: string[] = [];
      while (index < lines.length && !isBlank(lines[index]) && (text.length === 0 || !startsBlock(lines[index]))) {
        text.push(lines[index].trim());
        index++;
      }
      blocks.push({ kind: 'paragraph', content: parseInline(text.join(' ')) });
    }
  }

  return { title, blocks };
}
