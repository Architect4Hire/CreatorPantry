import {
  BRAND_SOURCE_ACCEPT,
  BRAND_SOURCE_MAX_BYTES,
  BRAND_SOURCE_TEXT_MAX_BYTES,
  BrandSourceDocumentSummary,
  BrandSourceDocumentType,
  BrandSourcePurpose,
} from '../../../models/brand-source-document.models';
import { BrandSourceAddFailure } from '../../../services/brand-source-document.service';

/** What the "Your examples" step asks, and how a saved slice is read back. Plain data, no framework. */

export type ExampleKind = 'sounds-like-me' | 'not-like-me' | 'blog' | 'social' | 'newsletter' | 'visual' | 'other';

export interface ExampleKindOption {
  readonly key: ExampleKind;
  readonly label: string;
  readonly hint: string;
  /** How the library files it. The creator sees the label, never these. */
  readonly documentType: BrandSourceDocumentType;
  readonly purpose: BrandSourcePurpose;
}

export const EXAMPLE_KINDS: readonly ExampleKindOption[] = [
  { key: 'sounds-like-me', label: 'This sounds like me', hint: 'Something you wrote that captures your voice', documentType: 'WritingSample', purpose: 'Voice' },
  { key: 'not-like-me', label: "This doesn't sound like me", hint: 'Something to steer away from', documentType: 'WritingSample', purpose: 'NotMyVoice' },
  { key: 'blog', label: 'A blog post', hint: 'One of your published posts', documentType: 'PublishedPost', purpose: 'WritingStyle' },
  { key: 'social', label: 'A social post', hint: 'A caption or post you shared', documentType: 'SocialSample', purpose: 'WritingStyle' },
  { key: 'newsletter', label: 'A newsletter', hint: 'An email you sent your readers', documentType: 'Newsletter', purpose: 'WritingStyle' },
  { key: 'visual', label: 'A look I like', hint: 'A photo or layout that shows your style', documentType: 'VisualReference', purpose: 'VisualDirection' },
  { key: 'other', label: 'Something else', hint: 'Anything else that helps', documentType: 'Other', purpose: 'Background' },
];

const KIND_KEYS: ReadonlySet<string> = new Set(EXAMPLE_KINDS.map((k) => k.key));

export function kindOption(key: ExampleKind): ExampleKindOption {
  return EXAMPLE_KINDS.find((k) => k.key === key) ?? EXAMPLE_KINDS[EXAMPLE_KINDS.length - 1];
}

/** The label for an example already in the library, from how it was filed. */
export function kindOfDocument(document: BrandSourceDocumentSummary): ExampleKind {
  const exact = EXAMPLE_KINDS.find((k) => k.documentType === document.documentType && k.purpose === document.purpose);
  if (exact) return exact.key;
  if (document.purpose === 'NotMyVoice') return 'not-like-me';
  return EXAMPLE_KINDS.find((k) => k.documentType === document.documentType)?.key ?? 'other';
}

export const FAILURE_COPY: Readonly<Record<BrandSourceAddFailure, string>> = {
  unsupported: "We can't use that kind of file. Try a PDF, Word document, text file, web page or image.",
  too_large: 'That file is too big. Files can be up to 20 MB, and text files up to 5 MB.',
  corrupt: 'That file looks damaged. Try saving it again, or paste the text instead.',
  rejected: "We couldn't accept that file. Try a different one.",
  invalid: "Something about that example wasn't right. Check it and try again.",
  forbidden: "You don't have permission to add examples here. Ask an Owner or Editor.",
  not_found: "We couldn't find this workspace. Go back to your brand settings and try again.",
  key_reused: "That didn't go through. Remove it and add it again.",
  cancelled: 'Stopped.',
  unavailable: "We couldn't add that right now. Try again in a moment.",
};

/** Only a failure that might not repeat is worth a Retry; the rest need a different file. */
export function canRetry(failure: BrandSourceAddFailure | null): boolean {
  return failure === 'unavailable';
}

const ACCEPTED = new Set(BRAND_SOURCE_ACCEPT.split(','));
const TEXT_EXTENSIONS = new Set(['.md', '.txt', '.html']);

function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot < 0 ? '' : name.slice(dot).toLowerCase();
}

/** A convenience check before anything is sent. The server still decides from the bytes. */
export function precheckFile(file: { readonly name: string; readonly size: number }): BrandSourceAddFailure | null {
  const extension = extensionOf(file.name);
  if (!ACCEPTED.has(extension)) return 'unsupported';
  if (file.size > BRAND_SOURCE_MAX_BYTES) return 'too_large';
  if (TEXT_EXTENSIONS.has(extension) && file.size > BRAND_SOURCE_TEXT_MAX_BYTES) return 'too_large';
  if (file.size === 0) return 'corrupt';
  return null;
}

export function pastedTitle(now: Date): string {
  return `Pasted text, ${now.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })}`;
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} bytes`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export interface SavedExample {
  readonly documentId: string;
  readonly kind: ExampleKind;
}

/** Reads a saved slice defensively: anything unknown or malformed is left out. */
export function decodeExamples(slice: Readonly<Record<string, unknown>> | null): readonly SavedExample[] {
  const raw = slice?.['items'];
  if (!Array.isArray(raw)) return [];
  const seen = new Set<string>();
  const items: SavedExample[] = [];
  for (const entry of raw) {
    if (typeof entry !== 'object' || entry === null) continue;
    const { documentId, kind } = entry as Record<string, unknown>;
    if (typeof documentId !== 'string' || typeof kind !== 'string' || !KIND_KEYS.has(kind) || seen.has(documentId)) continue;
    seen.add(documentId);
    items.push({ documentId, kind: kind as ExampleKind });
  }
  return items;
}
