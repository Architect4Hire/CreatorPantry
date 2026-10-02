import { decodeEnum, isRecord } from './recipe.models';

/**
 * Brand source documents as `GET/POST .../brand-source-documents` send them, reduced to what the setup wizard
 * shows: what the example is, what it was filed as, and the file's name and size. Never a storage location,
 * extracted text or a checksum — the API sends none, and nothing here asks for one.
 */

export const BRAND_SOURCE_DOCUMENT_TYPES = [
  'StyleGuide',
  'WritingSample',
  'PublishedPost',
  'Newsletter',
  'SocialSample',
  'VisualReference',
  'Other',
] as const;
export type BrandSourceDocumentType = (typeof BRAND_SOURCE_DOCUMENT_TYPES)[number];

export const BRAND_SOURCE_PURPOSES = ['Voice', 'WritingStyle', 'VisualDirection', 'Background', 'NotMyVoice'] as const;
export type BrandSourcePurpose = (typeof BRAND_SOURCE_PURPOSES)[number];

const TYPE_SET: ReadonlySet<string> = new Set(BRAND_SOURCE_DOCUMENT_TYPES);
const PURPOSE_SET: ReadonlySet<string> = new Set(BRAND_SOURCE_PURPOSES);

/** The files the library accepts, as the file picker's `accept` list. The server decides from the bytes. */
export const BRAND_SOURCE_ACCEPT = '.pdf,.docx,.md,.txt,.html,.png,.jpg,.jpeg,.webp';
export const BRAND_SOURCE_MAX_BYTES = 20 * 1024 * 1024;
export const BRAND_SOURCE_TEXT_MAX_BYTES = 5 * 1024 * 1024;
export const BRAND_SOURCE_TITLE_MAX = 200;
export const BRAND_SOURCE_AUDIENCE_MAX = 500;

export interface BrandSourceDocumentSummary {
  readonly id: string;
  readonly title: string;
  readonly documentType: BrandSourceDocumentType;
  readonly purpose: BrandSourcePurpose;
  readonly status: string;
  readonly fileName: string;
  readonly sizeBytes: number;
  readonly mediaType: string;
}

/** What the creator is adding, as the API files it. */
export interface BrandSourceDescription {
  readonly title: string;
  readonly documentType: BrandSourceDocumentType;
  readonly purpose: BrandSourcePurpose;
  readonly audience?: string;
}

export interface BrandSourcePasteBody extends BrandSourceDescription {
  readonly text: string;
}

export interface BrandSourceDocumentPage {
  readonly items: readonly BrandSourceDocumentSummary[];
  readonly nextCursor: string | null;
}

/** Reads one document from either the upload response or a list item. Unknown shapes decode to null. */
export function decodeBrandSourceDocument(raw: unknown): BrandSourceDocumentSummary | null {
  if (!isRecord(raw) || !isRecord(raw['currentVersion'])) return null;
  const version = raw['currentVersion'];
  const documentType = decodeEnum<BrandSourceDocumentType>(TYPE_SET, raw['documentType']);
  const purpose = decodeEnum<BrandSourcePurpose>(PURPOSE_SET, raw['purpose']);
  const size = Number(version['sizeBytes']);
  if (
    typeof raw['id'] !== 'string' ||
    typeof raw['title'] !== 'string' ||
    typeof raw['status'] !== 'string' ||
    documentType === null ||
    purpose === null ||
    typeof version['originalFileName'] !== 'string' ||
    typeof version['mediaType'] !== 'string' ||
    !Number.isFinite(size)
  ) {
    return null;
  }
  return {
    id: raw['id'],
    title: raw['title'],
    documentType,
    purpose,
    status: raw['status'],
    fileName: version['originalFileName'],
    sizeBytes: size,
    mediaType: version['mediaType'],
  };
}

export function decodeBrandSourceDocumentPage(raw: unknown): BrandSourceDocumentPage | null {
  if (!isRecord(raw) || !Array.isArray(raw['items'])) return null;
  const items: BrandSourceDocumentSummary[] = [];
  for (const item of raw['items']) {
    const decoded = decodeBrandSourceDocument(item);
    // An item this client cannot read is left out rather than failing the whole list.
    if (decoded) items.push(decoded);
  }
  const next = raw['nextCursor'];
  return { items, nextCursor: typeof next === 'string' ? next : null };
}

/** One document as `GET .../brand-source-documents/{id}` sends it: what the review step needs to act on it. */
export interface BrandSourceDocumentDetail extends BrandSourceDocumentSummary {
  readonly versionNumber: number;
  /** What the next archive or restore must quote. A different token means the document changed since it was read. */
  readonly concurrencyToken: string;
}

export function decodeBrandSourceDocumentDetail(raw: unknown): BrandSourceDocumentDetail | null {
  const summary = decodeBrandSourceDocument(raw);
  if (!summary || !isRecord(raw) || !isRecord(raw['currentVersion'])) return null;
  const versionNumber = Number(raw['currentVersion']['versionNumber']);
  const token = raw['concurrencyToken'];
  if (!Number.isInteger(versionNumber) || versionNumber < 1 || typeof token !== 'string') return null;
  return { ...summary, versionNumber, concurrencyToken: token };
}
