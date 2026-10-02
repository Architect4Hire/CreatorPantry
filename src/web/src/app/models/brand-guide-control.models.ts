/**
 * What the shared brand-guide control (11A.21a) shows and what it hands back.
 *
 * These are the control's own view types, not API shapes: a host maps its guide read into them, and the
 * control never sees an embedding, a chunk, a prompt or a storage path. Nothing here carries a workspace id —
 * the workspace comes from the route, never from a choice a creator makes on a form.
 */

/** One guide version a creator can write with. */
export interface BrandGuideChoice {
  readonly guideId: string;
  readonly name: string;
  readonly versionNumber: number;

  /** True when the sources behind this version have changed since it was approved. */
  readonly isStale: boolean;

  /** Plain-language reason, shown beside the warning. Null falls back to a generic sentence. */
  readonly staleReason?: string | null;

  /** When this version was approved, ISO 8601. Shown only behind "Guide details". */
  readonly approvedAt?: string | null;

  /** Plain labels for the parts of the guide this piece uses ("Tone", "Vocabulary"). Details only. */
  readonly appliedSections?: readonly string[];
}

/** A channel rule in the creator's words — never a section key, a prompt fragment or a token count. */
export interface BrandGuideChannelRule {
  readonly channel: string;
  readonly summary: string;
}

/** What the creator has chosen for this piece. `default` follows whichever guide is active. */
export type BrandGuideSelection =
  | { readonly kind: 'default' }
  | { readonly kind: 'override'; readonly guideId: string; readonly versionNumber: number }
  | { readonly kind: 'none' };

export type BrandGuideControlStatus = 'loading' | 'error' | 'ready';

export type BrandGuideRulesStatus = 'loading' | 'error' | 'ready';

/**
 * What a host may do, given a selection. The single answer both the control and the host's Submit read, so the
 * two cannot disagree about whether a stale or missing guide is acceptable.
 */
export type BrandGuideUse =
  | {
      readonly kind: 'guide';
      readonly source: 'default' | 'override';
      readonly guide: BrandGuideChoice;
      readonly stale: boolean;

      /** True while a stale guide has not been explicitly accepted. A host must not submit while blocked. */
      readonly blocked: boolean;
    }
  | { readonly kind: 'none'; readonly reason: 'chosen' | 'no-active-guide'; readonly blocked: false }
  /** An override that no longer exists. Blocked: falling back to another guide would be the silent swap. */
  | { readonly kind: 'unavailable'; readonly blocked: true };

export interface BrandGuideUseInputs {
  readonly selection: BrandGuideSelection;
  readonly activeGuide: BrandGuideChoice | null;
  readonly choices: readonly BrandGuideChoice[];
  readonly staleAcknowledged: boolean;
}

/**
 * Decides which guide a request would use — and refuses to guess.
 *
 * A stale guide is never used silently: it is `blocked` until the creator says to continue. A chosen guide that
 * has gone missing is `unavailable` rather than quietly replaced by the active one.
 */
export function resolveBrandGuideUse(inputs: BrandGuideUseInputs): BrandGuideUse {
  const { selection, activeGuide, choices, staleAcknowledged } = inputs;

  if (selection.kind === 'none') return { kind: 'none', reason: 'chosen', blocked: false };

  if (selection.kind === 'default') {
    if (activeGuide === null) return { kind: 'none', reason: 'no-active-guide', blocked: false };

    return guideUse('default', activeGuide, staleAcknowledged);
  }

  const picked = [...choices, ...(activeGuide === null ? [] : [activeGuide])].find(
    (choice) => choice.guideId === selection.guideId && choice.versionNumber === selection.versionNumber,
  );

  return picked === undefined ? { kind: 'unavailable', blocked: true } : guideUse('override', picked, staleAcknowledged);
}

function guideUse(source: 'default' | 'override', guide: BrandGuideChoice, staleAcknowledged: boolean): BrandGuideUse {
  return { kind: 'guide', source, guide, stale: guide.isStale, blocked: guide.isStale && !staleAcknowledged };
}

/** The key a `<select>` option carries for a guide version. */
export function brandGuideChoiceKey(choice: Pick<BrandGuideChoice, 'guideId' | 'versionNumber'>): string {
  return `${choice.guideId}@${choice.versionNumber}`;
}
