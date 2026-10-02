import { decodeEnum, isRecord } from './recipe.models';

/**
 * The text read from one version of a brand source document, as `GET .../versions/{n}/extraction` sends it.
 * `text` is present exactly when `state` is `Succeeded`; a scan or image carries `reason` instead and no text.
 * Never an address, a checksum or a person's id: the API sends none, and nothing here asks for one.
 */

export const BRAND_SOURCE_EXTRACTION_STATES = ['NotExtracted', 'Succeeded', 'Unsupported', 'Failed'] as const;
export type BrandSourceExtractionState = (typeof BRAND_SOURCE_EXTRACTION_STATES)[number];

export const BRAND_SOURCE_EXTRACTION_ORIGINS = ['Extracted', 'Corrected'] as const;
export type BrandSourceExtractionOrigin = (typeof BRAND_SOURCE_EXTRACTION_ORIGINS)[number];

const STATE_SET: ReadonlySet<string> = new Set(BRAND_SOURCE_EXTRACTION_STATES);
const ORIGIN_SET: ReadonlySet<string> = new Set(BRAND_SOURCE_EXTRACTION_ORIGINS);

/** The most text a correction may carry, as the server counts it (bytes of UTF-8). */
export const BRAND_SOURCE_CORRECTION_MAX_BYTES = 4 * 1024 * 1024;
export const BRAND_SOURCE_REASON_MAX = 500;

export interface BrandSourceExtraction {
  readonly versionNumber: number;
  readonly state: BrandSourceExtractionState;
  /** The artifact's identity; what a correction or a retry quotes. Null only when nothing has been read. */
  readonly id: string | null;
  readonly ordinal: number;
  readonly origin: BrandSourceExtractionOrigin | null;
  readonly text: string | null;
  /** Why there is no text, or that only the beginning was kept. Null when there is nothing to say. */
  readonly reason: string | null;
  /** Whether a read of this version is queued or running right now. */
  readonly isReading: boolean;
}

export function decodeBrandSourceExtraction(raw: unknown): BrandSourceExtraction | null {
  if (!isRecord(raw)) return null;
  const state = decodeEnum<BrandSourceExtractionState>(STATE_SET, raw['state']);
  const versionNumber = Number(raw['versionNumber']);
  if (state === null || !Number.isInteger(versionNumber)) return null;

  const id = raw['id'];
  const text = raw['text'];
  const reason = raw['reason'];
  // The API's own rule, restated: text exactly when it succeeded. Anything else is a response not to trust.
  if ((state === 'Succeeded') !== (typeof text === 'string')) return null;
  if (state !== 'NotExtracted' && typeof id !== 'string') return null;

  return {
    versionNumber,
    state,
    id: typeof id === 'string' ? id : null,
    ordinal: Number.isInteger(Number(raw['ordinal'])) ? Number(raw['ordinal']) : 0,
    origin: decodeEnum<BrandSourceExtractionOrigin>(ORIGIN_SET, raw['origin']),
    text: typeof text === 'string' ? text : null,
    reason: typeof reason === 'string' ? reason : null,
    isReading: raw['isReading'] === true,
  };
}
