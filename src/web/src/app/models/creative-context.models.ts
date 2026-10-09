// The creative context's API shapes (AF.1.3, AF.1.4): what one piece of creative work is about — the creator's
// words for it, the channels and day it is for, and the sources it draws on.
//
// Mirrors `CreativeContextServiceModel`, `CreativeContextReferenceServiceModel` and
// `CreativeContextSummaryServiceModel`. Nothing here names a workspace or a member, because the routes publish
// neither — and each decoder builds its result field by field, so an unknown field cannot ride through to a
// screen.

import { decodeEnum, isNumberOrNull, isRecord, isStringOrNull } from './recipe.models';

/**
 * `CreativeContextReferenceKind`, as the API publishes it. `SocialPackage` is in the server's enum and is
 * refused on the way in until post packages exist, so nothing here can build one.
 */
export type CreativeContextReferenceKind = 'Recipe' | 'RecipeConcept' | 'DamAsset' | 'GeneratedImage' | 'PromptRecord';

/** `System.DayOfWeek`, by name. */
export type CreativeContextDay = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday';

/**
 * `CreativeContextBriefSource`: what the creator chose to work from when a picture is planned (AF.3.2) — their
 * own description, the idea they picked, or the description with the idea's wording below it.
 */
export type CreativeContextBriefSource = 'Description' | 'Idea' | 'Combined';

const BRIEF_SOURCES: ReadonlySet<string> = new Set<CreativeContextBriefSource>(['Description', 'Idea', 'Combined']);

const KINDS: ReadonlySet<string> = new Set<CreativeContextReferenceKind>([
  'Recipe',
  'RecipeConcept',
  'DamAsset',
  'GeneratedImage',
  'PromptRecord',
]);

const DAYS: ReadonlySet<string> = new Set<CreativeContextDay>([
  'Sunday',
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
]);

/**
 * One source to hand to a creative context: a kind, and that kind's ids and no others.
 *
 * A union rather than one interface with every id optional, because the server refuses a reference that
 * carries another kind's field — so the shape that cannot be sent is the shape that cannot be built.
 */
export type CreativeContextSource =
  | { readonly kind: 'Recipe'; readonly recipeId: string; readonly recipeVersionId?: string | null }
  | { readonly kind: 'RecipeConcept'; readonly conceptRequestId: string; readonly conceptId: string }
  | { readonly kind: 'DamAsset'; readonly mediaAssetId: string; readonly mediaAssetVersionNumber?: number | null }
  | { readonly kind: 'GeneratedImage'; readonly generatedImageId: string }
  | { readonly kind: 'PromptRecord'; readonly promptRecordId: string };

/**
 * One source a context names. Ids only: a context points at its sources and never copies them, so there is no
 * title, preview or "still available" here to show — resolve the id through its own route for that.
 */
export interface CreativeContextReference {
  readonly id: string;
  readonly kind: CreativeContextReferenceKind;
  readonly sortOrder: number;
  readonly recipeId: string | null;
  readonly recipeVersionId: string | null;
  readonly conceptRequestId: string | null;
  readonly conceptId: string | null;
  readonly mediaAssetId: string | null;
  readonly mediaAssetVersionNumber: number | null;
  readonly generatedImageId: string | null;
  readonly promptRecordId: string | null;
  readonly addedAt: string;
}

/**
 * The recipe a piece of work is about, as an AI request names it (AF.3.3): the recipe, and the version that was
 * current when the creator linked it. A null version means the recipe had none yet, and reads as "the current
 * one" to every route that takes it.
 */
export interface LinkedRecipe {
  readonly recipeId: string;
  readonly recipeVersionId: string | null;
}

/**
 * The context's recipe reference, or null when it names none.
 *
 * The first, where a context names several: the working surfaces link one recipe to a run, and any others a
 * hand-off added are left as they are.
 */
export function recipeReferenceOf(context: CreativeContext | null): CreativeContextReference | null {
  return context?.references.find((reference) => reference.kind === 'Recipe' && reference.recipeId !== null) ?? null;
}

export function linkedRecipeOf(context: CreativeContext | null): LinkedRecipe | null {
  const reference = recipeReferenceOf(context);

  return reference === null || reference.recipeId === null
    ? null
    : { recipeId: reference.recipeId, recipeVersionId: reference.recipeVersionId };
}

/**
 * The context's reference picture, or null when it names none (AF.3.5).
 *
 * The first library asset or generated image it names. A context's references carry no role, so "the picture
 * this work takes its cues from" is a convention: the working surfaces keep one, and replace it rather than
 * add a second. A picture a hand-off put on the context is therefore read as this one — which is what handing
 * a picture to Image Studio means.
 */
export function pictureReferenceOf(context: CreativeContext | null): CreativeContextReference | null {
  return (
    context?.references.find(
      (reference) =>
        (reference.kind === 'DamAsset' && reference.mediaAssetId !== null) ||
        (reference.kind === 'GeneratedImage' && reference.generatedImageId !== null),
    ) ?? null
  );
}

/** A creative context in full. */
export interface CreativeContext {
  readonly id: string;
  readonly workingTitle: string | null;
  readonly pictureBrief: string | null;
  /** What the creator chose to work from, or null until they have chosen. */
  readonly briefSource: CreativeContextBriefSource | null;
  /**
   * The brief they chose, as they last left it. Never the same field as {@link pictureBrief}: that is what they
   * wrote before any idea was suggested, and no choice changes it.
   */
  readonly workingBrief: string | null;
  readonly channelKeys: readonly string[];
  readonly day: CreativeContextDay | null;
  readonly weeklyThemeKey: string | null;
  readonly references: readonly CreativeContextReference[];
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly archivedAt: string | null;
  /** Opaque. Send it back with the next edit; never inspect it. */
  readonly concurrencyToken: string;
}

/** One row of the recent list: enough to find a piece, and deliberately not the picture description. */
export interface CreativeContextSummary {
  readonly id: string;
  readonly workingTitle: string | null;
  readonly channelKeys: readonly string[];
  readonly day: CreativeContextDay | null;
  readonly weeklyThemeKey: string | null;
  readonly referenceCount: number;
  readonly updatedAt: string;
}

export interface CreativeContextPage {
  readonly items: readonly CreativeContextSummary[];
  /** Null on the last page. Follow it until it is. */
  readonly nextCursor: string | null;
}

/** What a new context starts with. Everything is optional: a context may begin as nothing but its source. */
export interface CreativeContextDraft {
  readonly workingTitle?: string | null;
  readonly pictureBrief?: string | null;
  readonly channelKeys?: readonly string[];
  readonly day?: CreativeContextDay | null;
  readonly weeklyThemeKey?: string | null;
  readonly from?: CreativeContextSource;
}

/**
 * A partial edit. A property left out is untouched; a property set to `null` is cleared — which is why none of
 * these may be normalised to `null` on the way out.
 */
export interface CreativeContextPatch {
  readonly workingTitle?: string | null;
  readonly pictureBrief?: string | null;
  readonly briefSource?: CreativeContextBriefSource | null;
  readonly workingBrief?: string | null;
  /** Replaces the ordered set. */
  readonly channelKeys?: readonly string[] | null;
  readonly day?: CreativeContextDay | null;
  readonly weeklyThemeKey?: string | null;
  readonly archived?: boolean;
}

/**
 * The request body for one source, with the optional pins left out when they were not given rather than sent as
 * `null`.
 */
export function encodeCreativeContextSource(source: CreativeContextSource): Record<string, unknown> {
  switch (source.kind) {
    case 'Recipe':
      return {
        kind: source.kind,
        recipeId: source.recipeId,
        ...(source.recipeVersionId ? { recipeVersionId: source.recipeVersionId } : {}),
      };
    case 'RecipeConcept':
      return { kind: source.kind, conceptRequestId: source.conceptRequestId, conceptId: source.conceptId };
    case 'DamAsset':
      return {
        kind: source.kind,
        mediaAssetId: source.mediaAssetId,
        ...(source.mediaAssetVersionNumber ? { mediaAssetVersionNumber: source.mediaAssetVersionNumber } : {}),
      };
    case 'GeneratedImage':
      return { kind: source.kind, generatedImageId: source.generatedImageId };
    case 'PromptRecord':
      return { kind: source.kind, promptRecordId: source.promptRecordId };
  }
}

export function encodeCreativeContextDraft(draft: CreativeContextDraft): Record<string, unknown> {
  return {
    ...(draft.workingTitle != null ? { workingTitle: draft.workingTitle } : {}),
    ...(draft.pictureBrief != null ? { pictureBrief: draft.pictureBrief } : {}),
    ...(draft.channelKeys && draft.channelKeys.length > 0 ? { channelKeys: [...draft.channelKeys] } : {}),
    ...(draft.day != null ? { day: draft.day } : {}),
    ...(draft.weeklyThemeKey != null ? { weeklyThemeKey: draft.weeklyThemeKey } : {}),
    ...(draft.from ? { from: encodeCreativeContextSource(draft.from) } : {}),
  };
}

/**
 * The request body for an edit. Only the properties the caller set are sent, `null` included, because to the
 * server an absent field and a `null` one are different instructions.
 */
export function encodeCreativeContextPatch(
  patch: CreativeContextPatch,
  expectedConcurrencyToken: string,
): Record<string, unknown> {
  const body: Record<string, unknown> = { expectedConcurrencyToken };

  for (const key of [
    'workingTitle',
    'pictureBrief',
    'briefSource',
    'workingBrief',
    'channelKeys',
    'day',
    'weeklyThemeKey',
    'archived',
  ] as const) {
    if (key in patch && patch[key] !== undefined) body[key] = patch[key];
  }

  return body;
}

/**
 * One value that identifies a source, for telling two apart: the same source always gives the same string and
 * two different ones never do. Not sent anywhere.
 */
export function creativeContextSourceKey(source: CreativeContextSource): string {
  return JSON.stringify(encodeCreativeContextSource(source));
}

function decodeStrings(raw: unknown): readonly string[] | null {
  return Array.isArray(raw) && raw.every((item) => typeof item === 'string') ? [...(raw as string[])] : null;
}

function decodeDay(raw: unknown): CreativeContextDay | null | undefined {
  if (raw === null || raw === undefined) return null;

  // `undefined` rather than null for a day that is not one: null is a legitimate answer here.
  return decodeEnum<CreativeContextDay>(DAYS, raw) ?? undefined;
}

export function decodeCreativeContextReference(raw: unknown): CreativeContextReference | null {
  if (!isRecord(raw)) return null;

  const kind = decodeEnum<CreativeContextReferenceKind>(KINDS, raw['kind']);

  if (
    kind === null ||
    typeof raw['id'] !== 'string' ||
    typeof raw['sortOrder'] !== 'number' ||
    typeof raw['addedAt'] !== 'string' ||
    !isStringOrNull(raw['recipeId'] ?? null) ||
    !isStringOrNull(raw['recipeVersionId'] ?? null) ||
    !isStringOrNull(raw['conceptRequestId'] ?? null) ||
    !isStringOrNull(raw['conceptId'] ?? null) ||
    !isStringOrNull(raw['mediaAssetId'] ?? null) ||
    !isNumberOrNull(raw['mediaAssetVersionNumber'] ?? null) ||
    !isStringOrNull(raw['generatedImageId'] ?? null) ||
    !isStringOrNull(raw['promptRecordId'] ?? null)
  ) {
    return null;
  }

  return {
    id: raw['id'],
    kind,
    sortOrder: raw['sortOrder'],
    recipeId: (raw['recipeId'] as string | null | undefined) ?? null,
    recipeVersionId: (raw['recipeVersionId'] as string | null | undefined) ?? null,
    conceptRequestId: (raw['conceptRequestId'] as string | null | undefined) ?? null,
    conceptId: (raw['conceptId'] as string | null | undefined) ?? null,
    mediaAssetId: (raw['mediaAssetId'] as string | null | undefined) ?? null,
    mediaAssetVersionNumber: (raw['mediaAssetVersionNumber'] as number | null | undefined) ?? null,
    generatedImageId: (raw['generatedImageId'] as string | null | undefined) ?? null,
    promptRecordId: (raw['promptRecordId'] as string | null | undefined) ?? null,
    addedAt: raw['addedAt'],
  };
}

export function decodeCreativeContext(raw: unknown): CreativeContext | null {
  if (!isRecord(raw)) return null;

  const channelKeys = decodeStrings(raw['channelKeys']);
  const day = decodeDay(raw['day']);
  const rawReferences = raw['references'];
  const rawSource = raw['briefSource'] ?? null;
  const briefSource = rawSource === null ? null : decodeEnum<CreativeContextBriefSource>(BRIEF_SOURCES, rawSource);

  if (
    typeof raw['id'] !== 'string' ||
    typeof raw['createdAt'] !== 'string' ||
    typeof raw['updatedAt'] !== 'string' ||
    typeof raw['concurrencyToken'] !== 'string' ||
    raw['concurrencyToken'] === '' ||
    !isStringOrNull(raw['workingTitle'] ?? null) ||
    !isStringOrNull(raw['pictureBrief'] ?? null) ||
    !isStringOrNull(raw['workingBrief'] ?? null) ||
    // A source this build does not know fails the read: showing the brief under a guessed label would tell the
    // creator they chose something they did not.
    (rawSource !== null && briefSource === null) ||
    !isStringOrNull(raw['weeklyThemeKey'] ?? null) ||
    !isStringOrNull(raw['archivedAt'] ?? null) ||
    channelKeys === null ||
    day === undefined ||
    !Array.isArray(rawReferences)
  ) {
    return null;
  }

  const references: CreativeContextReference[] = [];

  for (const item of rawReferences) {
    const reference = decodeCreativeContextReference(item);

    // One unreadable reference fails the whole context. Dropping it would show a creator a piece of work that
    // quietly names fewer sources than it does, and the next edit would be composed against that.
    if (reference === null) return null;
    references.push(reference);
  }

  return {
    id: raw['id'],
    workingTitle: (raw['workingTitle'] as string | null | undefined) ?? null,
    pictureBrief: (raw['pictureBrief'] as string | null | undefined) ?? null,
    briefSource,
    workingBrief: (raw['workingBrief'] as string | null | undefined) ?? null,
    channelKeys,
    day,
    weeklyThemeKey: (raw['weeklyThemeKey'] as string | null | undefined) ?? null,
    references: references.sort((left, right) => left.sortOrder - right.sortOrder),
    createdAt: raw['createdAt'],
    updatedAt: raw['updatedAt'],
    archivedAt: (raw['archivedAt'] as string | null | undefined) ?? null,
    concurrencyToken: raw['concurrencyToken'],
  };
}

export function decodeCreativeContextSummary(raw: unknown): CreativeContextSummary | null {
  if (!isRecord(raw)) return null;

  const channelKeys = decodeStrings(raw['channelKeys']);
  const day = decodeDay(raw['day']);

  if (
    typeof raw['id'] !== 'string' ||
    typeof raw['updatedAt'] !== 'string' ||
    typeof raw['referenceCount'] !== 'number' ||
    !isStringOrNull(raw['workingTitle'] ?? null) ||
    !isStringOrNull(raw['weeklyThemeKey'] ?? null) ||
    channelKeys === null ||
    day === undefined
  ) {
    return null;
  }

  return {
    id: raw['id'],
    workingTitle: (raw['workingTitle'] as string | null | undefined) ?? null,
    channelKeys,
    day,
    weeklyThemeKey: (raw['weeklyThemeKey'] as string | null | undefined) ?? null,
    referenceCount: raw['referenceCount'],
    updatedAt: raw['updatedAt'],
  };
}

export function decodeCreativeContextPage(raw: unknown): CreativeContextPage | null {
  if (!isRecord(raw) || !Array.isArray(raw['items']) || !isStringOrNull(raw['nextCursor'] ?? null)) return null;

  const items: CreativeContextSummary[] = [];

  for (const item of raw['items']) {
    const summary = decodeCreativeContextSummary(item);
    if (summary === null) return null;
    items.push(summary);
  }

  return { items, nextCursor: (raw['nextCursor'] as string | null | undefined) ?? null };
}
