/**
 * Where the three legal documents live, and nothing else about them. Its own file, apart from both their text
 * (`legal-pages.ts`) and the parser that reads it (`legal-document.ts`), because the landing page's footer links
 * to them and the landing page is the one eager route: whatever the footer imports is in the first screen's
 * bundle (LP.11).
 */

/**
 * Where a link to a sibling document goes. A link to any other file has nowhere to go on the site, so it
 * keeps its words and loses the link.
 */
export const LEGAL_ROUTES: Readonly<Record<string, string>> = {
  'privacy-policy.md': '/privacy',
  'terms-of-service.md': '/terms',
  'acceptable-use-policy.md': '/acceptable-use',
};

/** Which of the three a route shows: its `data.legal`. */
export type LegalPageKey = 'privacy' | 'terms' | 'acceptable-use';

export interface LegalLink {
  readonly key: LegalPageKey;
  /** The footer's words for it, and the browser tab's. */
  readonly label: string;
  readonly path: string;
}

/** The three, in the order the footer lists them. */
export const LEGAL_LINKS: readonly LegalLink[] = [
  { key: 'privacy', label: 'Privacy Policy', path: LEGAL_ROUTES['privacy-policy.md'] },
  { key: 'terms', label: 'Terms of Service', path: LEGAL_ROUTES['terms-of-service.md'] },
  { key: 'acceptable-use', label: 'Acceptable Use Policy', path: LEGAL_ROUTES['acceptable-use-policy.md'] },
];
