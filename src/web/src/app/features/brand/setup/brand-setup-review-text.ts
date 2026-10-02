import { BrandSourceDocumentDetail } from '../../../models/brand-source-document.models';
import { BRAND_SOURCE_CORRECTION_MAX_BYTES, BrandSourceExtraction } from '../../../models/brand-source-extraction.models';
import { BrandSourceExtractionFailure } from '../../../services/brand-source-extraction.service';
import { ExampleKind, decodeExamples } from './brand-setup-examples';

/** What the "Check the text" step shows, decides and remembers. Plain data, no framework. */

export type CardKind =
  | 'checking'
  | 'waiting'
  | 'stopped'
  | 'failed'
  | 'scan'
  | 'read'
  | 'partial'
  | 'confirmed'
  | 'left-out'
  | 'unreachable';

export interface ReviewCard {
  readonly documentId: string;
  readonly kind: ExampleKind;
  readonly detail: BrandSourceDocumentDetail | null;
  readonly extraction: BrandSourceExtraction | null;
  readonly load: 'loading' | 'ready' | 'unreachable';
}

export interface ConfirmedText {
  readonly documentId: string;
  /** The exact artifact the creator said looks right. A later correction or re-read is a different one. */
  readonly extractionId: string;
}

/** The cards that stop Continue until the creator decides something about them. */
export const NEEDS_DECISION: ReadonlySet<CardKind> = new Set<CardKind>([
  'checking',
  'waiting',
  'stopped',
  'failed',
  'scan',
  'read',
  'partial',
  'unreachable',
]);

export function cardKindOf(card: ReviewCard, confirmed: readonly ConfirmedText[]): CardKind {
  if (card.load === 'loading') return 'checking';
  if (card.load === 'unreachable' || !card.detail || !card.extraction) return 'unreachable';
  if (card.detail.status === 'Archived') return 'left-out';

  const extraction = card.extraction;
  if (extraction.isReading) return 'waiting';

  switch (extraction.state) {
    case 'NotExtracted':
      return 'stopped';
    case 'Failed':
      return 'failed';
    case 'Unsupported':
      return 'scan';
    case 'Succeeded': {
      // A creator's own correction is not generated text, and they have just read every word of it.
      if (extraction.origin === 'Corrected') return 'confirmed';
      if (confirmed.some((c) => c.documentId === card.documentId && c.extractionId === extraction.id)) return 'confirmed';
      return extraction.reason ? 'partial' : 'read';
    }
  }
}

/** Photos and "a look I like" have no text to check. */
export function isTextual(kind: ExampleKind): boolean {
  return kind !== 'visual';
}

export function sourcesOf(slice: Readonly<Record<string, unknown>> | null): readonly { documentId: string; kind: ExampleKind }[] {
  return decodeExamples(slice).filter((s) => isTextual(s.kind));
}

export function visualCountOf(slice: Readonly<Record<string, unknown>> | null): number {
  return decodeExamples(slice).filter((s) => !isTextual(s.kind)).length;
}

/** Reads a saved slice defensively: anything unknown or malformed is left out. */
export function decodeConfirmed(slice: Readonly<Record<string, unknown>> | null): readonly ConfirmedText[] {
  const raw = slice?.['confirmed'];
  if (!Array.isArray(raw)) return [];
  const seen = new Set<string>();
  const items: ConfirmedText[] = [];
  for (const entry of raw) {
    if (typeof entry !== 'object' || entry === null) continue;
    const { documentId, extractionId } = entry as Record<string, unknown>;
    if (typeof documentId !== 'string' || typeof extractionId !== 'string' || seen.has(documentId)) continue;
    seen.add(documentId);
    items.push({ documentId, extractionId });
  }
  return items;
}

export const PREVIEW_CHARS = 400;

/** The beginning of the text, cut at a word, for the card. The whole text is one disclosure away. */
export function previewOf(text: string): string {
  const trimmed = text.trim();
  if (trimmed.length <= PREVIEW_CHARS) return trimmed;
  const cut = trimmed.slice(0, PREVIEW_CHARS);
  const space = cut.lastIndexOf(' ');
  return `${(space > PREVIEW_CHARS / 2 ? cut.slice(0, space) : cut).trimEnd()}…`;
}

export const REASON_CORRECTED = 'Corrected by me';
export const REASON_TYPED = 'Typed in by me';

export function correctionProblem(text: string): string {
  if (text.trim() === '') return 'Type or paste some text first.';
  if (new TextEncoder().encode(text).length > BRAND_SOURCE_CORRECTION_MAX_BYTES) return 'Text can be up to 4 MB.';
  return '';
}

export const FAILURE_COPY: Readonly<Record<BrandSourceExtractionFailure, string>> = {
  conflict: 'This text changed while you were editing. Your version is still here.',
  not_retryable: "Reading this again wouldn't change anything. You can edit the text yourself instead.",
  archived: 'This example is left out. Bring it back to change it.',
  superseded: 'A newer file replaced this one. Go back a step and add it again to check the new text.',
  pending: "We're still reading this one. Try again in a moment.",
  invalid: "That text has characters we can't keep, like hidden formatting. Remove them and try again.",
  forbidden: "You don't have permission to change examples here. Ask an Owner or Editor.",
  not_found: "We couldn't find this example any more.",
  key_reused: "That didn't go through. Try again.",
  unavailable: "We couldn't do that right now. Try again in a moment.",
};

export const SHELF_COPY = {
  leaveOut: "We couldn't leave that one out just now. Try again in a moment.",
  bringBack: "We couldn't bring that one back just now. Try again in a moment.",
  forbidden: "You don't have permission to change examples here. Ask an Owner or Editor.",
} as const;
