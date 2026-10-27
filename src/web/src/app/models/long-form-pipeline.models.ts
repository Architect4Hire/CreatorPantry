import { LongFormDraftStatus } from './long-form-draft.models';

/**
 * The long-form pipeline (LF.6.2, B-38): a guided journey of four steps from "what is this piece about" to an
 * accepted article or newsletter issue.
 *
 * **The journey's state is the draft's, on the server.** Which steps a creator may open is worked out from the
 * draft's status every time, never remembered: a plan approved in another tab opens Write here too, and a
 * device that has never seen the piece can pick it up where it stands. The device remembers one thing — the
 * step the creator last had open — and that is a convenience the server's answer always overrules.
 */
export type LongFormPipelineStepSlug = 'about' | 'outline' | 'write' | 'review';

export interface LongFormPipelineStepDefinition {
  readonly slug: LongFormPipelineStepSlug;
  readonly label: string;
  /** Behind "Need help with this step?": what the step is and what to do in it. */
  readonly help: string;
  /** Said once above the step, always shown: the one thing a creator should know before they start. */
  readonly legend: string;
}

export const LONG_FORM_PIPELINE_STEPS: readonly LongFormPipelineStepDefinition[] = [
  {
    slug: 'about',
    label: 'What it is about',
    help: 'Pick the recipe this piece is about, or describe it in your own words. You can do both. An article and a newsletter issue are planned differently, so say which this is. You can change the plan before anything is written.',
    legend: 'Say what this piece is about. A recipe, a few words of your own, or both.',
  },
  {
    slug: 'outline',
    label: 'Outline',
    help: 'This is the plan for your piece: its sections, in order, and what each is for. Rename, reorder, add or remove sections. Writing starts only after you approve it.',
    legend: 'Nothing is written yet. Change the plan until it is the piece you want, then approve it.',
  },
  {
    slug: 'write',
    label: 'Write',
    help: 'Each section is written on its own, so you can redo one without touching the others. Edit any section in your own words. Mark a section settled to leave it alone.',
    legend: 'Write one section at a time, or the rest in one go. You are shown what that will use before it is used.',
  },
  {
    slug: 'review',
    label: 'Review and accept',
    help: 'Read the whole piece. Accepting makes it your article of record; you can then download it and get SEO suggestions. It is not sent anywhere.',
    legend: 'Nothing is accepted until you say so, and accepting does not publish anything.',
  },
];

export const LONG_FORM_PIPELINE_STEP_COUNT = LONG_FORM_PIPELINE_STEPS.length;

export const LONG_FORM_PIPELINE_STEP_SLUGS: readonly LongFormPipelineStepSlug[] = LONG_FORM_PIPELINE_STEPS.map(
  (step) => step.slug,
);

export function isLongFormPipelineStepSlug(value: unknown): value is LongFormPipelineStepSlug {
  return typeof value === 'string' && (LONG_FORM_PIPELINE_STEP_SLUGS as readonly string[]).includes(value);
}

export function longFormPipelineStepIndex(slug: LongFormPipelineStepSlug): number {
  return LONG_FORM_PIPELINE_STEP_SLUGS.indexOf(slug);
}

/**
 * The furthest step a piece has reached, from where it stands on the server.
 *
 * - no draft yet: `about` — there is nothing to outline.
 * - `Draft`: `outline` — a plan has been asked for, or is there to edit, and is not approved.
 * - `Outlined`: `write` — the plan is approved and nothing has been written.
 * - `Writing` and later: `review` — writing has begun, so the piece can be read whole. Accepting it is the
 *   creator's to do there; reaching the step accepts nothing.
 */
export function furthestLongFormPipelineStep(status: LongFormDraftStatus | null): LongFormPipelineStepSlug {
  switch (status) {
    case null:
      return 'about';
    case 'Draft':
      return 'outline';
    case 'Outlined':
      return 'write';
    default:
      return 'review';
  }
}

/** Whether a step may be opened for a piece that has reached `furthest`. Every earlier step may. */
export function canOpenLongFormPipelineStep(
  slug: LongFormPipelineStepSlug,
  furthest: LongFormPipelineStepSlug,
): boolean {
  return longFormPipelineStepIndex(slug) <= longFormPipelineStepIndex(furthest);
}

/** A step's standing in the step list. Progress is the server's: `done` means the piece has moved past it. */
export function longFormPipelineStepStatus(
  slug: LongFormPipelineStepSlug,
  current: LongFormPipelineStepSlug,
  furthest: LongFormPipelineStepSlug,
  accepted: boolean,
): 'upcoming' | 'current' | 'done' {
  if (slug === current) return 'current';

  // The last step is done only when the piece is accepted: being able to open it finishes nothing.
  if (slug === 'review') return accepted ? 'done' : 'upcoming';

  return longFormPipelineStepIndex(slug) < longFormPipelineStepIndex(furthest) ? 'done' : 'upcoming';
}

/**
 * Where a creator was in the journey, on this device. Identifiers and a step: not a word of the piece, and
 * nothing the server does not already hold.
 */
export interface LongFormPipelinePlace {
  readonly contextId: string;
  /** Null while the piece is still on its first step and no draft has been started. */
  readonly draftId: string | null;
  readonly step: LongFormPipelineStepSlug;
}

const PLACE_VERSION = 1;

export function encodeLongFormPipelinePlace(place: LongFormPipelinePlace): string {
  return JSON.stringify({ v: PLACE_VERSION, contextId: place.contextId, draftId: place.draftId, step: place.step });
}

/** The place, or null for anything that is not one: an old shape, a hand-edited value, nothing at all. */
export function decodeLongFormPipelinePlace(raw: string | null): LongFormPipelinePlace | null {
  if (raw === null) return null;

  try {
    const value = JSON.parse(raw) as unknown;
    if (typeof value !== 'object' || value === null) return null;

    const { v, contextId, draftId, step } = value as Record<string, unknown>;

    return v === PLACE_VERSION &&
      typeof contextId === 'string' &&
      contextId !== '' &&
      (draftId === null || (typeof draftId === 'string' && draftId !== '')) &&
      isLongFormPipelineStepSlug(step)
      ? { contextId, draftId, step }
      : null;
  } catch {
    return null;
  }
}

/** The fewest and the most words a creator may ask a plan for (mirrors `AiPolicy.LongFormTargetWords*`). */
export const LONG_FORM_TARGET_WORDS_LIMITS = { min: 150, max: 3000 } as const;

/** The range a kind is planned to, in words, when the creator asks for nothing else (mirrors `LongFormPolicy`). */
export const LONG_FORM_TARGET_WORDS: Readonly<
  Record<'BlogArticle' | 'NewsletterIssue', { readonly min: number; readonly max: number }>
> = {
  BlogArticle: { min: 900, max: 1600 },
  NewsletterIssue: { min: 300, max: 700 },
};
