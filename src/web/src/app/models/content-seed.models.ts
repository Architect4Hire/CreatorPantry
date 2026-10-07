// Wire models for GET /api/v1/workspaces/{workspaceSlug}/content-seeds. Mirrors
// CreatorPantry.Domain/Modules/Content/Managers/ContentSeedServiceModels.cs and
// ContentSeedViewModels.cs exactly.
//
// A seed is a suggestion, not a record. Nothing is persisted server-side, no model is called, and there is no
// seed to fetch later: `GET` is the whole feature. Asking again with the same `token` returns the same seed,
// against the same catalogue state and the same week.
//
// Every facet is nullable. An empty or fully retired catalogue, and a workspace with no theme on the chosen
// day, yield a seed with fewer parts rather than an error — so a client renders what arrived and never treats
// an absent facet as a failure.

import { decodeEnum, isRecord } from './recipe.models';

/** Mirrors System.DayOfWeek, as the global JsonStringEnumConverter writes it. */
export type DayOfWeek = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday';

/**
 * In the enum's own order, which is also the order the server offers and validates.
 *
 * Not reordered to start the week on Monday: the day is a key in a contract, a creator picks one rather than
 * reading a calendar, and a client-side reshuffle would only put the picker and the wire out of step.
 */
export const DAYS_OF_WEEK: readonly DayOfWeek[] = [
  'Sunday',
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
];

export const DAY_OF_WEEK_VALUES: ReadonlySet<string> = new Set<DayOfWeek>(DAYS_OF_WEEK);

/** One selected facet: the stable key to pin next time, and the name to show. */
export interface ContentSeedFacet {
  readonly key: string;
  readonly displayName: string;
  /** True when the caller asked for this value rather than the seed choosing it. */
  readonly pinned: boolean;
}

/** The method facet, which carries one fact the others do not. */
export interface ContentSeedMethod extends ContentSeedFacet {
  /**
   * True means guidance about this technique must carry an explicit caution, and a client showing this seed
   * must show one.
   *
   * **False means only that no caution has been attached.** It is not a statement that the technique is safe,
   * and no surface may render it as one (.claude/rules/ai.md).
   */
  readonly requiresSafetyCaution: boolean;
}

/** The day a seed is for, and the workspace's own theme for that day when it has one. */
export interface ContentSeedDay {
  readonly day: DayOfWeek;
  readonly pinned: boolean;
  /** Null when the workspace has no live theme on this day — the normal case, and never an error. */
  readonly theme: ContentSeedFacet | null;
}

/** One content idea, assembled from reference vocabulary and the workspace's own week. */
export interface ContentSeed {
  /** What produced this seed. Send it back to reproduce or share it. */
  readonly token: string;
  readonly cuisine: ContentSeedFacet | null;
  readonly dishType: ContentSeedFacet | null;
  readonly method: ContentSeedMethod | null;
  readonly photographyStyle: ContentSeedFacet | null;
  readonly channel: ContentSeedFacet | null;
  readonly day: ContentSeedDay;
  readonly occasion: ContentSeedFacet | null;
  /**
   * One line naming the seed's own parts, assembled server-side from fixed connecting text. Deterministic and
   * never model-written.
   *
   * **It can contain the creator's own words**, because a weekly theme's display name is free text they wrote.
   * Treat it as creator content: show it, let them edit a copy of it, and never let it be folded into
   * instructions for a model (.claude/rules/ai.md).
   */
  readonly description: string;
}

function decodeFacet(value: unknown): ContentSeedFacet | null {
  if (!isRecord(value)) return null;
  const { key, displayName, pinned } = value;
  if (typeof key !== 'string' || typeof displayName !== 'string' || typeof pinned !== 'boolean') return null;

  return { key, displayName, pinned };
}

/** An optional facet: absent is a valid answer, a malformed one is not, and the two must not be confused. */
function decodeOptionalFacet(value: unknown): { readonly facet: ContentSeedFacet | null } | null {
  if (value === null || value === undefined) return { facet: null };
  const facet = decodeFacet(value);

  return facet === null ? null : { facet };
}

function decodeMethod(value: unknown): { readonly method: ContentSeedMethod | null } | null {
  if (value === null || value === undefined) return { method: null };
  const base = decodeFacet(value);
  if (base === null || !isRecord(value)) return null;

  const requiresSafetyCaution = value['requiresSafetyCaution'];
  if (typeof requiresSafetyCaution !== 'boolean') return null;

  return { method: { ...base, requiresSafetyCaution } };
}

function decodeDay(value: unknown): ContentSeedDay | null {
  if (!isRecord(value)) return null;
  const day = decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, value['day']);
  const pinned = value['pinned'];
  const theme = decodeOptionalFacet(value['theme']);
  if (day === null || typeof pinned !== 'boolean' || theme === null) return null;

  return { day, pinned, theme: theme.facet };
}

export function decodeContentSeed(value: unknown): ContentSeed | null {
  if (!isRecord(value)) return null;

  const token = value['token'];
  const description = value['description'];
  const cuisine = decodeOptionalFacet(value['cuisine']);
  const dishType = decodeOptionalFacet(value['dishType']);
  const method = decodeMethod(value['method']);
  const photographyStyle = decodeOptionalFacet(value['photographyStyle']);
  const channel = decodeOptionalFacet(value['channel']);
  const occasion = decodeOptionalFacet(value['occasion']);
  const day = decodeDay(value['day']);

  if (
    typeof token !== 'string' ||
    token.length === 0 ||
    typeof description !== 'string' ||
    cuisine === null ||
    dishType === null ||
    method === null ||
    photographyStyle === null ||
    channel === null ||
    occasion === null ||
    day === null
  ) {
    return null;
  }

  return {
    token,
    cuisine: cuisine.facet,
    dishType: dishType.facet,
    method: method.method,
    photographyStyle: photographyStyle.facet,
    channel: channel.facet,
    day,
    occasion: occasion.facet,
    description,
  };
}

/** `ContentSeedPolicy.TokenMaxLength`. Longer and the server refuses the request. */
export const CONTENT_SEED_TOKEN_MAX_LENGTH = 64;

/** `ContentSeedPolicy.KeyMaxLength`. The widest key any catalogue holds. */
export const CONTENT_SEED_KEY_MAX_LENGTH = 64;

/** `ContentSeedPolicy.IsToken` — url-safe characters only, so a token survives being pasted into a link. */
const TOKEN_PATTERN = /^[A-Za-z0-9_-]+$/;

export function isContentSeedToken(value: string): boolean {
  return value.length > 0 && value.length <= CONTENT_SEED_TOKEN_MAX_LENGTH && TOKEN_PATTERN.test(value);
}

/**
 * The facets a caller may pin, as the stable key of each.
 *
 * `day` is not here: it is a `DayOfWeek` rather than a catalogue key, so it travels on its own.
 */
export interface ContentSeedPins {
  readonly cuisine?: string | null;
  readonly dishType?: string | null;
  readonly method?: string | null;
  readonly photographyStyle?: string | null;
  readonly channel?: string | null;
  readonly occasion?: string | null;
}

export type ContentSeedPinName = keyof ContentSeedPins;

/** In the order the seed shows them, so a picker and the seed body agree. */
export const CONTENT_SEED_PIN_NAMES: readonly ContentSeedPinName[] = [
  'cuisine',
  'dishType',
  'method',
  'photographyStyle',
  'channel',
  'occasion',
];

/** What one request asks a seed for. Every part is optional; with none of it the server picks everything. */
export interface ContentSeedQuery extends ContentSeedPins {
  readonly token?: string | null;
  readonly day?: DayOfWeek | null;
}

/**
 * The query string for one request.
 *
 * **The names are PascalCase on the wire** — `Token`, `DishType`, `PhotographyStyle` — because the server binds
 * `ContentSeedQueryViewModel` by property name. camelCase keys bind to nothing, which would look like a working
 * request that quietly ignored every pin.
 *
 * A blank or absent value is left out rather than sent empty, which is the same thing to the server
 * (`ContentSeedInputChecks.Normalize`) and keeps the URL readable in a shared link.
 */
export function contentSeedQueryParams(query: ContentSeedQuery): Record<string, string> {
  const params: Record<string, string> = {};
  const put = (name: string, value: string | null | undefined): void => {
    const trimmed = value?.trim();
    if (trimmed) params[name] = trimmed;
  };

  put('Token', query.token);
  put('Cuisine', query.cuisine);
  put('DishType', query.dishType);
  put('Method', query.method);
  put('PhotographyStyle', query.photographyStyle);
  put('Channel', query.channel);
  put('Occasion', query.occasion);
  if (query.day) params['Day'] = query.day;

  return params;
}

/** The field names the server's `400 content.seed.invalid` uses, so a refusal can be shown beside its control. */
export const CONTENT_SEED_FIELD_NAMES: Readonly<Record<ContentSeedPinName | 'token' | 'day', string>> = {
  token: 'Token',
  day: 'Day',
  cuisine: 'Cuisine',
  dishType: 'DishType',
  method: 'Method',
  photographyStyle: 'PhotographyStyle',
  channel: 'Channel',
  occasion: 'Occasion',
};

/** `ContentErrorCodes.ContentSeedInvalid`. */
export const CONTENT_SEED_INVALID_CODE = 'content.seed.invalid';
