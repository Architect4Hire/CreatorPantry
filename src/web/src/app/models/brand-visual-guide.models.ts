import { BrandGuideChoice } from './brand-guide-control.models';
import {
  BrandVisualReference,
  BrandVisualStyleLine,
} from './brand-visual-style-control.models';
import {
  BrandWritingGuideActive,
  decodeBrandWritingGuideActive,
  staleReasonFor,
} from './brand-writing-guide.models';
import { isRecord } from './recipe.models';

/**
 * `GET /api/v1/workspaces/{slug}/brand-visual-guide?task=…` — which visual style an image screen would use and
 * which references it could name (11A.21b). Mirrors `BrandVisualGuideServiceModel`.
 *
 * Only ever the workspace's *active* guide. A reference is a title, an id and whether text from it would be used:
 * the server returns no file name, storage location or image content, and neither does this model.
 */
export type BrandVisualTask = 'photography-concept' | 'image-prompt';

export interface BrandVisualGuideReference {
  readonly documentId: string;
  readonly title: string;
  readonly usable: boolean;
  readonly unusableReason: string | null;
}

export interface BrandVisualGuide {
  /** Null when the workspace has activated no guide. An answer, not an error. */
  readonly activeGuide: BrandWritingGuideActive | null;
  readonly hasVisualGuidance: boolean;
  readonly styleLines: readonly BrandVisualStyleLine[];
  readonly negativeGuidance: string | null;
  readonly references: readonly BrandVisualGuideReference[];
  readonly referencesTruncated: boolean;

  /** False when the library could not be read: an empty list then means "could not load", not "none". */
  readonly referencesAvailable: boolean;
}

/** Everything the shared visual-style control needs, in its own vocabulary. */
export interface BrandVisualControlData {
  readonly activeGuide: BrandGuideChoice | null;
  readonly hasVisualGuidance: boolean;
  readonly styleLines: readonly BrandVisualStyleLine[];
  readonly negativeGuidance: string | null;
  readonly references: readonly BrandVisualReference[];
  readonly referencesTruncated: boolean;
  readonly referencesStatus: 'ready' | 'error';
}

function decodeReference(value: unknown): BrandVisualGuideReference | null {
  if (!isRecord(value)) return null;

  const { documentId, title, usable, unusableReason } = value;

  if (
    typeof documentId !== 'string' ||
    typeof title !== 'string' ||
    typeof usable !== 'boolean' ||
    (unusableReason !== null && typeof unusableReason !== 'string')
  ) {
    return null;
  }

  return { documentId, title, usable, unusableReason };
}

function decodeLines(value: unknown): BrandVisualStyleLine[] | null {
  if (!Array.isArray(value)) return null;

  const lines: BrandVisualStyleLine[] = [];

  for (const item of value) {
    if (!isRecord(item) || typeof item['label'] !== 'string' || typeof item['summary'] !== 'string') return null;
    lines.push({ label: item['label'], summary: item['summary'] });
  }

  return lines;
}

/** Decodes the response, or null when it is not the shape the contract promises. */
export function decodeBrandVisualGuide(value: unknown): BrandVisualGuide | null {
  if (!isRecord(value)) return null;

  const styleLines = decodeLines(value['styleLines']);
  const rawReferences = value['references'];
  const negativeGuidance = value['negativeGuidance'];

  if (
    styleLines === null ||
    !Array.isArray(rawReferences) ||
    typeof value['hasVisualGuidance'] !== 'boolean' ||
    typeof value['referencesTruncated'] !== 'boolean' ||
    typeof value['referencesAvailable'] !== 'boolean' ||
    (negativeGuidance !== null && typeof negativeGuidance !== 'string')
  ) {
    return null;
  }

  const references: BrandVisualGuideReference[] = [];

  for (const item of rawReferences) {
    const reference = decodeReference(item);

    if (reference === null) return null;
    references.push(reference);
  }

  const rawGuide = value['activeGuide'];
  const activeGuide = rawGuide === null ? null : decodeBrandWritingGuideActive(rawGuide);

  if (rawGuide !== null && activeGuide === null) return null;

  return {
    activeGuide,
    hasVisualGuidance: value['hasVisualGuidance'],
    styleLines,
    negativeGuidance,
    references,
    referencesTruncated: value['referencesTruncated'],
    referencesAvailable: value['referencesAvailable'],
  };
}

/** Maps the server's answer onto the control's inputs. */
export function toBrandVisualControlData(guide: BrandVisualGuide): BrandVisualControlData {
  const active = guide.activeGuide;

  return {
    activeGuide:
      active === null
        ? null
        : {
            guideId: active.guideId,
            name: active.name,
            versionNumber: active.versionNumber,
            isStale: active.isStale,
            staleReason: active.isStale ? staleReasonFor(active.staleSourceCount) : null,
            approvedAt: active.approvedAt,
            appliedSections: active.appliedSections,
          },
    hasVisualGuidance: guide.hasVisualGuidance,
    styleLines: guide.styleLines,
    negativeGuidance: guide.negativeGuidance,
    references: guide.references.map((reference) => ({
      documentId: reference.documentId,
      title: reference.title,
      usable: reference.usable,
      unusableReason: reference.unusableReason,
    })),
    referencesTruncated: guide.referencesTruncated,
    referencesStatus: guide.referencesAvailable ? 'ready' : 'error',
  };
}
