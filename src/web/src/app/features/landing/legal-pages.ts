import acceptableUse from '../../../../../../docs/legal/acceptable-use-policy.md' with { loader: 'text' };
import privacy from '../../../../../../docs/legal/privacy-policy.md' with { loader: 'text' };
import terms from '../../../../../../docs/legal/terms-of-service.md' with { loader: 'text' };

import { LEGAL_LINKS, LegalLink, LegalPageKey } from './legal-links';

export interface LegalPage extends LegalLink {
  readonly markdown: string;
}

const MARKDOWN: Readonly<Record<LegalPageKey, string>> = { privacy, terms, 'acceptable-use': acceptableUse };

/** The three documents' text, which only the legal pages' own lazy chunk imports. */
export const LEGAL_PAGES: readonly LegalPage[] = LEGAL_LINKS.map((link) => ({ ...link, markdown: MARKDOWN[link.key] }));

export function legalPage(key: unknown): LegalPage | undefined {
  return LEGAL_PAGES.find((page) => page.key === key);
}
