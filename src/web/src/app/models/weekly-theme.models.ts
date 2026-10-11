// A workspace's themed days and what was made for them, as `…/weekly-themes` answers. Mirrors
// `WeeklyThemeServiceModel`, `WeeklyThemeWeekServiceModel` and `ThemedDayHistoryItemServiceModel`.

import { isRecord } from './recipe.models';

export type WeekDay = 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday' | 'Sunday';

/** A creator's week, Monday first — the order the server lists the live week in. */
export const WEEK_DAYS: readonly WeekDay[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

/** The server's caps, so a field can refuse what the route would (`WeeklyThemePolicy`). */
export const WEEKLY_THEME_LIMITS = {
  keyMaxLength: 64,
  displayNameMaxLength: 120,
  descriptionMaxLength: 500,
  standingNotesMaxLength: 2000,
} as const;

export interface WeeklyTheme {
  readonly day: WeekDay;
  /** Stable, and what other records store. Never shown to a creator and never changed by a rename. */
  readonly key: string;
  readonly displayName: string;
  readonly description: string | null;
  readonly standingNotes: string | null;
  /** Set once the theme has left the live week. Its row and its key survive. */
  readonly retiredAt: string | null;
  readonly revision: number;
}

/** One theme as the week replace takes it. */
export interface WeeklyThemeInput {
  readonly day: WeekDay;
  readonly key: string;
  readonly displayName: string;
  readonly description: string | null;
  readonly standingNotes: string | null;
}

export type ThemedDayOutcomeKind = 'KeptPicture' | 'AcceptedPosts' | 'AcceptedRecipeDraft';

export interface ThemedDayOutcome {
  readonly kind: ThemedDayOutcomeKind;
  readonly at: string;
  readonly mediaAssetId: string | null;
  readonly channelKeys: readonly string[];
  readonly needsReview: boolean;
  readonly recipeId: string | null;
}

export interface ThemedDayHistoryItem {
  readonly contextId: string;
  readonly workingTitle: string | null;
  readonly isArchived: boolean;
  readonly latestOutcomeAt: string;
  readonly outcomes: readonly ThemedDayOutcome[];
}

export interface ThemedDayHistoryPage {
  readonly items: readonly ThemedDayHistoryItem[];
  readonly nextCursor: string | null;
}

const OUTCOME_KINDS: readonly ThemedDayOutcomeKind[] = ['KeptPicture', 'AcceptedPosts', 'AcceptedRecipeDraft'];

export function isWeekDay(value: unknown): value is WeekDay {
  return typeof value === 'string' && (WEEK_DAYS as readonly string[]).includes(value);
}

function optionalText(value: unknown): string | null | undefined {
  if (value === null || value === undefined) return null;
  return typeof value === 'string' ? value : undefined;
}

function decodeTheme(value: unknown): WeeklyTheme | null {
  if (!isRecord(value)) return null;

  const { day, key, displayName, revision } = value;
  const description = optionalText(value['description']);
  const standingNotes = optionalText(value['standingNotes']);
  const retiredAt = optionalText(value['retiredAt']);

  if (
    !isWeekDay(day) ||
    typeof key !== 'string' ||
    typeof displayName !== 'string' ||
    typeof revision !== 'number' ||
    description === undefined ||
    standingNotes === undefined ||
    retiredAt === undefined
  ) {
    return null;
  }

  return { day, key, displayName, description, standingNotes, retiredAt, revision };
}

/**
 * The week, or null when the body is not one.
 *
 * One theme that does not decode fails the whole week rather than being dropped: this screen replaces the week
 * with what it shows, so a theme silently missing from the form would be retired by the next save.
 */
export function decodeWeeklyThemeWeek(value: unknown): readonly WeeklyTheme[] | null {
  if (!isRecord(value) || !Array.isArray(value['themes'])) return null;

  const themes: WeeklyTheme[] = [];
  for (const entry of value['themes'] as readonly unknown[]) {
    const theme = decodeTheme(entry);
    if (theme === null) return null;
    themes.push(theme);
  }

  return themes;
}

function decodeOutcome(value: unknown): ThemedDayOutcome | null {
  if (!isRecord(value)) return null;

  const { kind, at } = value;
  if (typeof kind !== 'string' || !(OUTCOME_KINDS as readonly string[]).includes(kind) || typeof at !== 'string') {
    return null;
  }

  const channelKeys = value['channelKeys'];

  return {
    kind: kind as ThemedDayOutcomeKind,
    at,
    mediaAssetId: typeof value['mediaAssetId'] === 'string' ? value['mediaAssetId'] : null,
    channelKeys: Array.isArray(channelKeys) ? channelKeys.filter((key): key is string => typeof key === 'string') : [],
    needsReview: value['needsReview'] === true,
    recipeId: typeof value['recipeId'] === 'string' ? value['recipeId'] : null,
  };
}

/**
 * One page of a theme's history, or null when the body is not one.
 *
 * An outcome of a kind this client does not know is left out rather than failing the page: the history is a
 * read-only list, so an item with one fewer line is degraded, not wrong.
 */
export function decodeThemedDayHistoryPage(value: unknown): ThemedDayHistoryPage | null {
  if (!isRecord(value) || !Array.isArray(value['items'])) return null;

  const items: ThemedDayHistoryItem[] = [];
  for (const entry of value['items'] as readonly unknown[]) {
    if (!isRecord(entry)) return null;

    const { contextId, latestOutcomeAt, outcomes } = entry;
    if (typeof contextId !== 'string' || typeof latestOutcomeAt !== 'string' || !Array.isArray(outcomes)) return null;

    items.push({
      contextId,
      workingTitle: typeof entry['workingTitle'] === 'string' ? entry['workingTitle'] : null,
      isArchived: entry['isArchived'] === true,
      latestOutcomeAt,
      outcomes: (outcomes as readonly unknown[])
        .map(decodeOutcome)
        .filter((outcome): outcome is ThemedDayOutcome => outcome !== null),
    });
  }

  const nextCursor = value['nextCursor'];

  return { items, nextCursor: typeof nextCursor === 'string' ? nextCursor : null };
}

/**
 * A key for a new theme, made once from its name and never again.
 *
 * Lowercase letters, digits and hyphens, which is the only shape the server accepts, and different from every
 * key in `taken` — retired themes included, because a key is never reused in a workspace. A name with nothing
 * usable in it ("🌮") falls back to the day.
 */
export function weeklyThemeKeyFor(displayName: string, day: WeekDay, taken: ReadonlySet<string>): string {
  const slug = displayName
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    // Room left for a "-99" suffix inside the server's cap.
    .slice(0, WEEKLY_THEME_LIMITS.keyMaxLength - 4)
    .replace(/-+$/g, '');

  const base = slug.length > 0 ? slug : `${day.toLowerCase()}-theme`;
  if (!taken.has(base)) return base;

  for (let suffix = 2; ; suffix++) {
    const candidate = `${base}-${suffix}`;
    if (!taken.has(candidate)) return candidate;
  }
}
