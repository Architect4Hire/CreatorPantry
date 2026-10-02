import { decodeEnum, isRecord } from './recipe.models';

/**
 * The resumable "Create my voice" setup session, as `GET/PUT .../brand-setup-session` sends it, plus the fixed
 * list of steps the wizard walks. The session holds *progress and draft answers only*: nothing here is active
 * guidance, and completing it activates nothing (activation is a separate, later step).
 */

export interface BrandSetupStepDefinition {
  readonly slug: string;
  readonly label: string;
  /** Plain-language help for the step. No model, prompt or embedding vocabulary. */
  readonly help: string;
  /**
   * The step's one sentence about what has to be answered, shown above its sections by the shell.
   *
   * Said once per step, never field by field: marking one optional field among a dozen unmarked ones makes the
   * rest read as required (DESIGN-SYSTEM.md). Which way round it runs is the step's own business — most of this
   * wizard is optional and says so, while the first step names the two answers it needs.
   */
  readonly legend: string;
  /** What the placeholder body says until the step is built. */
  readonly comingSoon: string;
  /** An optional step may be skipped; the creator is never forced through it. */
  readonly skippable: boolean;
}

export const BRAND_SETUP_STEPS = [
  {
    slug: 'goals',
    label: "What you're making",
    help: 'Tell us what you create and who it is for, so everything that follows starts from the right place. You can change your answers later.',
    legend: "Tell us what you're creating for and where you'll share it. Everything else is optional.",
    comingSoon: 'Choosing what you make and who it is for is coming soon.',
    skippable: false,
  },
  {
    slug: 'style',
    label: 'How you sound',
    help: 'A few easy choices about how you like to write: friendly or formal, short or chatty. There are no wrong answers.',
    legend: "Every question here is optional, and there are no wrong answers.",
    comingSoon: 'Describing how you like to sound is coming soon.',
    skippable: false,
  },
  {
    slug: 'examples',
    label: 'Your examples',
    help: 'Writing you are proud of helps show your style. This step is optional, and you can skip it if you would rather describe your style in your own words.',
    legend: "Examples are optional. Add as many or as few as you like, or skip this step.",
    comingSoon: 'Adding examples of your writing is coming soon.',
    skippable: true,
  },
  {
    slug: 'review-text',
    label: 'Check the text',
    help: 'If you added examples, you can check that the words we read from them are right before they are used. Nothing is used until you say so.',
    legend: "Check the words we read from each example. Nothing is used until you say so.",
    comingSoon: 'Checking the text from your examples is coming soon.',
    skippable: true,
  },
  {
    slug: 'create',
    label: 'Build your guide',
    help: 'Choose whether to have a first draft of your guide written for you, or to write it yourself. Either way, you stay in charge.',
    legend: "Choose how your guide gets written. Nothing is switched on yet.",
    comingSoon: 'Building your guide is coming soon.',
    skippable: false,
  },
  {
    slug: 'edit',
    label: 'Review your guide',
    help: 'Read your guide one section at a time and change anything that does not sound like you. Your edits always win.',
    legend: "Change anything that does not sound like you. Your edits always win.",
    comingSoon: 'Reviewing and editing your guide is coming soon.',
    skippable: false,
  },
  {
    slug: 'finish',
    label: 'Try it and decide',
    help: 'See how your guide reads on a sample, then decide what happens next. Finishing setup does not switch anything on by itself.',
    legend: "Decide what happens to your guide. Nothing is switched on until you say so.",
    comingSoon: 'Trying your guide and deciding is coming soon.',
    skippable: false,
  },
] as const satisfies readonly BrandSetupStepDefinition[];

export type BrandSetupStepSlug = (typeof BRAND_SETUP_STEPS)[number]['slug'];

export const BRAND_SETUP_STEP_SLUGS: readonly BrandSetupStepSlug[] = BRAND_SETUP_STEPS.map((step) => step.slug);
export const FIRST_BRAND_SETUP_STEP: BrandSetupStepSlug = BRAND_SETUP_STEPS[0].slug;
export const LAST_BRAND_SETUP_STEP: BrandSetupStepSlug = BRAND_SETUP_STEPS[BRAND_SETUP_STEPS.length - 1].slug;

/** Mirrors the server's 64 KB ceiling on `draftJson`. */
export const BRAND_SETUP_DRAFT_MAX_CHARS = 64 * 1024;

const SLUG_SET: ReadonlySet<string> = new Set(BRAND_SETUP_STEP_SLUGS);

export function stepIndexOf(slug: string | null | undefined): number {
  return slug ? BRAND_SETUP_STEP_SLUGS.indexOf(slug as BrandSetupStepSlug) : -1;
}

export function isBrandSetupStepSlug(value: unknown): value is BrandSetupStepSlug {
  return typeof value === 'string' && SLUG_SET.has(value);
}

export type BrandSetupStepStatus = 'upcoming' | 'current' | 'done' | 'skipped';

export type BrandSetupSessionStatus = 'inProgress' | 'completed';

export interface BrandSetupSession {
  readonly status: BrandSetupSessionStatus;
  readonly currentStep: BrandSetupStepSlug;
  readonly furthestStep: BrandSetupStepSlug;
  readonly completedSteps: readonly BrandSetupStepSlug[];
  readonly skippedSteps: readonly BrandSetupStepSlug[];
  readonly draftJson: string;
  readonly createdUtc: string;
  readonly updatedUtc: string;
  readonly completedUtc: string | null;
  readonly rowVersion: string;
}

/** The request body of PUT; the server validates the same shape. */
export interface BrandSetupSessionWrite {
  readonly currentStep: BrandSetupStepSlug;
  readonly furthestStep: BrandSetupStepSlug;
  readonly completedSteps: readonly BrandSetupStepSlug[];
  readonly skippedSteps: readonly BrandSetupStepSlug[];
  readonly draftJson: string;
}

/** The creator's answers, one slice per step. The shell never looks inside a slice. */
export type BrandSetupDraft = Readonly<Record<string, Readonly<Record<string, unknown>>>>;

/**
 * What a step body tells the shell. The shell reads only these three facts; it never inspects `draft`.
 * `draft` is the step's own slice, or null when the step has nothing to hand over.
 */
export interface BrandSetupStepReport {
  readonly canContinue: boolean;
  readonly isDirty: boolean;
  readonly draft: Readonly<Record<string, unknown>> | null;
}

export const NOT_READY_REPORT: BrandSetupStepReport = { canContinue: false, isDirty: false, draft: null };

const SESSION_STATUSES: ReadonlySet<string> = new Set<BrandSetupSessionStatus>(['inProgress', 'completed']);

function decodeSlugs(value: unknown): BrandSetupStepSlug[] | null {
  if (!Array.isArray(value)) return null;
  // Unknown slugs (from a newer server) are dropped rather than failing the whole read.
  return value.filter(isBrandSetupStepSlug);
}

export function decodeBrandSetupSession(value: unknown): BrandSetupSession | null {
  if (!isRecord(value)) return null;

  const status = decodeEnum<BrandSetupSessionStatus>(SESSION_STATUSES, value['status']);
  const completedSteps = decodeSlugs(value['completedSteps']);
  const skippedSteps = decodeSlugs(value['skippedSteps']);
  const { currentStep, furthestStep, draftJson, createdUtc, updatedUtc, completedUtc, rowVersion } = value;

  if (
    status === null ||
    completedSteps === null ||
    skippedSteps === null ||
    !isBrandSetupStepSlug(currentStep) ||
    !isBrandSetupStepSlug(furthestStep) ||
    typeof draftJson !== 'string' ||
    typeof createdUtc !== 'string' ||
    typeof updatedUtc !== 'string' ||
    !(completedUtc === null || completedUtc === undefined || typeof completedUtc === 'string') ||
    typeof rowVersion !== 'string' ||
    rowVersion.length === 0
  ) {
    return null;
  }

  return {
    status,
    currentStep,
    furthestStep,
    completedSteps,
    skippedSteps,
    draftJson,
    createdUtc,
    updatedUtc,
    completedUtc: completedUtc ?? null,
    rowVersion,
  };
}

/** Parses `draftJson` into per-step slices; anything that is not an object of objects reads as empty. */
export function parseBrandSetupDraft(draftJson: string): BrandSetupDraft {
  try {
    const parsed: unknown = JSON.parse(draftJson);
    if (!isRecord(parsed) || Array.isArray(parsed)) return {};
    const slices: Record<string, Readonly<Record<string, unknown>>> = {};
    for (const [key, slice] of Object.entries(parsed)) {
      if (isRecord(slice) && !Array.isArray(slice)) slices[key] = slice;
    }
    return slices;
  } catch {
    return {};
  }
}

export type StepRouteResolution =
  | { readonly kind: 'ok'; readonly index: number }
  | { readonly kind: 'redirect'; readonly slug: BrandSetupStepSlug };

/**
 * Where a URL's `:step` may land. An unknown slug goes to the first step; a step beyond the furthest the
 * creator has reached goes to the furthest, so a pasted deep link cannot skip ahead.
 */
export function resolveStepRoute(slug: string | null, furthestIndex: number): StepRouteResolution {
  const index = stepIndexOf(slug);
  if (index < 0) return { kind: 'redirect', slug: FIRST_BRAND_SETUP_STEP };
  if (index > furthestIndex) return { kind: 'redirect', slug: BRAND_SETUP_STEP_SLUGS[Math.max(0, furthestIndex)] };
  return { kind: 'ok', index };
}

export function stepStatusOf(
  slug: BrandSetupStepSlug,
  currentSlug: BrandSetupStepSlug | null,
  completed: readonly BrandSetupStepSlug[],
  skipped: readonly BrandSetupStepSlug[],
): BrandSetupStepStatus {
  if (slug === currentSlug) return 'current';
  if (completed.includes(slug)) return 'done';
  if (skipped.includes(slug)) return 'skipped';
  return 'upcoming';
}

/** The word shown beside every step marker. Status is never carried by colour alone. */
export const STEP_STATUS_WORD: Readonly<Record<BrandSetupStepStatus, string>> = {
  upcoming: 'Not started',
  current: 'You are here',
  done: 'Done',
  skipped: 'Skipped',
};

export const STEP_STATUS_GLYPH: Readonly<Record<BrandSetupStepStatus, string>> = {
  upcoming: '○',
  current: '●',
  done: '✓',
  skipped: '↷',
};

/** Orders slugs by step order and removes duplicates, so equal progress always serialises the same way. */
export function normalizeSlugs(slugs: readonly BrandSetupStepSlug[]): BrandSetupStepSlug[] {
  return BRAND_SETUP_STEP_SLUGS.filter((slug) => slugs.includes(slug));
}
