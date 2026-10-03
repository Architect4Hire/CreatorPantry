import {
  BRAND_SOURCE_EXTRACTION_ORIGINS,
  BRAND_SOURCE_EXTRACTION_STATES,
  BrandSourceExtractionOrigin,
  BrandSourceExtractionState,
} from './brand-source-extraction.models';
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

/**
 * One document as `GET .../brand-source-documents/{id}` sends it, in full.
 *
 * A superset of a library row — the same document plus the three things a list deliberately leaves out: the
 * token a change must quote, the current version's checksum, and when it was last archived. The detail screen
 * needs all of it; the setup wizard reads the subset it cares about.
 */
export interface BrandSourceDocumentDetail extends BrandLibraryRow {
  /** What the next archive, replacement or restore must quote. A different token means it changed since the read. */
  readonly concurrencyToken: string;
  /** `sha256:` and the digest of the current version's bytes. Also that version's download entity tag. */
  readonly contentChecksum: string;
  /** When the document was last archived, kept through a restore. Null for one never archived. */
  readonly archivedAt: string | null;
}

export function decodeBrandSourceDocumentDetail(raw: unknown): BrandSourceDocumentDetail | null {
  const row = decodeBrandLibraryRow(raw);
  if (!row || !isRecord(raw) || !isRecord(raw['currentVersion'])) return null;

  const token = raw['concurrencyToken'];
  const checksum = raw['currentVersion']['contentChecksum'];
  const archivedAt = raw['archivedAt'];
  if (typeof token !== 'string' || typeof checksum !== 'string') return null;

  return {
    ...row,
    concurrencyToken: token,
    contentChecksum: checksum,
    archivedAt: typeof archivedAt === 'string' ? archivedAt : null,
  };
}

// ---------------------------------------------------------------------------
// Version history, as `GET .../brand-source-documents/{id}/versions` sends it.
// ---------------------------------------------------------------------------

/**
 * One version a document has had: the file's own facts, and where *its* text stands.
 *
 * Extraction is per version, not per document: text stays attached to the version it was read from, so a
 * superseded version keeps the text it had while a newer one reads `NotExtracted`. This list is the only
 * place that history is visible.
 *
 * **`createdByMembershipId` is deliberately not decoded.** The API sends it, but nothing can turn a
 * membership id into a person's name yet, and a raw identifier on screen is worse than saying nothing about
 * who added a version. Decoding it as required would also mean a response missing it drops the whole row —
 * a new way to fail, for a field no surface reads. Add it here, and to the decoder, alongside whatever
 * resolves member display names.
 */
export interface BrandSourceVersionRow {
  readonly id: string;
  readonly versionNumber: number;
  readonly mediaType: string;
  readonly sizeBytes: number;
  readonly originalFileName: string;
  readonly contentChecksum: string;
  readonly createdAt: string;
  /** Exactly one row of a complete history is current, and only it accepts a replacement, retry or correction. */
  readonly isCurrent: boolean;
  readonly extraction: BrandSourceExtractionSummary;
}

export interface BrandSourceVersionPage {
  readonly items: readonly BrandSourceVersionRow[];
  readonly nextCursor: string | null;
}

export function decodeBrandSourceVersionRow(raw: unknown): BrandSourceVersionRow | null {
  if (!isRecord(raw)) return null;

  const extraction = decodeBrandSourceExtractionSummary(raw['extraction']);
  const versionNumber = Number(raw['versionNumber']);
  const size = Number(raw['sizeBytes']);

  if (
    extraction === null ||
    typeof raw['id'] !== 'string' ||
    !Number.isInteger(versionNumber) ||
    versionNumber < 1 ||
    typeof raw['mediaType'] !== 'string' ||
    !Number.isFinite(size) ||
    typeof raw['originalFileName'] !== 'string' ||
    typeof raw['contentChecksum'] !== 'string' ||
    typeof raw['createdAt'] !== 'string'
  ) {
    return null;
  }

  return {
    id: raw['id'],
    versionNumber,
    mediaType: raw['mediaType'],
    sizeBytes: size,
    originalFileName: raw['originalFileName'],
    contentChecksum: raw['contentChecksum'],
    createdAt: raw['createdAt'],
    isCurrent: raw['isCurrent'] === true,
    extraction,
  };
}

export function decodeBrandSourceVersionPage(raw: unknown): BrandSourceVersionPage | null {
  if (!isRecord(raw) || !Array.isArray(raw['items'])) return null;

  const items: BrandSourceVersionRow[] = [];
  for (const item of raw['items']) {
    const decoded = decodeBrandSourceVersionRow(item);
    // A row this client cannot read is left out rather than failing the whole history.
    if (decoded) items.push(decoded);
  }

  const next = raw['nextCursor'];
  return { items, nextCursor: typeof next === 'string' ? next : null };
}

// ---------------------------------------------------------------------------
// Usage, as `GET .../brand-source-documents/{id}/usage` sends it.
// ---------------------------------------------------------------------------

export const BRAND_SOURCE_HOLD_KINDS = ['StyleGuideVersion'] as const;
export type BrandSourceHoldKind = (typeof BRAND_SOURCE_HOLD_KINDS)[number];

const HOLD_KIND_SET: ReadonlySet<string> = new Set(BRAND_SOURCE_HOLD_KINDS);

/** One record that points at one version of this document. */
export interface BrandSourceHold {
  readonly kind: BrandSourceHoldKind;
  readonly holderId: string;
  /** The guide's display name, so a screen can name what it is about rather than showing an id. */
  readonly holderName: string | null;
  readonly holderVersionNumber: number;
  /** Which of *this document's* versions is cited. */
  readonly sourceVersionNumber: number;
  /** A draft holder is a link, not a hold: it may still be superseded. */
  readonly isApproved: boolean;
}

/**
 * What still points at one document, and what a removal would not take with it.
 *
 * Nothing here is a warning about data loss, because a removal causes none: every version row and stored
 * object survives it, and a guide version that cited this document still resolves afterwards.
 */
export interface BrandSourceUsage {
  readonly documentId: string;
  readonly versionCount: number;
  readonly storedBytes: number;
  /** True when at least one *approved* guide version cites one of this document's versions. */
  readonly isHeld: boolean;
  readonly holds: readonly BrandSourceHold[];
}

function decodeBrandSourceHold(raw: unknown): BrandSourceHold | null {
  if (!isRecord(raw)) return null;

  const kind = decodeEnum<BrandSourceHoldKind>(HOLD_KIND_SET, raw['kind']);
  const holderVersionNumber = Number(raw['holderVersionNumber']);
  const sourceVersionNumber = Number(raw['sourceVersionNumber']);
  const name = raw['holderName'];

  if (
    kind === null ||
    typeof raw['holderId'] !== 'string' ||
    !Number.isInteger(holderVersionNumber) ||
    !Number.isInteger(sourceVersionNumber)
  ) {
    return null;
  }

  return {
    kind,
    holderId: raw['holderId'],
    holderName: typeof name === 'string' ? name : null,
    holderVersionNumber,
    sourceVersionNumber,
    isApproved: raw['isApproved'] === true,
  };
}

export function decodeBrandSourceUsage(raw: unknown): BrandSourceUsage | null {
  if (!isRecord(raw)) return null;

  const versionCount = Number(raw['versionCount']);
  const storedBytes = Number(raw['storedBytes']);

  if (
    typeof raw['documentId'] !== 'string' ||
    !Number.isInteger(versionCount) ||
    versionCount < 1 ||
    !Number.isFinite(storedBytes)
  ) {
    return null;
  }

  const holds: BrandSourceHold[] = [];
  for (const hold of Array.isArray(raw['holds']) ? raw['holds'] : []) {
    const decoded = decodeBrandSourceHold(hold);
    // A hold this client cannot read is left out; `isHeld` is the server's own word and is not recomputed.
    if (decoded) holds.push(decoded);
  }

  return {
    documentId: raw['documentId'],
    versionCount,
    storedBytes,
    isHeld: raw['isHeld'] === true,
    holds,
  };
}

// ---------------------------------------------------------------------------
// The Brand Library list. A superset of the wizard's row: the same document, plus the facts a library screen
// shows and filters on. Kept beside the wizard's shape rather than folded into it, so the two screens cannot
// quietly change each other's contract, and decoded through the same function so the shared fields are read
// one way only.
// ---------------------------------------------------------------------------

/** The statuses the list will answer for. `Removed` is a tombstone the API never lists and never accepts here. */
export const BRAND_LIBRARY_STATUSES = ['Active', 'Archived'] as const;
export type BrandLibraryStatus = (typeof BRAND_LIBRARY_STATUSES)[number];

const STATUS_SET: ReadonlySet<string> = new Set(BRAND_LIBRARY_STATUSES);
const EXTRACTION_STATE_SET: ReadonlySet<string> = new Set(BRAND_SOURCE_EXTRACTION_STATES);
const EXTRACTION_ORIGIN_SET: ReadonlySet<string> = new Set(BRAND_SOURCE_EXTRACTION_ORIGINS);

/**
 * Where a row's current version stands with text, as the list reports it: the state, who produced the latest
 * text, and when. Not the artifact itself — no text, no id, no address.
 */
export interface BrandSourceExtractionSummary {
  readonly state: BrandSourceExtractionState;
  /** Null exactly when nothing has been read yet. */
  readonly origin: BrandSourceExtractionOrigin | null;
  readonly at: string | null;
}

export interface BrandLibraryRow extends BrandSourceDocumentSummary {
  readonly channelKey: string | null;
  readonly audience: string | null;
  readonly tags: readonly string[];
  readonly versionNumber: number;
  readonly extraction: BrandSourceExtractionSummary;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/**
 * What the library is asking for. Every field is a filter the server applies, which is the whole point: a
 * page is a page of a filtered set, so narrowing it in the browser would hide matches sitting on pages this
 * screen has not fetched — the one list bug a reader cannot see.
 */
export interface BrandLibraryQuery {
  readonly search: string;
  readonly status: BrandLibraryStatus;
  readonly documentType: BrandSourceDocumentType | null;
  readonly purpose: BrandSourcePurpose | null;
  readonly channelKey: string | null;
  readonly extractionState: BrandSourceExtractionState | null;
  /** The previous page's `nextCursor`, verbatim, or null for the first page. */
  readonly cursor: string | null;
  readonly limit: number;
}

export const DEFAULT_BRAND_LIBRARY_QUERY: BrandLibraryQuery = {
  search: '',
  status: 'Active',
  documentType: null,
  purpose: null,
  channelKey: null,
  extractionState: null,
  cursor: null,
  limit: 25,
};

export interface BrandLibraryPage {
  readonly items: readonly BrandLibraryRow[];
  readonly nextCursor: string | null;
}

export function decodeBrandLibraryStatus(value: unknown): BrandLibraryStatus | null {
  return decodeEnum<BrandLibraryStatus>(STATUS_SET, value);
}

export function decodeBrandSourceExtractionSummary(raw: unknown): BrandSourceExtractionSummary | null {
  if (!isRecord(raw)) return null;
  const state = decodeEnum<BrandSourceExtractionState>(EXTRACTION_STATE_SET, raw['state']);
  if (state === null) return null;

  const at = raw['at'];
  return {
    state,
    origin: decodeEnum<BrandSourceExtractionOrigin>(EXTRACTION_ORIGIN_SET, raw['origin']),
    at: typeof at === 'string' ? at : null,
  };
}

export function decodeBrandLibraryRow(raw: unknown): BrandLibraryRow | null {
  const summary = decodeBrandSourceDocument(raw);
  if (!summary || !isRecord(raw) || !isRecord(raw['currentVersion'])) return null;

  const extraction = decodeBrandSourceExtractionSummary(raw['extraction']);
  const versionNumber = Number(raw['currentVersion']['versionNumber']);
  const channelKey = raw['channelKey'];
  const audience = raw['audience'];
  const createdAt = raw['createdAt'];
  const updatedAt = raw['updatedAt'];

  if (
    extraction === null ||
    !Number.isInteger(versionNumber) ||
    versionNumber < 1 ||
    typeof createdAt !== 'string' ||
    typeof updatedAt !== 'string'
  ) {
    return null;
  }

  return {
    ...summary,
    channelKey: typeof channelKey === 'string' ? channelKey : null,
    audience: typeof audience === 'string' ? audience : null,

    // Tags are the server's, already normalized and sorted. An entry that is not a string is dropped rather
    // than rendered as "undefined" beside the ones that are.
    tags: Array.isArray(raw['tags']) ? raw['tags'].filter((tag): tag is string => typeof tag === 'string') : [],
    versionNumber,
    extraction,
    createdAt,
    updatedAt,
  };
}

export function decodeBrandLibraryPage(raw: unknown): BrandLibraryPage | null {
  if (!isRecord(raw) || !Array.isArray(raw['items'])) return null;

  const items: BrandLibraryRow[] = [];
  for (const item of raw['items']) {
    const decoded = decodeBrandLibraryRow(item);
    // A row this client cannot read is left out rather than failing the whole page, as the wizard's list does.
    if (decoded) items.push(decoded);
  }

  const next = raw['nextCursor'];
  return { items, nextCursor: typeof next === 'string' ? next : null };
}
