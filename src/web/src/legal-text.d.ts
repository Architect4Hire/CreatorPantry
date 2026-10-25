/**
 * A markdown file imported as its text, with `with { loader: 'text' }`. The legal pages read `docs/legal/` this
 * way so the site and the documents counsel reviews are the same words (`features/landing/legal-pages.ts`).
 */
declare module '*.md' {
  const markdown: string;
  export default markdown;
}
