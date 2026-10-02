import {
  BrandGuideChoice,
  BrandGuideSelection,
  BrandGuideUse,
  resolveBrandGuideUse,
} from './brand-guide-control.models';

/**
 * What the shared visual-style control (11A.21b) shows and hands back, for photography and image-prompt setup.
 *
 * View types, not API shapes. Nothing here names a storage location, an embedding, a provider or a workspace:
 * a reference is a library document the creator already owns, identified by its id and a title.
 */

/** The most reference examples one generation may name. Matches the server's cap on named source documents. */
export const BRAND_VISUAL_MAX_REFERENCES = 10;

/** One library document that could serve as a visual reference example. */
export interface BrandVisualReference {
  readonly documentId: string;
  readonly title: string;

  /** False when no text could be read from it, so nothing of it would reach the generation. */
  readonly usable: boolean;

  /** Plain-language reason shown when it is not usable. */
  readonly unusableReason?: string | null;
}

/** One line of the look the guide describes — "Photography direction: soft window light". */
export interface BrandVisualStyleLine {
  readonly label: string;
  readonly summary: string;
}

/**
 * What a host may do, given a selection — the one answer the control and the host's Submit both read.
 *
 * `no-visual-guide` is a guide that is active but describes no look: not an error and not blocked, but not the
 * same message as having no guide at all, because the remedy differs (write the visual sections, not set up a
 * guide).
 */
export type BrandVisualUse =
  | {
      readonly kind: 'guide';
      readonly source: 'default' | 'override';
      readonly guide: BrandGuideChoice;
      readonly stale: boolean;
      readonly blocked: boolean;
    }
  | {
      readonly kind: 'no-visual-guide';
      readonly source: 'default' | 'override';
      readonly guide: BrandGuideChoice;
      readonly stale: boolean;
      readonly blocked: boolean;
    }
  | { readonly kind: 'none'; readonly reason: 'chosen' | 'no-active-guide'; readonly blocked: false }
  | { readonly kind: 'unavailable'; readonly blocked: true };

export interface BrandVisualUseInputs {
  readonly selection: BrandGuideSelection;
  readonly activeGuide: BrandGuideChoice | null;
  readonly choices: readonly BrandGuideChoice[];
  readonly staleAcknowledged: boolean;

  /** Whether the guide that would be used describes any visual style. */
  readonly hasVisualGuidance: boolean;
}

export function resolveBrandVisualUse(inputs: BrandVisualUseInputs): BrandVisualUse {
  const base: BrandGuideUse = resolveBrandGuideUse(inputs);

  if (base.kind !== 'guide') return base;

  return {
    kind: inputs.hasVisualGuidance ? 'guide' : 'no-visual-guide',
    source: base.source,
    guide: base.guide,
    stale: base.stale,
    blocked: base.blocked,
  };
}

/**
 * Something the generation would quietly do differently from what the creator may expect — told to them first,
 * where the server can only report it afterwards. None of these blocks a request.
 */
export type BrandVisualConflict =
  | { readonly kind: 'reference-unusable'; readonly documentId: string; readonly title: string; readonly reason: string }
  | { readonly kind: 'reference-missing'; readonly documentId: string }
  | { readonly kind: 'guide-not-active'; readonly name: string; readonly versionNumber: number };

export const REFERENCE_UNUSABLE_FALLBACK = 'No text could be read from it, so it will not be used. Its image is never sent.';

export interface BrandVisualConflictInputs {
  readonly use: BrandVisualUse;
  readonly activeGuide: BrandGuideChoice | null;
  readonly references: readonly BrandVisualReference[];
  readonly selectedReferenceIds: readonly string[];
}

/**
 * What would not work as the creator might expect. Reference conflicts are only reported while a guide is in
 * use: without one the server sends no documents at all, and warning about a reference that was never going to
 * be sent would be noise.
 */
export function resolveBrandVisualConflicts(inputs: BrandVisualConflictInputs): BrandVisualConflict[] {
  const { use, activeGuide, references, selectedReferenceIds } = inputs;
  const conflicts: BrandVisualConflict[] = [];

  if (use.kind === 'none' || use.kind === 'unavailable') return conflicts;

  for (const id of selectedReferenceIds) {
    const reference = references.find((candidate) => candidate.documentId === id);

    if (reference === undefined) conflicts.push({ kind: 'reference-missing', documentId: id });
    else if (!reference.usable) {
      conflicts.push({
        kind: 'reference-unusable',
        documentId: id,
        title: reference.title,
        reason: reference.unusableReason?.trim() || REFERENCE_UNUSABLE_FALLBACK,
      });
    }
  }

  const sameAsActive =
    activeGuide !== null &&
    use.guide.guideId === activeGuide.guideId &&
    use.guide.versionNumber === activeGuide.versionNumber;

  if (use.source === 'override' && !sameAsActive) {
    conflicts.push({ kind: 'guide-not-active', name: use.guide.name, versionNumber: use.guide.versionNumber });
  }

  return conflicts;
}
