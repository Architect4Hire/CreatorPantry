import { BrandSourceDocumentDetail } from '../../../models/brand-source-document.models';
import { ExampleKind, SavedExample, decodeExamples } from './brand-setup-examples';
import { decodeConfirmed } from './brand-setup-review-text';

/** What the "Build your guide" step asks, shows and remembers. Plain data, no framework. */

/** How the guide gets written. Nothing is preselected: a default here would be a decision made for them. */
export type GuideMethod = 'draft' | 'myself';

export interface CreateChoice {
  readonly method: GuideMethod | null;
  /**
   * Only meaningful for `draft`: the creator has said in so many words that writing it may spend some of
   * their AI allowance. Kept when they switch to writing it themselves, so going back to a draft does not
   * ask them the same question twice.
   */
  readonly costAcknowledged: boolean;
}

export const NO_CHOICE: CreateChoice = { method: null, costAcknowledged: false };

/** Reads a saved slice defensively: anything unknown or malformed reads as no choice at all. */
export function decodeCreateChoice(slice: Readonly<Record<string, unknown>> | null): CreateChoice {
  const method = slice?.['method'];
  return {
    method: method === 'draft' || method === 'myself' ? method : null,
    costAcknowledged: slice?.['costAcknowledged'] === true,
  };
}

export function sameChoice(a: CreateChoice, b: CreateChoice): boolean {
  return a.method === b.method && a.costAcknowledged === b.costAcknowledged;
}

/** One example the creator picked earlier, as this step looks it up. */
export interface SourceRow {
  readonly documentId: string;
  readonly kind: ExampleKind;
  readonly detail: BrandSourceDocumentDetail | null;
  readonly load: 'loading' | 'ready' | 'unreachable';
}

export type SourceState = 'loading' | 'checked' | 'unchecked' | 'visual' | 'left-out' | 'missing' | 'unreachable';

/**
 * What one example's row says.
 *
 * `checked` means the creator said its text looks right in the step before. Whether that decision still names
 * the newest reading of the file is the checking step's business, not this one's — this step reports what they
 * decided, and never re-opens it.
 */
export function sourceStateOf(row: SourceRow, checked: ReadonlySet<string>): SourceState {
  if (row.load === 'loading') return 'loading';
  if (row.load === 'unreachable') return 'unreachable';
  if (row.detail === null) return 'missing';
  if (row.detail.status === 'Archived') return 'left-out';
  if (row.kind === 'visual') return 'visual';
  return checked.has(row.documentId) ? 'checked' : 'unchecked';
}

/** The states whose example is really going to be used. */
export function isUsed(state: SourceState): boolean {
  return state === 'checked' || state === 'unchecked' || state === 'visual';
}

/** Every example the creator picked, in the order they added them. */
export function selectedSources(slice: Readonly<Record<string, unknown>> | null): readonly SavedExample[] {
  return decodeExamples(slice);
}

/** The examples whose text the creator said looks right, by document. */
export function checkedIdsOf(slice: Readonly<Record<string, unknown>> | null): ReadonlySet<string> {
  return new Set(decodeConfirmed(slice).map((entry) => entry.documentId));
}

/** "2 examples and 1 look" — the words for a count of each, or an empty string when nothing is going to be used. */
export function usedSummary(words: number, looks: number): string {
  const parts: string[] = [];
  if (words > 0) parts.push(words === 1 ? '1 example' : `${words} examples`);
  if (looks > 0) parts.push(looks === 1 ? '1 look' : `${looks} looks`);
  if (parts.length === 0) return '';
  return `${parts.join(' and ')} will be used.`;
}
