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
    help: 'A starting point put together from the cooking vocabulary and your own weekly theme. Keep the parts you like, swap the rest, and pick one when it looks right. Nothing is written down until you do.',
    legend: 'Keep what you like and try again for the rest. Nothing is saved until you pick one.',
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
    help: 'Filing what you kept, so you can find it again and see where it came from.',
    legend: '',
    comingSoon: 'Saving to your library is coming soon.',
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
 * **An id and a decision, and nothing else.** No bytes, media type, size, dimensions, provider, model or
 * retention deadline: every one of those is read back from the operation, which is the same rule the prompt
 * step follows. Storing a copy would make this a second account of what the server holds, free to disagree
 * with it.
 *
 * **What was declined is not here either.** Each picture's own status is the truth about that, and the one
 * place it lives is the server.
 */
export interface ContentPipelineImagesState {
  /** The IMG-003 run last asked for, so its pictures come back after a refresh. */
  readonly operationId: string | null;
  /**
   * The pictures the creator marked to carry forward.
   *
   * Stored whole — unlike a suggestion — because from here on it is a decision. These stay `Staged`
   * server-side: nothing on this step files anything in a library, so there is no `Kept` to read back.
   */
  readonly keepers: readonly string[];
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
    channelKey: null,
    day: null,
    variantCount: DEFAULT_VARIANT_COUNT,
    scene: [],
    style: [],
    concept: '',
  };
}

export function emptyContentPipelinePromptState(): ContentPipelinePromptState {
  return {
    conceptRequestId: null,
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
  return { operationId: null, keepers: [] };
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
    config.channelKey === null &&
    config.day === null &&
    config.variantCount === DEFAULT_VARIANT_COUNT &&
    config.scene.length === 0 &&
    config.style.length === 0 &&
    config.concept.trim() === '' &&
    seed.lastToken === null &&
    seed.accepted === null &&
    Object.keys(seed.keep).length === 0 &&
    prompt.conceptRequestId === null &&
    prompt.chosen === null &&
    prompt.brief === null &&
    prompt.reference === null &&
    prompt.promptRequestId === null &&
    prompt.finalPrompt.trim() === '' &&
    images.operationId === null &&
    images.keepers.length === 0
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
 * values lives. `token` is the caller's: pass one to reproduce a seed, or leave it out for a new one.
 */
export function contentSeedQueryFor(draft: ContentPipelineDraft, token?: string | null): ContentSeedQuery {
  return {
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

function decodeConfig(value: unknown): ContentPipelineConfig | null {
  if (!isRecord(value)) return null;

  const { channelKey, concept } = value;
  const scene = decodeStringList(value['scene']);
  const style = decodeStringList(value['style']);

  if (channelKey !== null && channelKey !== undefined && typeof channelKey !== 'string') return null;
  if (concept !== undefined && typeof concept !== 'string') return null;
  if (scene === null || style === null) return null;

  // An unrecognised day is dropped rather than failing the whole draft: the day is one answer among several, and
  // losing the rest of a creator's setup over it would cost more than re-picking it.
  const rawDay = value['day'];
  const day = rawDay === null || rawDay === undefined ? null : decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, rawDay);

  return {
    channelKey: typeof channelKey === 'string' && channelKey.trim() !== '' ? channelKey.trim() : null,
    day,
    variantCount: clampVariantCount(value['variantCount']),
    scene,
    style,
    concept: typeof concept === 'string' ? concept.slice(0, CONTENT_PIPELINE_LIMITS.conceptMaxLength) : '',
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
function decodePromptState(value: unknown): ContentPipelinePromptState | null {
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

  if (
    chosen === null ||
    brief === null ||
    reference === null ||
    generated === null ||
    conceptRequestId === null ||
    referenceRequestId === null ||
    promptRequestId === null ||
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
 * Absent is **not** a failure, for `decodePromptState`'s reason: a draft written before this step existed has
 * no block, and everything in it is re-askable. Present and malformed is, because the alternative is resuming
 * a run whose keepers silently became something else.
 */
function decodeImagesState(value: unknown): ContentPipelineImagesState | null {
  if (value === undefined || value === null) return emptyContentPipelineImagesState();
  if (!isRecord(value)) return null;

  const operationId = optionalId(value['operationId']);
  if (operationId === null) return null;

  const rawKeepers = value['keepers'];
  if (rawKeepers !== undefined && rawKeepers !== null && !Array.isArray(rawKeepers)) return null;

  const keepers = Array.isArray(rawKeepers)
    ? Array.from(
        new Set(
          (rawKeepers as unknown[]).filter((entry): entry is string => typeof entry === 'string' && entry !== ''),
        ),
      ).slice(0, CONTENT_PIPELINE_LIMITS.maxVariants)
    : [];

  return {
    // Keepers without a run name pictures nothing can find, so they go with it.
    operationId: operationId.id,
    keepers: operationId.id === null ? [] : keepers,
  };
}

/**
 * The keepers a run can still account for.
 *
 * A marked picture that the run no longer lists, or lists as declined or expired, is dropped: carrying it would
 * gate the step's Continue on a picture that is not there any more. Retention collects staged bytes on its own
 * schedule, so this is an ordinary outcome of coming back a day later rather than an error.
 */
export function keepersStillPresent(
  keepers: readonly string[],
  images: readonly { readonly id: string; readonly status: string }[],
): readonly string[] {
  const live = new Set(images.filter((image) => image.status === 'Staged').map((image) => image.id));

  return keepers.filter((id) => live.has(id));
}

/**
 * One stored draft, or null when there is nothing usable to resume.
 *
 * Null covers every way this can go wrong — absent, not JSON, another version, too large, a shape this build
 * does not recognise — because the caller does the same thing in all of them: start clean and say so. Nothing
 * here throws, since a browser's stored data is not under this product's control and a screen that cannot open
 * is worse than a draft that was lost.
 */
export function decodeContentPipelineDraft(raw: string | null | undefined): ContentPipelineDraft | null {
  if (typeof raw !== 'string' || raw.length === 0) return null;
  if (raw.length > CONTENT_PIPELINE_LIMITS.storedMaxChars) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (!isRecord(parsed) || parsed['v'] !== CONTENT_PIPELINE_DRAFT_VERSION) return null;

  const config = decodeConfig(parsed['config']);
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

  const prompt = decodePromptState(parsed['prompt']);
  if (prompt === null) return null;

  const images = decodeImagesState(parsed['images']);
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
