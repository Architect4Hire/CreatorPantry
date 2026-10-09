// The Content Pipeline's steps, its in-progress draft, and the limits its fields obey (PIPE-UI-001/002).
//
// View and storage types, not API shapes. The pipeline writes nothing to the server in these two steps: the
// seed generator persists nothing by design, and everything the creator has filled in lives in the draft below
// until a later step submits it. Nothing here names a provider, a blob, a prompt or an embedding.
//
// Every numeric and length limit mirrors a server constant by name, so a field cannot accept something the
// route it eventually feeds would refuse.

import {
  ContentSeed,
  ContentSeedPinName,
  ContentSeedQuery,
  DAY_OF_WEEK_VALUES,
  DayOfWeek,
  decodeContentSeed,
  isContentSeedToken,
} from './content-seed.models';
import { CreativeContextBriefSource, LinkedRecipe } from './creative-context.models';
import {
  CreativeContextFields,
  EMPTY_CREATIVE_CONTEXT_FIELDS,
  decodeCreativeContextFieldsPart,
} from './creative-context-fields.models';
import { GENERATED_IMAGE_PROMPT_MAX_LENGTH } from './generated-image.models';
import { PhotographyShotKind, decodePhotographyShotKind } from './photography-concept.models';
import { decodeEnum, isRecord } from './recipe.models';

export interface ContentPipelineStepDefinition {
  readonly slug: string;
  readonly label: string;
  /** Plain-language help for the step. No model, prompt, storage or provider vocabulary. */
  readonly help: string;
  /**
   * The step's one sentence about what has to be answered, shown above its sections by the shell.
   *
   * Said once per step, never field by field: marking one optional field among a dozen unmarked ones makes the
   * rest read as required (DESIGN-SYSTEM.md).
   */
  readonly legend: string;
  /** What the step's body says until the step is built. Null for a step that is. */
  readonly comingSoon: string | null;
}

/**
 * The whole journey, including the four steps that are not built yet.
 *
 * Declared in full on purpose. The progress figure a creator reads has to be the truth about the journey rather
 * than about how much of it has shipped, and a later step can then be filled in without reworking the shell —
 * which is how `BrandSetupShellComponent` was grown. A step with a `comingSoon` body offers no action at all.
 */
export const CONTENT_PIPELINE_STEPS = [
  {
    slug: 'setup',
    label: 'Set it up',
    help: 'Where the post is going, which day it is for, how many pictures to try, and anything you already know you want in the shot. All of it is optional, and you can change any of it later.',
    legend: 'Everything on this step is optional. Fill in what you already know.',
    comingSoon: null,
  },
  {
    slug: 'idea',
    label: 'Pick an idea',
    help: 'A starting point put together from the cooking vocabulary and your own weekly theme. Say what you already know — the cuisine, the kind of dish, how it is cooked — and the rest is suggested around it. Keep the parts you like, swap the rest, and pick one when it looks right. Nothing is written down until you do.',
    legend: 'Say what you know, keep what you like, and try again for the rest. Nothing is saved until you pick one.',
    comingSoon: null,
  },
  {
    slug: 'prompt',
    label: 'Write the prompt',
    help: 'A few looks planned for you, then the words that describe the picture. Add a brief or a photograph you like if you have one. Whatever you write yourself is what gets used.',
    legend: 'Pick a look and a shot, then the prompt. Everything in Extras is optional.',
    comingSoon: null,
  },
  {
    slug: 'images',
    label: 'Make the images',
    help: 'Your prompt goes off to be turned into pictures — one to four of them, as you asked on the first step. Look at what comes back, keep the ones worth using, download any you want a copy of, and tidy the rest away.',
    legend: 'Keep at least one picture. Nothing is filed in your library on this step.',
    comingSoon: null,
  },
  {
    slug: 'posts',
    label: 'Write the posts',
    help: 'Writing the words that go out with the pictures, for each place you publish.',
    legend: '',
    comingSoon: 'Writing the posts is coming soon.',
  },
  {
    slug: 'library',
    label: 'Save to your library',
    help: 'Filing the pictures you kept, so you can find them again and see where they came from. Each one is saved on its own, with what you want to say about it.',
    legend: 'Save the ones you want to keep. A picture you do not save is not kept.',
    comingSoon: null,
  },
] as const satisfies readonly ContentPipelineStepDefinition[];

export type ContentPipelineStepSlug = (typeof CONTENT_PIPELINE_STEPS)[number]['slug'];

export const CONTENT_PIPELINE_STEP_SLUGS: readonly ContentPipelineStepSlug[] = CONTENT_PIPELINE_STEPS.map(
  (step) => step.slug,
);

export const FIRST_CONTENT_PIPELINE_STEP: ContentPipelineStepSlug = CONTENT_PIPELINE_STEPS[0].slug;
export const CONTENT_PIPELINE_STEP_COUNT = CONTENT_PIPELINE_STEPS.length;

const STEP_SLUG_SET: ReadonlySet<string> = new Set(CONTENT_PIPELINE_STEP_SLUGS);

export function isContentPipelineStepSlug(value: unknown): value is ContentPipelineStepSlug {
  return typeof value === 'string' && STEP_SLUG_SET.has(value);
}

export function contentPipelineStepIndex(slug: string | null | undefined): number {
  return slug ? CONTENT_PIPELINE_STEP_SLUGS.indexOf(slug as ContentPipelineStepSlug) : -1;
}

export type ContentPipelineStepStatus = 'upcoming' | 'current' | 'done';

export function contentPipelineStepStatus(
  slug: ContentPipelineStepSlug,
  current: ContentPipelineStepSlug | null,
  furthest: ContentPipelineStepSlug,
): ContentPipelineStepStatus {
  if (slug === current) return 'current';
  return contentPipelineStepIndex(slug) < contentPipelineStepIndex(furthest) ? 'done' : 'upcoming';
}

export const STEP_STATUS_GLYPH: Readonly<Record<ContentPipelineStepStatus, string>> = {
  upcoming: '○',
  current: '◉',
  done: '●',
};

/** The word beside the glyph, because status is never carried by shape or colour alone. */
export const STEP_STATUS_WORD: Readonly<Record<ContentPipelineStepStatus, string>> = {
  upcoming: 'Not started',
  current: 'You are here',
  done: 'Done',
};

/**
 * Every limit the two built steps enforce, each mirroring the server constant named beside it.
 *
 * Enforced here so a creator is stopped at the control rather than by a refusal after the fact. The server
 * still decides: these are the same numbers, not a substitute for them.
 */
export const CONTENT_PIPELINE_LIMITS = {
  /** `AiPolicy.PhotographyCreatorConceptMaxLength`. */
  conceptMaxLength: 1000,
  /** `AiPolicy.PhotographyOverrideMaxLength` — one scene element or style direction. */
  overrideMaxLength: 300,
  /**
   * `GeneratedImageInputChecks.PromptTextMaxLength` — the prompt, and the avoid list beside it.
   *
   * The prompt box had no bound until now because nothing consumed it; the image route does, and it refuses a
   * longer one. Enforced at the control so a creator is stopped while writing rather than after asking, and
   * taken from the model that talks to that route so the number has one home rather than two.
   */
  promptMaxLength: GENERATED_IMAGE_PROMPT_MAX_LENGTH,
  /** `AiPolicy.MaxPhotographyOverrideCount` — per list, scene and style counted separately. */
  maxOverrides: 10,
  /** `CreativeContextPolicy.WorkingTitleMaxLength` — the name the creator gives what the picture is of. */
  subjectMaxLength: 200,
  /** `MediaPolicy.MinVariantsPerOperation`. */
  minVariants: 1,
  /**
   * `MediaPolicy.MaxVariantsPerOperation`. Four is a contact sheet a creator can compare, and it is also a
   * spend guard — every variant is a separate charge.
   */
  maxVariants: 4,
  /**
   * The largest stored draft that will be read back.
   *
   * A ceiling on what one workspace's draft may occupy in a browser that has a shared, small quota. Every field
   * is already bounded, so reaching this means something wrote the key that was not this product, and the
   * remedy is the same as for any unreadable draft: discard it and start clean.
   */
  storedMaxChars: 64 * 1024,
} as const;

/** How many pictures to try, as the only four answers there are. */
export const VARIANT_COUNT_CHOICES: readonly number[] = [1, 2, 3, 4];

/**
 * The default. Two rather than one, because the step after this one is a choice between pictures and a single
 * image gives nothing to choose between; and two rather than four, because the count is a cost.
 */
export const DEFAULT_VARIANT_COUNT = 2;

/** What the creator filled in on `setup`. All of it optional, which is this capability's shape. */
export interface ContentPipelineConfig {
  /**
   * What the picture is of, named by the creator. Empty for none.
   *
   * The context's `workingTitle`: a dish name for work with no recipe in the library behind it. It is what the
   * idea, the looks and the prompt are built around when no recipe is linked — see {@link contentSubjectOf},
   * which is the one place that precedence is decided.
   */
  readonly subject: string;
  /** A `ContentChannel.key`, or null for no channel in particular. */
  readonly channelKey: string | null;
  readonly day: DayOfWeek | null;
  readonly variantCount: number;
  /** Scene elements — a surface, a season, a time of day, a prop. The creator's own words. */
  readonly scene: readonly string[];
  /** Style directions — a mood, a palette, a treatment. The creator's own words. */
  readonly style: readonly string[];
  /** The picture the creator already has in mind, in their own words. Never written over by this product. */
  readonly concept: string;
  /**
   * What the creator chose to plan the picture from — their description, the idea they picked, or both — or
   * null until they have chosen (AF.3.2).
   */
  readonly briefSource: ContentPipelineBriefSource | null;
  /**
   * The brief that choice made, and theirs to edit from then on. This is what a look is planned from; nothing
   * reads {@link concept} or an idea in its place. Empty until they have chosen.
   */
  readonly brief: string;
}

/** One string for a linked recipe and its pinned version, for telling two apart. Empty for none. */
export function linkedRecipeKey(recipe: LinkedRecipe | null): string {
  return recipe === null ? '' : `${recipe.recipeId}@${recipe.recipeVersionId ?? ''}`;
}

/**
 * The typed name to build around, or null when a linked recipe is what the work is about.
 *
 * **The linked recipe always wins**, and this is the only place that is decided. A recipe is creator-owned
 * source material the server reads for itself — its title, cuisine, course and method — where the typed name
 * is a name and nothing else; sending both would ask every route to settle the same question again, and two
 * answers would eventually disagree. The name is still *kept* on the work, because it is the creator's own and
 * unlinking the recipe brings it back as the subject.
 */
export function contentSubjectOf(config: Pick<ContentPipelineConfig, 'subject'>, recipe: LinkedRecipe | null): string | null {
  const typed = config.subject.trim();

  return recipe !== null || typed === '' ? null : typed;
}

/** `CreativeContextBriefSource`, under the name the pipeline's own types use. */
export type ContentPipelineBriefSource = CreativeContextBriefSource;

const BRIEF_SOURCE_VALUES: ReadonlySet<string> = new Set<ContentPipelineBriefSource>(['Description', 'Idea', 'Combined']);

/** How each choice is said to the creator, wherever it is named. */
export const CONTENT_PIPELINE_BRIEF_SOURCE_LABELS: Readonly<Record<ContentPipelineBriefSource, string>> = {
  Description: 'Work from my description',
  Idea: 'Work from this idea',
  Combined: 'Combine them',
};

/** What separates the creator's description from the idea's wording in a combined brief: one blank line. */
const BRIEF_JOIN = '\n\n';

/**
 * The brief a choice makes, or null when the choice has nothing to make it from.
 *
 * **The description is never rewritten.** It is used exactly as typed; "combine" puts the idea's wording
 * *below* it, after a blank line, so the creator can see where their words end and the suggestion begins.
 */
export function contentPipelineBriefFor(
  source: ContentPipelineBriefSource,
  description: string,
  idea: string | null,
): string | null {
  const hasDescription = description.trim() !== '';
  const hasIdea = idea !== null && idea.trim() !== '';

  if (source === 'Description') return hasDescription ? description : null;
  if (source === 'Idea') return hasIdea ? idea : null;

  return hasDescription && hasIdea ? `${description}${BRIEF_JOIN}${idea}` : null;
}

/** True when the brief is not simply what its source makes — so replacing it would lose the creator's edits. */
export function isContentPipelineBriefEdited(draft: ContentPipelineDraft): boolean {
  const { briefSource, brief, concept } = draft.config;
  if (brief.trim() === '') return false;
  if (briefSource === null) return true;

  return contentPipelineBriefFor(briefSource, concept, draft.seed.accepted?.description ?? null) !== brief;
}

/**
 * The brief after a change to what it is made from.
 *
 * A brief the creator has not touched follows its source: correcting the description on the setup step
 * corrects a brief chosen from it. One they have edited is theirs and stays as it is. A choice whose source is
 * gone — the idea was un-picked, the description emptied — and whose brief was never edited is cleared, so the
 * creator is asked again rather than left planning from wording that is no longer on screen.
 */
export function contentPipelineBriefAfter(
  previous: ContentPipelineDraft,
  next: ContentPipelineDraft,
): Pick<ContentPipelineConfig, 'briefSource' | 'brief'> {
  const { briefSource, brief } = next.config;

  // The creator set the brief or the choice in this change: that is the answer.
  if (briefSource !== previous.config.briefSource || brief !== previous.config.brief) return { briefSource, brief };
  if (briefSource === null || isContentPipelineBriefEdited(previous)) return { briefSource, brief };

  const made = contentPipelineBriefFor(briefSource, next.config.concept, next.seed.accepted?.description ?? null);

  return made === null ? { briefSource: null, brief: '' } : { briefSource, brief: made };
}

/** The five facets a creator can lock on the `idea` step. */
export type ContentSeedKeepName = 'cuisine' | 'dishType' | 'method' | 'photographyStyle' | 'occasion';

export const CONTENT_SEED_KEEP_NAMES: readonly ContentSeedKeepName[] = [
  'cuisine',
  'dishType',
  'method',
  'photographyStyle',
  'occasion',
];

/**
 * The facet keys locked against a re-roll, by facet name.
 *
 * **Channel and day are not in here.** They have a home on `setup`, and keeping one from a generated seed
 * writes it there instead — so each of those two values has exactly one place it lives and the setup step is
 * never out of step with the idea on screen.
 */
export type ContentPipelineKeep = { readonly [K in ContentSeedKeepName]?: string };

export const CONTENT_SEED_FACET_LABELS: Readonly<Record<ContentSeedPinName | 'day' | 'theme', string>> = {
  cuisine: 'Cuisine',
  dishType: 'Dish type',
  method: 'Method',
  photographyStyle: 'Photo style',
  channel: 'Channel',
  occasion: 'Occasion',
  day: 'Day',
  theme: "That day's theme",
};

export interface ContentPipelineSeedState {
  /**
   * The token of the seed last on screen, so the same one comes back after a refresh.
   *
   * The seed itself is not stored while it is only a suggestion — it is not a record, and keeping a copy would
   * make it one. The token is what reproduces it, which is exactly what the contract offers.
   */
  readonly lastToken: string | null;
  readonly keep: ContentPipelineKeep;
  /** The idea the creator picked. Stored whole, because from here on it is a decision rather than a suggestion. */
  readonly accepted: ContentSeed | null;
}

/**
 * A document of the creator's, as the pipeline refers to one.
 *
 * Both a brief and a reference image are brand source documents they already own — the only thing the IMG routes
 * will read. The title is kept beside the id so the step can name what is attached without a round trip, and it
 * is a copy: renaming the document in the library does not rename it here.
 */
export interface ContentPipelineDocumentRef {
  readonly documentId: string;
  readonly title: string;
}

/** Which concept, and which of its shots, the creator picked to compose a prompt for. */
export interface ContentPipelineChosenShot {
  /** The IMG-001 request whose proposal holds the concept. */
  readonly conceptRequestId: string;
  /** The concept's server-minted id. A client only ever echoes one it was shown. */
  readonly conceptId: string;
  /** Kept so the step can name the pick without the proposal, which is not stored. */
  readonly label: string;
  readonly shotKind: PhotographyShotKind;
}

/**
 * Whose words the prompt currently is.
 *
 * **It replaces a plain "edited" flag, which was doing two jobs badly.** A surface has to answer two questions
 * about the prompt — *may this be replaced without asking?* and *was this written by a model?* — and a boolean
 * conflated them: taking the wording from a reference reading is not an edit, but it is a choice, and the text
 * is still generated. Four states answer both.
 *
 * `creator` is the safe reading for anything unrecognised: the worst mistake here is replacing words a person
 * wrote without asking.
 */
export type ContentPipelinePromptSource = 'none' | 'composed' | 'reference' | 'creator';

const PROMPT_SOURCE_VALUES: ReadonlySet<string> = new Set<ContentPipelinePromptSource>([
  'none',
  'composed',
  'reference',
  'creator',
]);

/** True for a prompt the creator did not write, which must therefore be shown as generated. */
export function isGeneratedPromptSource(source: ContentPipelinePromptSource): boolean {
  return source === 'composed' || source === 'reference';
}

/** True when replacing the prompt would take away something the creator chose or wrote. */
export function promptReplacementNeedsAsking(prompt: ContentPipelinePromptState): boolean {
  return prompt.finalPrompt.trim() !== '' && prompt.promptSource !== 'composed' && prompt.promptSource !== 'none';
}

/** What the model composed, kept beside the creator's own text so they can see what they changed. */
export interface ContentPipelineGeneratedPrompt {
  readonly text: string;
  readonly avoid: readonly string[];
}

/**
 * The prompt step's state (PIPE-UI-003).
 *
 * **Request ids, not answers.** A proposal is durable server-side and readable by its request id, so the ids are
 * what is kept and the content is read back on resume — the same reasoning that keeps a seed's token rather than
 * the seed. The two exceptions are the creator's **pick** and their **final prompt**: a pick is a decision, and
 * the final prompt is the creator's own words, which nothing else holds.
 */
export interface ContentPipelinePromptState {
  /** The IMG-001 request last asked for, so its concepts come back after a refresh. */
  readonly conceptRequestId: string | null;
  /**
   * The brief that request was sent with, so a look planned from an older brief can be said to be one.
   *
   * Null where that is not known — a request made before this was kept — which reads as "not stale": the
   * alternative is telling a creator their looks are out of date on a guess.
   */
  readonly plannedBrief: string | null;
  /**
   * The recipe that request named, as {@link linkedRecipeKey} writes it — empty for none — so looks planned
   * around a recipe that has since been changed or unlinked can be said to be. Null where that is not known.
   */
  readonly plannedRecipe: string | null;
  /**
   * The dish name that request was sent with, or null for none — so looks planned under a name the creator has
   * since changed can be said to be.
   *
   * Separate from {@link plannedRecipe} rather than folded into it, because the two are different answers to
   * what the picture is of and a creator reads a different sentence about each.
   */
  readonly plannedSubject: string | null;
  readonly chosen: ContentPipelineChosenShot | null;
  /** A brief to compose from. Optional; its text is untrusted. */
  readonly brief: ContentPipelineDocumentRef | null;
  /** A reference photograph to read. Optional. */
  readonly reference: ContentPipelineDocumentRef | null;
  /** The IMG-004 request last asked for that reference. */
  readonly referenceRequestId: string | null;
  /** The IMG-002 request last asked for. */
  readonly promptRequestId: string | null;
  /**
   * What the model composed, as it composed it.
   *
   * Kept — unlike a seed — because the creator edits a copy of it, and "what did it say before I changed it?" is
   * a question they can only be answered if the original is here.
   */
  readonly generated: ContentPipelineGeneratedPrompt | null;
  /**
   * The prompt that counts. The creator's own words from the moment they touch it.
   *
   * **Nothing overwrites this without asking.** A recomposition, or a reference reading offered as a starting
   * point, replaces it only on an explicit confirmation (.claude/rules/recipes.md, EASE-005).
   */
  readonly finalPrompt: string;
  /** Whose words {@link finalPrompt} is, which decides both how it is labelled and whether a replace asks. */
  readonly promptSource: ContentPipelinePromptSource;
}

/**
 * The images step's state (PIPE-UI-004).
 *
 * **An id, and nothing else.** No bytes, media type, size, dimensions, provider, model or retention deadline:
 * every one of those is read back from the operation, which is the same rule the prompt step follows. Storing
 * a copy would make this a second account of what the server holds, free to disagree with it.
 *
 * **The keepers are not here either, since AF.4.3.** A marked picture is a decision about the work, so it
 * belongs to the work: it is a `Keeper` reference on the creative context, where the library step and any
 * other device can read it. What was declined is each picture's own status, and the one place that lives is
 * the server.
 */
export interface ContentPipelineImagesState {
  /** The IMG-003 run last asked for, so its pictures come back after a refresh. */
  readonly operationId: string | null;
}

/** One workspace's unfinished pipeline run, as it is held between visits. */
export interface ContentPipelineDraft {
  readonly config: ContentPipelineConfig;
  readonly seed: ContentPipelineSeedState;
  readonly prompt: ContentPipelinePromptState;
  readonly images: ContentPipelineImagesState;
  /** The furthest step reached, which is as far as the step list will let a creator jump. */
  readonly furthestStep: ContentPipelineStepSlug;
  /** When this draft was last written, as an ISO instant. */
  readonly savedAt: string;
}

export function emptyContentPipelineConfig(): ContentPipelineConfig {
  return {
    subject: '',
    channelKey: null,
    day: null,
    variantCount: DEFAULT_VARIANT_COUNT,
    scene: [],
    style: [],
    concept: '',
    briefSource: null,
    brief: '',
  };
}

export function emptyContentPipelinePromptState(): ContentPipelinePromptState {
  return {
    conceptRequestId: null,
    plannedBrief: null,
    plannedRecipe: null,
    plannedSubject: null,
    chosen: null,
    brief: null,
    reference: null,
    referenceRequestId: null,
    promptRequestId: null,
    generated: null,
    finalPrompt: '',
    promptSource: 'none',
  };
}

export function emptyContentPipelineImagesState(): ContentPipelineImagesState {
  return { operationId: null };
}

export function emptyContentPipelineDraft(now: Date = new Date()): ContentPipelineDraft {
  return {
    config: emptyContentPipelineConfig(),
    seed: { lastToken: null, keep: {}, accepted: null },
    prompt: emptyContentPipelinePromptState(),
    images: emptyContentPipelineImagesState(),
    furthestStep: FIRST_CONTENT_PIPELINE_STEP,
    savedAt: now.toISOString(),
  };
}

/** True when nothing on the draft has been filled in, so there is nothing to resume. */
export function isContentPipelineDraftEmpty(draft: ContentPipelineDraft): boolean {
  const { config, seed, prompt, images } = draft;

  return (
    config.subject.trim() === '' &&
    config.channelKey === null &&
    config.day === null &&
    config.variantCount === DEFAULT_VARIANT_COUNT &&
    config.scene.length === 0 &&
    config.style.length === 0 &&
    config.concept.trim() === '' &&
    config.brief.trim() === '' &&
    seed.lastToken === null &&
    seed.accepted === null &&
    Object.keys(seed.keep).length === 0 &&
    prompt.conceptRequestId === null &&
    prompt.chosen === null &&
    prompt.brief === null &&
    prompt.reference === null &&
    prompt.promptRequestId === null &&
    prompt.finalPrompt.trim() === '' &&
    images.operationId === null
  );
}

export function clampVariantCount(value: unknown): number {
  const count = typeof value === 'number' && Number.isFinite(value) ? Math.round(value) : DEFAULT_VARIANT_COUNT;
  if (count < CONTENT_PIPELINE_LIMITS.minVariants) return CONTENT_PIPELINE_LIMITS.minVariants;
  if (count > CONTENT_PIPELINE_LIMITS.maxVariants) return CONTENT_PIPELINE_LIMITS.maxVariants;

  return count;
}

/**
 * One override list, ready to submit: trimmed, blanks dropped, each entry cut to length and the list cut to
 * count.
 *
 * Cut rather than refused, because that is what the creator's own editing already did — a blank row is a row
 * they emptied, not an error to report back at them. The length cut is the only lossy part, and the control
 * stops them well before it with a `maxlength`.
 */
export function cleanOverrides(values: readonly string[]): readonly string[] {
  return values
    .map((value) => value.trim().slice(0, CONTENT_PIPELINE_LIMITS.overrideMaxLength))
    .filter((value) => value.length > 0)
    .slice(0, CONTENT_PIPELINE_LIMITS.maxOverrides);
}

/**
 * What the next seed request asks for.
 *
 * Channel and day come from `setup` and the other five from the keep map, which is the one place each of those
 * values lives. `token` is the caller's: pass one to reproduce a seed, or leave it out for a new one. `recipe`
 * is the one linked to the run, read from its creative context: the idea is built around it.
 */
export function contentSeedQueryFor(
  draft: ContentPipelineDraft,
  token?: string | null,
  recipe: LinkedRecipe | null = null,
): ContentSeedQuery {
  return {
    recipeId: recipe?.recipeId ?? null,
    recipeVersionId: recipe?.recipeVersionId ?? null,
    subject: contentSubjectOf(draft.config, recipe),
    token: token ?? null,
    channel: draft.config.channelKey,
    day: draft.config.day,
    cuisine: draft.seed.keep.cuisine ?? null,
    dishType: draft.seed.keep.dishType ?? null,
    method: draft.seed.keep.method ?? null,
    photographyStyle: draft.seed.keep.photographyStyle ?? null,
    occasion: draft.seed.keep.occasion ?? null,
  };
}

/**
 * The version of the stored shape.
 *
 * A stored draft from another version is **discarded, never migrated**. It holds minutes of a creator's
 * filling-in, not work they would lose anything irreplaceable by redoing, and a half-understood migration that
 * silently changed what their idea said would be the worse failure. Raise this on any shape change.
 *
 * Raised to 2 by 12.10a, which added the prompt step's state, to 3 when a plain "edited" flag became
 * {@link ContentPipelinePromptSource}, and to 4 by 12.10b for {@link ContentPipelineImagesState}. Every discard
 * so far has cost nothing real: the feature is unreleased, so the only earlier drafts are on a developer's own
 * machine.
 *
 * **Version 4 is the last shape that holds a whole run**, and the one exception to "never migrated": since
 * AF.3.1 a run lives on a creative context, so a v4 draft found on a device is work not yet filed on one. It is
 * filed once and removed — see {@link CONTENT_PIPELINE_RUN_VERSION} for what is kept after that.
 */
export const CONTENT_PIPELINE_DRAFT_VERSION = 4;

function decodeStringList(value: unknown): readonly string[] | null {
  if (value === undefined) return [];
  if (!Array.isArray(value) || value.some((item) => typeof item !== 'string')) return null;

  return cleanOverrides(value as string[]);
}

function decodeKeep(value: unknown): ContentPipelineKeep | null {
  if (value === undefined || value === null) return {};
  if (!isRecord(value)) return null;

  const keep: { -readonly [K in ContentSeedKeepName]?: string } = {};
  for (const name of CONTENT_SEED_KEEP_NAMES) {
    const key = value[name];
    if (key === undefined || key === null) continue;
    if (typeof key !== 'string') return null;

    const trimmed = key.trim();
    // A key longer than any catalogue holds cannot name an entry, so keeping it would only earn a refusal on
    // the next generate. Dropped rather than stored.
    if (trimmed.length > 0 && trimmed.length <= 64) keep[name] = trimmed;
  }

  return keep;
}

export function decodeContentPipelineConfig(value: unknown): ContentPipelineConfig | null {
  if (!isRecord(value)) return null;

  const { channelKey, concept } = value;
  const subject = value['subject'];
  const scene = decodeStringList(value['scene']);
  const style = decodeStringList(value['style']);

  if (channelKey !== null && channelKey !== undefined && typeof channelKey !== 'string') return null;
  if (concept !== undefined && typeof concept !== 'string') return null;
  if (subject !== undefined && typeof subject !== 'string') return null;
  if (scene === null || style === null) return null;

  const rawBrief = value['brief'];
  const rawSource = value['briefSource'];
  if (rawBrief !== undefined && typeof rawBrief !== 'string') return null;
  // An unrecognised source is dropped with its brief kept: the words are the creator's, and the choice is one
  // answer they can give again.
  const briefSource =
    typeof rawSource === 'string' && BRIEF_SOURCE_VALUES.has(rawSource) ? (rawSource as ContentPipelineBriefSource) : null;

  // An unrecognised day is dropped rather than failing the whole draft: the day is one answer among several, and
  // losing the rest of a creator's setup over it would cost more than re-picking it.
  const rawDay = value['day'];
  const day = rawDay === null || rawDay === undefined ? null : decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, rawDay);

  return {
    subject: typeof subject === 'string' ? subject.slice(0, CONTENT_PIPELINE_LIMITS.subjectMaxLength) : '',
    channelKey: typeof channelKey === 'string' && channelKey.trim() !== '' ? channelKey.trim() : null,
    day,
    variantCount: clampVariantCount(value['variantCount']),
    scene,
    style,
    concept: typeof concept === 'string' ? concept.slice(0, CONTENT_PIPELINE_LIMITS.conceptMaxLength) : '',
    briefSource,
    brief: typeof rawBrief === 'string' ? rawBrief : '',
  };
}

function decodeDocumentRef(value: unknown): { readonly ref: ContentPipelineDocumentRef | null } | null {
  if (value === null || value === undefined) return { ref: null };
  if (!isRecord(value)) return null;

  const { documentId, title } = value;
  if (typeof documentId !== 'string' || documentId === '' || typeof title !== 'string') return null;

  return { ref: { documentId, title } };
}

function decodeChosenShot(value: unknown): { readonly chosen: ContentPipelineChosenShot | null } | null {
  if (value === null || value === undefined) return { chosen: null };
  if (!isRecord(value)) return null;

  const { conceptRequestId, conceptId, label } = value;
  const shotKind = decodePhotographyShotKind(value['shotKind']);

  if (
    typeof conceptRequestId !== 'string' ||
    conceptRequestId === '' ||
    typeof conceptId !== 'string' ||
    conceptId === '' ||
    typeof label !== 'string' ||
    shotKind === null
  ) {
    return null;
  }

  return { chosen: { conceptRequestId, conceptId, label, shotKind } };
}

function decodeGeneratedPrompt(value: unknown): { readonly generated: ContentPipelineGeneratedPrompt | null } | null {
  if (value === null || value === undefined) return { generated: null };
  if (!isRecord(value)) return null;

  const text = value['text'];
  const avoid = value['avoid'];
  if (typeof text !== 'string') return null;
  if (avoid !== undefined && (!Array.isArray(avoid) || avoid.some((item) => typeof item !== 'string'))) return null;

  return { generated: { text, avoid: Array.isArray(avoid) ? (avoid as string[]) : [] } };
}

function optionalId(value: unknown): { readonly id: string | null } | null {
  if (value === null || value === undefined) return { id: null };
  if (typeof value !== 'string') return null;

  return { id: value === '' ? null : value };
}

/**
 * The prompt step's stored state, or null when it is a shape this build cannot read.
 *
 * Absent is **not** a failure: a draft written before this step existed has no block, and defaulting it costs
 * the creator nothing, because everything in it is re-askable. A block that is *present and malformed* is a
 * failure, because the alternative is resuming a run whose pick or final prompt silently became something else.
 */
export function decodeContentPipelinePromptState(value: unknown): ContentPipelinePromptState | null {
  if (value === undefined || value === null) return emptyContentPipelinePromptState();
  if (!isRecord(value)) return null;

  const chosen = decodeChosenShot(value['chosen']);
  const brief = decodeDocumentRef(value['brief']);
  const reference = decodeDocumentRef(value['reference']);
  const generated = decodeGeneratedPrompt(value['generated']);
  const conceptRequestId = optionalId(value['conceptRequestId']);
  const referenceRequestId = optionalId(value['referenceRequestId']);
  const promptRequestId = optionalId(value['promptRequestId']);

  const finalPrompt = value['finalPrompt'];
  const rawSource = value['promptSource'];
  const plannedBrief = value['plannedBrief'];
  const plannedRecipe = value['plannedRecipe'];
  const plannedSubject = value['plannedSubject'];

  if (
    chosen === null ||
    brief === null ||
    reference === null ||
    generated === null ||
    conceptRequestId === null ||
    referenceRequestId === null ||
    promptRequestId === null ||
    (plannedBrief !== undefined && plannedBrief !== null && typeof plannedBrief !== 'string') ||
    (plannedRecipe !== undefined && plannedRecipe !== null && typeof plannedRecipe !== 'string') ||
    (plannedSubject !== undefined && plannedSubject !== null && typeof plannedSubject !== 'string') ||
    (finalPrompt !== undefined && typeof finalPrompt !== 'string') ||
    (rawSource !== undefined && typeof rawSource !== 'string')
  ) {
    return null;
  }

  const text = typeof finalPrompt === 'string' ? finalPrompt : '';
  // An unrecognised source on a prompt that has text reads as the creator's. Guessing the other way would let a
  // recomposition replace words a person wrote without asking, which is the one outcome worth defending against.
  const promptSource: ContentPipelinePromptSource =
    typeof rawSource === 'string' && PROMPT_SOURCE_VALUES.has(rawSource)
      ? (rawSource as ContentPipelinePromptSource)
      : text.trim() === ''
        ? 'none'
        : 'creator';

  return {
    conceptRequestId: conceptRequestId.id,
    // A planned brief without its request describes a plan nobody made, so it goes with it.
    plannedBrief: conceptRequestId.id !== null && typeof plannedBrief === 'string' ? plannedBrief : null,
    plannedRecipe: conceptRequestId.id !== null && typeof plannedRecipe === 'string' ? plannedRecipe : null,
    plannedSubject:
      conceptRequestId.id !== null && typeof plannedSubject === 'string' && plannedSubject !== ''
        ? plannedSubject
        : null,
    chosen: chosen.chosen,
    brief: brief.ref,
    // A reference request without its reference names a reading of nothing, so it goes with it.
    reference: reference.ref,
    referenceRequestId: reference.ref === null ? null : referenceRequestId.id,
    promptRequestId: promptRequestId.id,
    generated: generated.generated,
    finalPrompt: text,
    promptSource,
  };
}

/**
 * The images step's stored state, or null for a shape this build cannot read.
 *
 * Absent is **not** a failure, for `decodeContentPipelinePromptState`'s reason: a draft written before this step existed has
 * no block, and everything in it is re-askable. Present and malformed is.
 *
 * A `keepers` array from an older shape is read and dropped. Since AF.4.3 a keeper is a reference on the
 * work's creative context, and a stored run from before that cannot be migrated onto one here — this decoder
 * has no workspace, no session and no way to write. {@link CONTENT_PIPELINE_RUN_VERSION} is what actually
 * keeps such a record out: by the time this is reached, the version has already matched.
 */
export function decodeContentPipelineImagesState(value: unknown): ContentPipelineImagesState | null {
  if (value === undefined || value === null) return emptyContentPipelineImagesState();
  if (!isRecord(value)) return null;

  const operationId = optionalId(value['operationId']);
  if (operationId === null) return null;

  return { operationId: operationId.id };
}

// `keepersStillPresent` was here until AF.4.3, and is gone with the marks it pruned. A keeper is a reference
// on the work now, and a reference points at something that may have stopped being usable rather than at
// nothing — so the library step says a picture has gone instead of quietly forgetting the creator chose it.

/**
 * One stored draft, or null when there is nothing usable to resume.
 *
 * Null covers every way this can go wrong — absent, not JSON, another version, too large, a shape this build
 * does not recognise — because the caller does the same thing in all of them: start clean and say so. Nothing
 * here throws, since a browser's stored data is not under this product's control and a screen that cannot open
 * is worse than a draft that was lost.
 */
export function decodeContentPipelineDraft(raw: string | null | undefined): ContentPipelineDraft | null {
  const parsed = parseStored(raw, CONTENT_PIPELINE_DRAFT_VERSION);

  return parsed === null ? null : decodeDraftBody(parsed);
}

/** Stored JSON of one version, as a record, or null for anything else. */
function parseStored(raw: string | null | undefined, version: number): Record<string, unknown> | null {
  if (typeof raw !== 'string' || raw.length === 0) return null;
  if (raw.length > CONTENT_PIPELINE_LIMITS.storedMaxChars) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  return isRecord(parsed) && parsed['v'] === version ? parsed : null;
}

function decodeDraftBody(parsed: Record<string, unknown>): ContentPipelineDraft | null {
  const config = decodeContentPipelineConfig(parsed['config']);
  const seedRaw = parsed['seed'];
  if (config === null || !isRecord(seedRaw)) return null;

  const keep = decodeKeep(seedRaw['keep']);
  if (keep === null) return null;

  const rawToken = seedRaw['lastToken'];
  const lastToken = typeof rawToken === 'string' && isContentSeedToken(rawToken) ? rawToken : null;

  const rawAccepted = seedRaw['accepted'];
  const accepted = rawAccepted === null || rawAccepted === undefined ? null : decodeContentSeed(rawAccepted);
  // An accepted idea that cannot be read is the one thing not quietly dropped: the steps after this depend on
  // it, so a draft claiming one it cannot produce is discarded whole rather than resumed without it.
  if (rawAccepted !== null && rawAccepted !== undefined && accepted === null) return null;

  const prompt = decodeContentPipelinePromptState(parsed['prompt']);
  if (prompt === null) return null;

  const images = decodeContentPipelineImagesState(parsed['images']);
  if (images === null) return null;

  const furthest = parsed['furthestStep'];
  const savedAt = parsed['savedAt'];

  return {
    config,
    seed: { lastToken, keep, accepted },
    prompt,
    images,
    furthestStep: isContentPipelineStepSlug(furthest) ? furthest : FIRST_CONTENT_PIPELINE_STEP,
    savedAt: typeof savedAt === 'string' ? savedAt : '',
  };
}

export function encodeContentPipelineDraft(draft: ContentPipelineDraft): string {
  return JSON.stringify({ v: CONTENT_PIPELINE_DRAFT_VERSION, ...draft });
}

/**
 * The version of what this device keeps for a run that is on a creative context (AF.3.1).
 *
 * The same blocks as a v4 draft with the channel, the day, the picture and the chosen brief left out: those live on the context,
 * and a second copy here would be free to disagree with it. Beside them, {@link ContentPipelineKeptRun.unsent}.
 *
 * Raised to 6 by AF.4.3, which moved the keepers onto the context. A v5 record is discarded rather than
 * migrated, as every earlier one has been: its marks cannot be written to a context from a decoder, and
 * resuming without them would quietly show a run with nothing kept as one the creator had kept nothing in.
 *
 * **Not raised for {@link ContentPipelinePromptState.plannedSubject}**, and that is a decision rather than an
 * oversight. The rule above is there so a shape cannot be half-read; this field's absence has a defined
 * meaning — "not known", which reads as not stale, exactly as `plannedBrief` and `plannedRecipe` do — so a v6
 * record written before it existed decodes to the right answer rather than a guessed one. Discarding real
 * in-progress runs would have cost something and bought nothing.
 */
export const CONTENT_PIPELINE_RUN_VERSION = 6;

/** What this device keeps for one run on one creative context. */
export interface ContentPipelineKeptRun {
  /** The run, with the channel, day and picture empty: the context supplies them when it is read. */
  readonly draft: ContentPipelineDraft;
  /**
   * Edits to the context's own fields that have not reached the server, or null.
   *
   * The one place a context field is ever on the device, and only until it is sent: without it, a creator who
   * typed while offline and then closed the tab would lose exactly those words.
   */
  readonly unsent: Partial<CreativeContextFields> | null;
}

/** The config with the answers a creative context holds laid over it. */
export function contentPipelineConfigWith(
  config: ContentPipelineConfig,
  fields: Pick<
    CreativeContextFields,
    'workingTitle' | 'channelKey' | 'day' | 'pictureBrief' | 'briefSource' | 'workingBrief'
  >,
): ContentPipelineConfig {
  return {
    ...config,
    subject: fields.workingTitle,
    channelKey: fields.channelKey,
    day: fields.day,
    concept: fields.pictureBrief,
    briefSource: fields.briefSource,
    brief: fields.workingBrief,
  };
}

/**
 * The unsent block of a kept record: null for none, `undefined` for one that is there and cannot be read.
 *
 * Unreadable is a reason to discard the record rather than to drop the block quietly — resuming without it
 * would show the server's older answer as though it were the creator's latest.
 */
export function decodeUnsentFields(raw: unknown): Partial<CreativeContextFields> | null | undefined {
  if (raw === null || raw === undefined) return null;

  const part = decodeCreativeContextFieldsPart(raw);
  if (part === null) return undefined;

  return Object.keys(part).length > 0 ? part : null;
}

export function decodeContentPipelineRun(raw: string | null | undefined): ContentPipelineKeptRun | null {
  const parsed = parseStored(raw, CONTENT_PIPELINE_RUN_VERSION);
  if (parsed === null) return null;

  const draft = decodeDraftBody(parsed);
  const unsent = decodeUnsentFields(parsed['unsent']);
  if (draft === null || unsent === undefined) return null;

  return {
    draft: { ...draft, config: contentPipelineConfigWith(draft.config, EMPTY_CREATIVE_CONTEXT_FIELDS) },
    unsent,
  };
}

export function encodeContentPipelineRun(
  draft: ContentPipelineDraft,
  unsent: Partial<CreativeContextFields> | null,
): string {
  const { variantCount, scene, style } = draft.config;

  return JSON.stringify({ v: CONTENT_PIPELINE_RUN_VERSION, ...draft, config: { variantCount, scene, style }, unsent });
}
