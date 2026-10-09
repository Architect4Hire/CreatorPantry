// The DAM library's list shapes and the view helpers built on them (DAM-UI-001/002).
//
// Mirrors `MediaAssetSummaryServiceModel` and `MediaAssetSearchPageServiceModel`. Nothing here names a
// workspace, a member, a URL, an object key, a checksum or bytes, because the list route publishes none — and
// each decoder builds its result field by field, so an unexpected field could never ride through to a screen.

import { DAYS_OF_WEEK, DAY_OF_WEEK_VALUES, DayOfWeek } from './content-seed.models';
import {
  PROMPT_IMAGE_KIND_LABELS,
  PROMPT_SOURCE_LABELS,
  PromptImageKind,
  PromptRecordSource,
} from './prompt-library.models';
import { decodeEnum, isNumberOrNull, isRecord, isStringOrNull } from './recipe.models';

/** `MediaAssetKind`, as stored. `Unspecified` is never valid on a row, so it is not a value here. */
export type DamAssetKind = 'Original' | 'Imported' | 'Derived' | 'AiGenerated';

/** Where an asset's bytes came from, in plain words. Exhaustive by type. */
export const DAM_ASSET_KIND_LABELS: Readonly<Record<DamAssetKind, string>> = {
  Original: 'Uploaded',
  Imported: 'Imported',
  Derived: 'Edited copy',
  AiGenerated: 'AI generated',
};

const KINDS: ReadonlySet<string> = new Set(Object.keys(DAM_ASSET_KIND_LABELS));

/** `MediaAssetSearchSort`. The server's two orderings; arbitrary columns are not accepted. */
export type DamAssetSort = 'RecentlyAdded' | 'Title';

export const DAM_ASSET_DAYS: readonly DayOfWeek[] = DAYS_OF_WEEK;

/** One asset as a library page reports it: enough to draw a card, and never where its bytes are. */
export interface DamAssetSummary {
  readonly id: string;
  readonly title: string;
  readonly description: string | null;
  readonly kind: DamAssetKind;
  /** The creator's own description of the picture, when they wrote one. */
  readonly altText: string | null;
  readonly channelKey: string | null;
  readonly platformKey: string | null;
  readonly day: DayOfWeek | null;
  readonly styleKey: string | null;
  readonly cuisineId: string | null;
  readonly courseId: string | null;
  /** Version numbers start at one and are never removed, so this is also how many versions there are. */
  readonly currentVersionNumber: number;
  /** Null, with zero dimensions and size, for an asset whose current version could not be read. */
  readonly mediaType: string | null;
  readonly width: number;
  readonly height: number;
  readonly sizeBytes: number;
  /**
   * How many times the asset has been logged as used, or null when the server did not say.
   *
   * Null rather than a refusal: the field was added after the route shipped, so an older server omits it, and
   * a library that went blank over one missing count would be the worse failure. A card then says nothing
   * about usage, which is true.
   */
  readonly utilizationCount: number | null;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/**
 * What showing an asset's picture needs, and nothing else — so a list row and a detail page can both hand one
 * to the same component. A `DamAssetSummary` already is one.
 */
export interface DamAssetPicture {
  readonly id: string;
  readonly title: string;
  readonly altText: string | null;
  readonly currentVersionNumber: number;
}

export interface DamAssetSearchPage {
  readonly items: readonly DamAssetSummary[];
  /** Null on the last page. */
  readonly nextCursor: string | null;
  /** Every match across all pages, or null when the request asked not to be told. */
  readonly totalCount: number | null;
}

/**
 * What the library asks for.
 *
 * A subset of what the route supports, on purpose: platform, style and tags have no route that lists their
 * choices, so there is nothing to build a picker from; cuisine, course, date and recipe filters have no control
 * on this screen yet.
 */
export interface DamAssetSearchQuery {
  readonly search: string;
  /** A `ContentChannel.key`, or null for every channel. */
  readonly channel: string | null;
  readonly day: DayOfWeek | null;
  readonly sort: DamAssetSort;
  /** The previous page's `nextCursor`, or null for the first page. */
  readonly cursor: string | null;
}

export function encodeDamAssetSearchQuery(query: DamAssetSearchQuery): Record<string, string> {
  const params: Record<string, string> = {};

  const search = query.search.trim();
  if (search.length > 0) params['search'] = search;
  if (query.channel !== null && query.channel.trim() !== '') params['channel'] = query.channel.trim();
  if (query.day !== null) params['day'] = query.day;
  // The default is left unsaid, so a shared link and a cursor scope do not depend on spelling it.
  if (query.sort === 'Title') params['sort'] = 'Title';

  if (query.cursor !== null) {
    params['cursor'] = query.cursor;

    // The total spans every page and cannot change as one is followed, so it is asked for once and carried by
    // the caller. Counting again per page is a second query for an answer already held.
    params['includeTotal'] = 'false';
  }

  return params;
}

export function decodeDamAssetSummary(value: unknown): DamAssetSummary | null {
  if (!isRecord(value)) return null;

  const {
    id,
    title,
    description,
    altText,
    channelKey,
    platformKey,
    styleKey,
    cuisineId,
    courseId,
    currentVersionNumber,
    mediaType,
    width,
    height,
    sizeBytes,
    utilizationCount,
    createdAt,
    updatedAt,
  } = value;
  const kind = decodeEnum<DamAssetKind>(KINDS, value['kind']);
  const rawDay = value['day'];
  const day = rawDay === null || rawDay === undefined ? null : decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, rawDay);

  if (
    typeof id !== 'string' ||
    id === '' ||
    typeof title !== 'string' ||
    !isStringOrNull(description) ||
    kind === null ||
    !isStringOrNull(altText) ||
    !isStringOrNull(channelKey) ||
    !isStringOrNull(platformKey) ||
    // A day that is present and unreadable fails the row: guessing "no day" would put it in the wrong filter.
    (rawDay !== null && rawDay !== undefined && day === null) ||
    !isStringOrNull(styleKey) ||
    !isStringOrNull(cuisineId) ||
    !isStringOrNull(courseId) ||
    typeof currentVersionNumber !== 'number' ||
    !isStringOrNull(mediaType) ||
    typeof width !== 'number' ||
    typeof height !== 'number' ||
    typeof sizeBytes !== 'number' ||
    (utilizationCount !== undefined && !isNumberOrNull(utilizationCount)) ||
    typeof createdAt !== 'string' ||
    createdAt === '' ||
    typeof updatedAt !== 'string'
  ) {
    return null;
  }

  return {
    id,
    title,
    description,
    kind,
    altText,
    channelKey,
    platformKey,
    day,
    styleKey,
    cuisineId,
    courseId,
    currentVersionNumber,
    mediaType,
    width,
    height,
    sizeBytes,
    utilizationCount: typeof utilizationCount === 'number' ? utilizationCount : null,
    createdAt,
    updatedAt,
  };
}

export function decodeDamAssetSearchPage(value: unknown): DamAssetSearchPage | null {
  if (!isRecord(value)) return null;
  const { items, nextCursor, totalCount } = value;

  if (!Array.isArray(items) || !isStringOrNull(nextCursor) || !isNumberOrNull(totalCount)) return null;

  const decoded = items.map(decodeDamAssetSummary);
  // One unreadable row fails the page rather than silently shortening it: a library that quietly drops an
  // asset is worse than one that reports it could not be read.
  if (decoded.some((item) => item === null)) return null;

  return { items: decoded as DamAssetSummary[], nextCursor, totalCount };
}

/** "3 versions". The current number is the count, because numbers start at one and none is ever removed. */
export function damVersionCountText(asset: DamAssetSummary): string {
  const count = Math.max(1, asset.currentVersionNumber);

  return count === 1 ? '1 version' : `${count} versions`;
}

/** How often the asset has been logged as used, or an empty string when the server did not say. */
export function damUsageText(asset: DamAssetSummary): string {
  const count = asset.utilizationCount;
  if (count === null) return '';
  if (count <= 0) return 'Not used yet';

  return count === 1 ? 'Used once' : `Used ${count} times`;
}

/**
 * What a screen reader is told a thumbnail shows.
 *
 * The creator's own alt text when they wrote one, because they have seen the picture. Otherwise only what is
 * known: its title, said as a title. Nothing here has looked at the pixels, so nothing here describes them
 * (.claude/rules/media.md).
 */
export function damThumbnailAltText(asset: Pick<DamAssetPicture, 'title' | 'altText'>): string {
  const written = asset.altText?.trim() ?? '';

  return written !== '' ? written : `Picture titled “${asset.title}”. No description has been written for it.`;
}

// ---------------------------------------------------------------------------
// One asset in full (DAM-UI-003). Mirrors `MediaAssetDetailServiceModel` and the utilization page.
//
// Deliberately narrower than the route: the checksum, the generated-image id and the tombstone fields are not
// read, because nothing on the page uses them. The concurrency token is, since 12.10g: an edit has to quote it.
// ---------------------------------------------------------------------------

/** `MediaAssetVersionSource`. How one version's bytes arrived. */
export type DamAssetVersionSource = 'Upload' | 'GeneratedImage';

export const DAM_VERSION_SOURCE_LABELS: Readonly<Record<DamAssetVersionSource, string>> = {
  Upload: 'Uploaded',
  GeneratedImage: 'Kept from a generated picture',
};

/** `RecipeAssetRole`. What a picture is to a recipe that uses it. */
export type DamRecipeAssetRole = 'Hero' | 'Gallery' | 'Process' | 'Social' | 'Step';

export const DAM_RECIPE_ROLE_LABELS: Readonly<Record<DamRecipeAssetRole, string>> = {
  Hero: 'Lead picture',
  Gallery: 'Gallery picture',
  Process: 'In-progress picture',
  Social: 'Social picture',
  Step: 'Step picture',
};

const VERSION_SOURCES: ReadonlySet<string> = new Set(Object.keys(DAM_VERSION_SOURCE_LABELS));
const RECIPE_ROLES: ReadonlySet<string> = new Set(Object.keys(DAM_RECIPE_ROLE_LABELS));
const PROMPT_KINDS: ReadonlySet<string> = new Set(Object.keys(PROMPT_IMAGE_KIND_LABELS));
const PROMPT_SOURCES: ReadonlySet<string> = new Set(Object.keys(PROMPT_SOURCE_LABELS));

export interface DamAssetVersion {
  readonly versionNumber: number;
  readonly mediaType: string;
  readonly width: number;
  readonly height: number;
  readonly sizeBytes: number;
  /** The creator's own file name, for display only. Never what a download is named. */
  readonly originalFileName: string | null;
  readonly source: DamAssetVersionSource;
  readonly createdAt: string;
}

export interface DamAssetTag {
  readonly id: string;
  readonly name: string;
}

export interface DamAssetRecipeLink {
  readonly recipeId: string;
  readonly title: string;
  readonly role: DamRecipeAssetRole;
  readonly caption: string | null;
}

/** One prompt in the asset's lineage: enough to name it and open it, never its words. */
export interface DamAssetPrompt {
  readonly id: string;
  readonly label: string | null;
  readonly imageKind: PromptImageKind;
  readonly source: PromptRecordSource;
  readonly createdAt: string;
}

export interface DamAssetDetail {
  readonly id: string;
  readonly title: string;
  readonly description: string | null;
  readonly altText: string | null;
  readonly kind: DamAssetKind;
  readonly channelKey: string | null;
  readonly platformKey: string | null;
  readonly day: DayOfWeek | null;
  readonly styleKey: string | null;
  readonly rightsHolder: string | null;
  readonly attributionText: string | null;
  readonly tags: readonly DamAssetTag[];
  /** Null for an asset whose current version could not be read. */
  readonly currentVersion: DamAssetVersion | null;
  /** Every version, newest first, as the server orders them. */
  readonly versions: readonly DamAssetVersion[];
  readonly utilizationCount: number;
  readonly recipeLinks: readonly DamAssetRecipeLink[];
  /**
   * How many brand profiles use the asset, and how many recipe test-run pictures do — or null when the server
   * did not say. Null rather than zero: the fields were added after the route shipped, and "nothing else uses
   * this" is not something to tell a creator about to remove an asset on the strength of a missing field.
   */
  readonly brandProfileCount: number | null;
  readonly testAttachmentCount: number | null;
  readonly prompts: readonly DamAssetPrompt[];
  readonly createdAt: string;
  readonly updatedAt: string;
  /**
   * Opaque. Sent back as `expectedConcurrencyToken` by the next metadata edit, and never parsed, compared or
   * shown. It changes whenever the asset does — an edit, a new version — so it is always taken from the
   * latest read.
   */
  readonly concurrencyToken: string;
}

/** One logged use. `utilizedOn` is a calendar date (`YYYY-MM-DD`), not an instant. */
export interface DamAssetUtilization {
  readonly id: string;
  readonly platformKey: string;
  readonly utilizedOn: string;
  /** The weekday as the server derived it when the use was logged. Shown as sent, never re-derived here. */
  readonly utilizedDay: DayOfWeek;
  readonly campaignName: string | null;
  readonly notes: string | null;
}

export interface DamAssetUtilizationPage {
  readonly items: readonly DamAssetUtilization[];
  readonly nextCursor: string | null;
  readonly totalCount: number | null;
}

const CALENDAR_DATE = /^(\d{4})-(\d{2})-(\d{2})$/;

function decodeAll<T>(value: unknown, decode: (item: unknown) => T | null): T[] | null {
  if (!Array.isArray(value)) return null;

  const decoded = value.map(decode);

  // One unreadable entry fails the list rather than silently shortening it.
  return decoded.some((item) => item === null) ? null : (decoded as T[]);
}

export function decodeDamAssetVersion(value: unknown): DamAssetVersion | null {
  if (!isRecord(value)) return null;

  const { versionNumber, mediaType, width, height, sizeBytes, originalFileName, createdAt } = value;
  const source = decodeEnum<DamAssetVersionSource>(VERSION_SOURCES, value['source']);

  if (
    typeof versionNumber !== 'number' ||
    typeof mediaType !== 'string' ||
    typeof width !== 'number' ||
    typeof height !== 'number' ||
    typeof sizeBytes !== 'number' ||
    !isStringOrNull(originalFileName) ||
    source === null ||
    typeof createdAt !== 'string'
  ) {
    return null;
  }

  return { versionNumber, mediaType, width, height, sizeBytes, originalFileName, source, createdAt };
}

function decodeTag(value: unknown): DamAssetTag | null {
  if (!isRecord(value)) return null;
  const { id, name } = value;

  return typeof id === 'string' && typeof name === 'string' ? { id, name } : null;
}

function decodeRecipeLink(value: unknown): DamAssetRecipeLink | null {
  if (!isRecord(value)) return null;

  const { recipeId, title, caption } = value;
  const role = decodeEnum<DamRecipeAssetRole>(RECIPE_ROLES, value['role']);

  return typeof recipeId === 'string' &&
    recipeId !== '' &&
    typeof title === 'string' &&
    role !== null &&
    isStringOrNull(caption)
    ? { recipeId, title, role, caption }
    : null;
}

function decodePrompt(value: unknown): DamAssetPrompt | null {
  if (!isRecord(value)) return null;

  const { id, label, createdAt } = value;
  const imageKind = decodeEnum<PromptImageKind>(PROMPT_KINDS, value['imageKind']);
  const source = decodeEnum<PromptRecordSource>(PROMPT_SOURCES, value['source']);

  return typeof id === 'string' &&
    id !== '' &&
    isStringOrNull(label) &&
    imageKind !== null &&
    source !== null &&
    typeof createdAt === 'string'
    ? { id, label, imageKind, source, createdAt }
    : null;
}

export function decodeDamAssetDetail(value: unknown): DamAssetDetail | null {
  if (!isRecord(value)) return null;

  const {
    id,
    title,
    description,
    altText,
    channelKey,
    platformKey,
    styleKey,
    rightsHolder,
    attributionText,
    utilizationCount,
    createdAt,
    updatedAt,
    concurrencyToken,
    brandProfileCount,
    testAttachmentCount,
  } = value;
  const kind = decodeEnum<DamAssetKind>(KINDS, value['kind']);
  const rawDay = value['day'];
  const day = rawDay === null || rawDay === undefined ? null : decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, rawDay);
  const rawCurrent = value['currentVersion'];
  const currentVersion = rawCurrent === null || rawCurrent === undefined ? null : decodeDamAssetVersion(rawCurrent);
  const tags = decodeAll(value['tags'], decodeTag);
  const versions = decodeAll(value['versions'], decodeDamAssetVersion);
  const recipeLinks = decodeAll(value['recipeLinks'], decodeRecipeLink);
  const prompts = decodeAll(value['prompts'], decodePrompt);

  if (
    typeof id !== 'string' ||
    id === '' ||
    typeof title !== 'string' ||
    !isStringOrNull(description) ||
    !isStringOrNull(altText) ||
    kind === null ||
    !isStringOrNull(channelKey) ||
    !isStringOrNull(platformKey) ||
    (rawDay !== null && rawDay !== undefined && day === null) ||
    !isStringOrNull(styleKey) ||
    !isStringOrNull(rightsHolder) ||
    !isStringOrNull(attributionText) ||
    tags === null ||
    (rawCurrent !== null && rawCurrent !== undefined && currentVersion === null) ||
    versions === null ||
    typeof utilizationCount !== 'number' ||
    recipeLinks === null ||
    prompts === null ||
    typeof createdAt !== 'string' ||
    typeof updatedAt !== 'string' ||
    typeof concurrencyToken !== 'string' ||
    concurrencyToken === '' ||
    (brandProfileCount !== undefined && !isNumberOrNull(brandProfileCount)) ||
    (testAttachmentCount !== undefined && !isNumberOrNull(testAttachmentCount))
  ) {
    return null;
  }

  return {
    id,
    title,
    description,
    altText,
    kind,
    channelKey,
    platformKey,
    day,
    styleKey,
    rightsHolder,
    attributionText,
    tags,
    currentVersion,
    versions,
    utilizationCount,
    recipeLinks,
    brandProfileCount: typeof brandProfileCount === 'number' ? brandProfileCount : null,
    testAttachmentCount: typeof testAttachmentCount === 'number' ? testAttachmentCount : null,
    prompts,
    createdAt,
    updatedAt,
    concurrencyToken,
  };
}

export function decodeDamAssetUtilization(value: unknown): DamAssetUtilization | null {
  if (!isRecord(value)) return null;

  const { id, platformKey, utilizedOn, campaignName, notes } = value;
  const utilizedDay = decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, value['utilizedDay']);

  if (
    typeof id !== 'string' ||
    id === '' ||
    typeof platformKey !== 'string' ||
    typeof utilizedOn !== 'string' ||
    !CALENDAR_DATE.test(utilizedOn) ||
    utilizedDay === null ||
    !isStringOrNull(campaignName) ||
    !isStringOrNull(notes)
  ) {
    return null;
  }

  return { id, platformKey, utilizedOn, utilizedDay, campaignName, notes };
}

export function decodeDamAssetUtilizationPage(value: unknown): DamAssetUtilizationPage | null {
  if (!isRecord(value)) return null;
  const { nextCursor, totalCount } = value;
  const items = decodeAll(value['items'], decodeDamAssetUtilization);

  if (items === null || !isStringOrNull(nextCursor) || !isNumberOrNull(totalCount)) return null;

  return { items, nextCursor, totalCount };
}

/**
 * A calendar date as a creator reads it, with no timezone in the way.
 *
 * `utilizedOn` is a date, not an instant. Handing `2026-10-06` to `new Date` reads it as midnight UTC, which a
 * browser west of Greenwich then shows as the 5th — so the three numbers are placed in UTC and formatted in
 * UTC, and the day that was logged is the day that is shown.
 */
export function damCalendarDateText(value: string, locale?: string): string {
  const match = CALENDAR_DATE.exec(value);
  if (match === null) return value;

  const at = new Date(Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3])));

  return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(at);
}

/** What a prompt in an asset's lineage is called: its label, or its kind when it has none. */
export function damPromptName(prompt: DamAssetPrompt): string {
  const label = prompt.label?.trim() ?? '';

  return label !== '' ? label : `${PROMPT_IMAGE_KIND_LABELS[prompt.imageKind]} prompt`;
}

// ---------------------------------------------------------------------------
// Editing an asset's metadata, and adding a version (DAM-UI-004).
// ---------------------------------------------------------------------------

/**
 * Every limit the edit form enforces, each mirroring the `MediaPolicy` constant named beside it.
 *
 * Enforced at the control so a creator is stopped while typing rather than by a refusal afterwards. The server
 * still decides: these are the same numbers, not a substitute for them.
 */
export const DAM_ASSET_LIMITS = {
  /** `MediaPolicy.AssetTitleMaxLength`. */
  titleMaxLength: 200,
  /** `MediaPolicy.AssetDescriptionMaxLength`. */
  descriptionMaxLength: 2000,
  /** `MediaPolicy.AltTextMaxLength`. */
  altTextMaxLength: 1000,
  /** `MediaPolicy.VocabularyKeyMaxLength` — channel, platform and style keys. */
  keyMaxLength: 64,
  /** `MediaPolicy.RightsTextMaxLength` — rights holder and attribution. */
  rightsMaxLength: 500,
  /**
   * `MediaPolicy.ImageMaxBytes`. The one ceiling checked before an upload, to spare a creator a transfer that
   * cannot succeed. What the bytes *are* stays the server's decision.
   */
  imageMaxBytes: 32 * 1024 * 1024,
} as const;

/** What the file picker offers. A hint to the browser only — the server reads the bytes, not the name. */
export const DAM_IMAGE_ACCEPT = 'image/png,image/jpeg,image/webp,image/gif';

/**
 * What a creator can change about an asset, as the edit form holds it.
 *
 * Text fields are strings, never null: an empty box is how a creator clears one. `tagIds` is the asset's
 * complete set. Cuisine and course are not here because the form has no control for them, and a field that is
 * not here can never be sent — so they are left exactly as they are.
 */
export interface DamAssetMetadataDraft {
  readonly title: string;
  readonly description: string;
  readonly altText: string;
  readonly channelKey: string;
  readonly platformKey: string;
  readonly day: DayOfWeek | null;
  readonly styleKey: string;
  readonly rightsHolder: string;
  readonly attributionText: string;
  readonly tagIds: readonly string[];
}

/** The text fields of the draft, which share one comparison and one encoding. */
export const DAM_METADATA_TEXT_FIELDS = [
  'title',
  'description',
  'altText',
  'channelKey',
  'platformKey',
  'styleKey',
  'rightsHolder',
  'attributionText',
] as const;

export type DamMetadataTextField = (typeof DAM_METADATA_TEXT_FIELDS)[number];
export type DamMetadataField = DamMetadataTextField | 'day' | 'tagIds';

export const DAM_METADATA_FIELDS: readonly DamMetadataField[] = [...DAM_METADATA_TEXT_FIELDS, 'day', 'tagIds'];

/** The form's starting point: the asset as it was read. */
export function damMetadataDraftOf(asset: DamAssetDetail): DamAssetMetadataDraft {
  return {
    title: asset.title,
    description: asset.description ?? '',
    altText: asset.altText ?? '',
    channelKey: asset.channelKey ?? '',
    platformKey: asset.platformKey ?? '',
    day: asset.day,
    styleKey: asset.styleKey ?? '',
    rightsHolder: asset.rightsHolder ?? '',
    attributionText: asset.attributionText ?? '',
    tagIds: asset.tags.map((tag) => tag.id),
  };
}

function sameTagSet(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false;
  const held = new Set(a);

  return b.every((id) => held.has(id));
}

/**
 * The fields that differ between two drafts.
 *
 * Text is compared trimmed, because the server stores it trimmed: a trailing space is not a change, and
 * sending one would be an edit that changes nothing. Tags are compared as a set, since their order means
 * nothing.
 */
export function damMetadataChanges(
  baseline: DamAssetMetadataDraft,
  current: DamAssetMetadataDraft,
): readonly DamMetadataField[] {
  const changed: DamMetadataField[] = [];

  for (const field of DAM_METADATA_TEXT_FIELDS) {
    if (baseline[field].trim() !== current[field].trim()) changed.push(field);
  }
  if (baseline.day !== current.day) changed.push('day');
  if (!sameTagSet(baseline.tagIds, current.tagIds)) changed.push('tagIds');

  return changed;
}

/**
 * The PATCH body: the token, and only the fields the creator changed.
 *
 * **A field that did not change is left out entirely**, which is what the route's merge semantics read as
 * "leave it alone" — so an edit to the title can never blank an attribution, and a field this form does not
 * know about is never touched. **A field emptied is sent as `null`**, which is "clear it". The title is the
 * one field that cannot be cleared; an empty one is refused by the form before this is called.
 *
 * `tagIds` is published as `tags`, the route's own name, and replaces the asset's whole set.
 */
export function encodeDamAssetPatch(
  expectedConcurrencyToken: string,
  baseline: DamAssetMetadataDraft,
  current: DamAssetMetadataDraft,
): Record<string, unknown> {
  const body: Record<string, unknown> = { expectedConcurrencyToken };

  for (const field of damMetadataChanges(baseline, current)) {
    if (field === 'tagIds') {
      body['tags'] = [...current.tagIds];
    } else if (field === 'day') {
      body['day'] = current.day;
    } else {
      const text = current[field].trim();
      body[field] = text === '' ? null : text;
    }
  }

  return body;
}

/**
 * What stands in the way of saving, by field. Empty when the draft may be sent.
 *
 * The lengths are also enforced by `maxlength` on each control, so these are reached only by a paste a browser
 * did not cut — but a refusal the creator can read beats one the server has to send.
 */
export function validateDamMetadataDraft(draft: DamAssetMetadataDraft): Readonly<Partial<Record<DamMetadataField, string>>> {
  const errors: Partial<Record<DamMetadataField, string>> = {};
  const tooLong = (field: DamMetadataTextField, max: number, name: string): void => {
    if (draft[field].trim().length > max) errors[field] = `${name} can be at most ${max} characters.`;
  };

  if (draft.title.trim() === '') errors.title = 'A picture needs a title.';
  tooLong('title', DAM_ASSET_LIMITS.titleMaxLength, 'The title');
  tooLong('description', DAM_ASSET_LIMITS.descriptionMaxLength, 'The description');
  tooLong('altText', DAM_ASSET_LIMITS.altTextMaxLength, 'Alt text');
  tooLong('channelKey', DAM_ASSET_LIMITS.keyMaxLength, 'The channel');
  tooLong('platformKey', DAM_ASSET_LIMITS.keyMaxLength, 'The platform');
  tooLong('styleKey', DAM_ASSET_LIMITS.keyMaxLength, 'The style');
  tooLong('rightsHolder', DAM_ASSET_LIMITS.rightsMaxLength, 'The rights holder');
  tooLong('attributionText', DAM_ASSET_LIMITS.rightsMaxLength, 'The attribution');

  return errors;
}

/**
 * A draft carried onto a newer read of the same asset, after someone else changed it.
 *
 * **The creator's own changes win; everything they did not touch takes the latest.** For each field, "touched"
 * means it differs from the baseline the creator started from. That keeps their work and stops a stale copy of
 * an untouched field from being sent back over a collaborator's edit.
 */
export function rebaseDamMetadataDraft(
  staleBaseline: DamAssetMetadataDraft,
  current: DamAssetMetadataDraft,
  latest: DamAssetMetadataDraft,
): DamAssetMetadataDraft {
  const touched = new Set(damMetadataChanges(staleBaseline, current));
  const pick = <K extends DamMetadataField>(field: K): DamAssetMetadataDraft[K] =>
    touched.has(field) ? current[field] : latest[field];

  return {
    title: pick('title'),
    description: pick('description'),
    altText: pick('altText'),
    channelKey: pick('channelKey'),
    platformKey: pick('platformKey'),
    day: pick('day'),
    styleKey: pick('styleKey'),
    rightsHolder: pick('rightsHolder'),
    attributionText: pick('attributionText'),
    tagIds: pick('tagIds'),
  };
}

/** The server's name for a field, mapped back to the form's — only `tags` differs. */
export function damMetadataFieldFor(serverField: string): DamMetadataField | null {
  const name = serverField.charAt(0).toLowerCase() + serverField.slice(1);
  if (name === 'tags' || name === 'workspaceTagIds') return 'tagIds';

  return (DAM_METADATA_FIELDS as readonly string[]).includes(name) ? (name as DamMetadataField) : null;
}

// ---------------------------------------------------------------------------
// Logging a use, and removing an asset from the library (DAM-UI-004/005).
// ---------------------------------------------------------------------------

/** The limits a logged use obeys, mirroring `MediaPolicy` and `MediaAssetUtilizationPolicy`. */
export const DAM_USE_LIMITS = {
  /** `MediaPolicy.VocabularyKeyMaxLength`. */
  platformMaxLength: 64,
  /** `MediaPolicy.CampaignNameMaxLength`. */
  campaignMaxLength: 200,
  /** `MediaPolicy.NotesMaxLength`. */
  notesMaxLength: 2000,
  /**
   * How many days past today a use may be dated. One, as the server allows: not "after today", so a creator
   * east of the server is not refused for already being on tomorrow's date.
   */
  maxDaysAhead: 1,
} as const;

/** One use as the form holds it. `utilizedOn` is a calendar date, `YYYY-MM-DD`, as a date input gives it. */
export interface DamUseDraft {
  readonly platformKey: string;
  readonly utilizedOn: string;
  readonly campaignName: string;
  readonly notes: string;
}

export type DamUseField = keyof DamUseDraft;

export const DAM_USE_FIELDS: readonly DamUseField[] = ['platformKey', 'utilizedOn', 'campaignName', 'notes'];

/**
 * A date as `YYYY-MM-DD` in the browser's own calendar.
 *
 * Built from the local year, month and day rather than from `toISOString`, which is UTC: late in the evening
 * west of Greenwich that would already be tomorrow, and "today" would default to a date the creator is not on.
 */
export function damLocalDateValue(date: Date, addDays = 0): string {
  const shifted = new Date(date.getFullYear(), date.getMonth(), date.getDate() + addDays);
  const pad = (value: number): string => String(value).padStart(2, '0');

  return `${shifted.getFullYear()}-${pad(shifted.getMonth() + 1)}-${pad(shifted.getDate())}`;
}

export function emptyDamUseDraft(today: Date = new Date()): DamUseDraft {
  return { platformKey: '', utilizedOn: damLocalDateValue(today), campaignName: '', notes: '' };
}

/** True when the creator has entered something a close would lose. The defaulted date alone is not that. */
export function isDamUseDraftTouched(draft: DamUseDraft, today: Date = new Date()): boolean {
  return (
    draft.platformKey.trim() !== '' ||
    draft.campaignName.trim() !== '' ||
    draft.notes.trim() !== '' ||
    draft.utilizedOn !== damLocalDateValue(today)
  );
}

/** What stands in the way of logging the use, by field. Empty when it may be sent. */
export function validateDamUseDraft(
  draft: DamUseDraft,
  today: Date = new Date(),
): Readonly<Partial<Record<DamUseField, string>>> {
  const errors: Partial<Record<DamUseField, string>> = {};

  const platform = draft.platformKey.trim();
  if (platform === '') errors.platformKey = 'Say where it was used.';
  else if (platform.length > DAM_USE_LIMITS.platformMaxLength) {
    errors.platformKey = `The platform can be at most ${DAM_USE_LIMITS.platformMaxLength} characters.`;
  }

  if (!CALENDAR_DATE.test(draft.utilizedOn)) {
    errors.utilizedOn = 'Choose the date it was used.';
  } else if (draft.utilizedOn > damLocalDateValue(today, DAM_USE_LIMITS.maxDaysAhead)) {
    // `YYYY-MM-DD` strings order the same way the dates do, so this needs no parsing and no timezone.
    errors.utilizedOn = 'That date is in the future. A use can be dated today or earlier.';
  }

  if (draft.campaignName.trim().length > DAM_USE_LIMITS.campaignMaxLength) {
    errors.campaignName = `The campaign can be at most ${DAM_USE_LIMITS.campaignMaxLength} characters.`;
  }
  if (draft.notes.trim().length > DAM_USE_LIMITS.notesMaxLength) {
    errors.notes = `Notes can be at most ${DAM_USE_LIMITS.notesMaxLength} characters.`;
  }

  return errors;
}

/**
 * The POST body for a logged use.
 *
 * **No weekday.** The server derives it from the date, and a client that could send one could send one that
 * disagrees with its own date. Empty optional text is sent as null rather than as blanks.
 */
export function encodeDamUse(draft: DamUseDraft): Record<string, unknown> {
  const campaign = draft.campaignName.trim();
  const notes = draft.notes.trim();

  return {
    platformKey: draft.platformKey.trim(),
    utilizedOn: draft.utilizedOn,
    campaignName: campaign === '' ? null : campaign,
    notes: notes === '' ? null : notes,
  };
}

/** The server's name for a use field, mapped back to the form's. */
export function damUseFieldFor(serverField: string): DamUseField | null {
  const name = serverField.charAt(0).toLowerCase() + serverField.slice(1);

  return (DAM_USE_FIELDS as readonly string[]).includes(name) ? (name as DamUseField) : null;
}

/** A recipe that still points at a removed asset. */
export interface DamAffectedRecipe {
  readonly id: string;
  readonly title: string;
}

/**
 * One asset's removal, as the route reports it. Mirrors `MediaAssetDeletionServiceModel`, without the
 * membership id of who removed it — which no screen can turn into a name — and without the token, which a
 * removed asset has no further use for.
 */
export interface DamAssetRemoval {
  readonly id: string;
  readonly title: string;
  readonly deletedAt: string;
  /** True when the asset had already been removed and this call changed nothing. */
  readonly alreadyDeleted: boolean;
  /** How many recipes still link to it, which can exceed the ones that could be named. */
  readonly recipeCount: number;
  readonly recipes: readonly DamAffectedRecipe[];
  readonly brandProfileCount: number;
  readonly testAttachmentCount: number;
}

export function decodeDamAssetRemoval(value: unknown): DamAssetRemoval | null {
  if (!isRecord(value)) return null;

  const { id, title, deletedAt, alreadyDeleted } = value;
  const affected = value['affected'];
  if (
    typeof id !== 'string' ||
    id === '' ||
    typeof title !== 'string' ||
    typeof deletedAt !== 'string' ||
    typeof alreadyDeleted !== 'boolean' ||
    !isRecord(affected)
  ) {
    return null;
  }

  const { recipeCount, brandProfileCount, testAttachmentCount } = affected;
  const recipes = decodeAll(affected['recipes'], (item): DamAffectedRecipe | null => {
    if (!isRecord(item)) return null;
    const recipeId = item['id'];
    const recipeTitle = item['title'];

    return typeof recipeId === 'string' && typeof recipeTitle === 'string' ? { id: recipeId, title: recipeTitle } : null;
  });

  if (
    typeof recipeCount !== 'number' ||
    typeof brandProfileCount !== 'number' ||
    typeof testAttachmentCount !== 'number' ||
    recipes === null
  ) {
    return null;
  }

  return { id, title, deletedAt, alreadyDeleted, recipeCount, recipes, brandProfileCount, testAttachmentCount };
}

function countOf(count: number, one: string, many: string): string {
  return count === 1 ? `1 ${one}` : `${count} ${many}`;
}

/** How many recipe names a sentence carries before it says "and N more". */
const NAMED_RECIPES = 3;

/**
 * What still points at the asset, as the clauses of one sentence. Empty when nothing does.
 *
 * Recipes are named, up to three, because a creator can go and fix a recipe they can name. Brand profiles and
 * test-run pictures are counted, which is all the server says about them.
 */
export function damLinkImpactClauses(impact: {
  readonly recipeTitles: readonly string[];
  readonly recipeCount: number;
  readonly brandProfileCount: number | null;
  readonly testAttachmentCount: number | null;
}): readonly string[] {
  const clauses: string[] = [];

  if (impact.recipeCount > 0) {
    const named = impact.recipeTitles.slice(0, NAMED_RECIPES);
    const unnamed = impact.recipeCount - named.length;
    // "More" only means something after a name: with none to give, the count alone is the whole truth.
    const names = named.length > 0 && unnamed > 0 ? [...named, `${unnamed} more`] : named;

    clauses.push(
      names.length > 0
        ? `${countOf(impact.recipeCount, 'recipe', 'recipes')} (${names.join(', ')})`
        : countOf(impact.recipeCount, 'recipe', 'recipes'),
    );
  }
  if ((impact.brandProfileCount ?? 0) > 0) {
    clauses.push(countOf(impact.brandProfileCount ?? 0, 'brand profile', 'brand profiles'));
  }
  if ((impact.testAttachmentCount ?? 0) > 0) {
    clauses.push(countOf(impact.testAttachmentCount ?? 0, 'recipe test picture', 'recipe test pictures'));
  }

  return clauses;
}

function joinClauses(clauses: readonly string[]): string {
  if (clauses.length <= 1) return clauses.join('');

  return `${clauses.slice(0, -1).join(', ')} and ${clauses[clauses.length - 1]}`;
}

/**
 * The question a creator is asked before an asset is removed, built only from what the asset's own read says.
 *
 * **It never says "delete".** Removing takes the asset out of the library and erases nothing: the files, the
 * versions and the history are kept, and every link is left standing. It says that — and it also says the
 * other true thing, that the app cannot bring the asset back yet, because a creator deciding on the first
 * sentence alone would be deciding on half of it.
 */
export function damRemovalWarning(asset: DamAssetDetail): string {
  // Distinct recipes: an asset that is a recipe's lead picture and in its gallery is one recipe to go and fix.
  const titles = Array.from(new Map(asset.recipeLinks.map((link) => [link.recipeId, link.title])).values());
  const clauses = damLinkImpactClauses({
    recipeTitles: titles,
    recipeCount: titles.length,
    brandProfileCount: asset.brandProfileCount,
    testAttachmentCount: asset.testAttachmentCount,
  });

  const sentences = [
    `“${asset.title}” will be taken out of the library for everyone in this workspace.`,
    'Its files, versions and usage history are kept, not erased.',
  ];

  if (clauses.length > 0) {
    sentences.push(
      `It is still used by ${joinClauses(clauses)}. Those links stay, and will point at a picture that is no longer in the library.`,
    );
  } else if (asset.brandProfileCount !== null && asset.testAttachmentCount !== null) {
    sentences.push('Nothing else in this workspace is using it.');
  }

  sentences.push('There is no way to bring it back from the app yet.');

  return sentences.join(' ');
}

/** The picture a detail page shows, in the shape the thumbnail asks for. */
export function damPictureOf(asset: DamAssetDetail): DamAssetPicture {
  return {
    id: asset.id,
    title: asset.title,
    altText: asset.altText,
    // The version is part of what tells one picture from the next, so a page left open across a new version
    // would fetch again. Zero for an asset with no readable version, which the render route answers 404 for.
    currentVersionNumber: asset.currentVersion?.versionNumber ?? 0,
  };
}

// ---------------------------------------------------------------------------
// Keeping a generated picture as a library asset (AF.4.1).
// ---------------------------------------------------------------------------

/**
 * An asset as the create routes report it. Mirrors `MediaAssetServiceModel`.
 *
 * **`recipeId` is not proof of a link.** When the picture was already in the library the route answers with
 * the asset the first save made and echoes the recipe this request named, without writing anything. Read the
 * asset's own detail to know what it is linked to.
 */
export interface DamCreatedAsset {
  readonly id: string;
  readonly title: string;
  readonly kind: DamAssetKind;
  readonly currentVersionNumber: number;
  readonly mediaType: string;
  readonly width: number;
  readonly height: number;
  readonly sizeBytes: number;
  readonly sourceGeneratedImageId: string | null;
  /** The prompt saved beside the asset by this request, or null when none was. */
  readonly promptRecordId: string | null;
  readonly recipeId: string | null;
  readonly createdAt: string;
}

export function decodeDamCreatedAsset(value: unknown): DamCreatedAsset | null {
  if (!isRecord(value)) return null;

  const {
    id,
    title,
    currentVersionNumber,
    mediaType,
    width,
    height,
    sizeBytes,
    sourceGeneratedImageId,
    promptRecordId,
    recipeId,
    createdAt,
  } = value;
  const kind = decodeEnum<DamAssetKind>(KINDS, value['kind']);

  if (
    typeof id !== 'string' ||
    id === '' ||
    typeof title !== 'string' ||
    kind === null ||
    typeof currentVersionNumber !== 'number' ||
    typeof mediaType !== 'string' ||
    typeof width !== 'number' ||
    typeof height !== 'number' ||
    typeof sizeBytes !== 'number' ||
    !isStringOrNull(sourceGeneratedImageId ?? null) ||
    !isStringOrNull(promptRecordId ?? null) ||
    !isStringOrNull(recipeId ?? null) ||
    typeof createdAt !== 'string'
  ) {
    return null;
  }

  return {
    id,
    title,
    kind,
    currentVersionNumber,
    mediaType,
    width,
    height,
    sizeBytes,
    sourceGeneratedImageId: (sourceGeneratedImageId as string | null | undefined) ?? null,
    promptRecordId: (promptRecordId as string | null | undefined) ?? null,
    recipeId: (recipeId as string | null | undefined) ?? null,
    createdAt,
  };
}

/** `MediaAssetInputChecks.MaxTags`: the most tags one asset may carry when it is created. */
export const DAM_KEEP_MAX_TAGS = 25;

/**
 * The prompt that made a picture, as the surface that asked for it holds it. Mirrors `PromptRecordSaveInput`.
 *
 * Lineage, not form input: the save dialog sends it as given or not at all. A `Manual` prompt names no
 * proposal, draft or template; any other source names all of them, which is the caller's to get right.
 */
export interface DamKeepPrompt {
  readonly channelKey: string;
  readonly imageKind: PromptImageKind;
  readonly text: string;
  readonly source: PromptRecordSource;
  readonly label?: string | null;
  readonly generatedText?: string | null;
  readonly aiProposalId?: string | null;
  readonly recipeId?: string | null;
  readonly recipeVersionId?: string | null;
  readonly promptTemplateId?: string | null;
  readonly promptTemplateVersion?: string | null;
  readonly promptTemplateBodyChecksum?: string | null;
}

/** A recipe as the save dialog names it: the id to send, and the title to show. */
export interface DamKeepRecipe {
  readonly id: string;
  readonly title: string;
}

/** What a creator says about a generated picture as it goes into the library. */
export interface DamKeepDraft {
  readonly title: string;
  readonly altText: string;
  readonly tagIds: readonly string[];
  readonly recipe: DamKeepRecipe | null;
  /** Whether the prompt goes into the prompt library too. Means nothing when there is no prompt to keep. */
  readonly keepPrompt: boolean;
}

export type DamKeepField = 'title' | 'altText' | 'tagIds' | 'recipe';

export const DAM_KEEP_FIELDS: readonly DamKeepField[] = ['title', 'altText', 'tagIds', 'recipe'];

/** True when two drafts would send the same request. Text is compared trimmed, tags as a set. */
export function sameDamKeepDraft(a: DamKeepDraft, b: DamKeepDraft): boolean {
  return (
    a.title.trim() === b.title.trim() &&
    a.altText.trim() === b.altText.trim() &&
    sameTagSet(a.tagIds, b.tagIds) &&
    (a.recipe?.id ?? null) === (b.recipe?.id ?? null) &&
    a.keepPrompt === b.keepPrompt
  );
}

/** What stands in the way of saving, by field. Empty when the draft may be sent. */
export function validateDamKeepDraft(draft: DamKeepDraft): Readonly<Partial<Record<DamKeepField, string>>> {
  const errors: Partial<Record<DamKeepField, string>> = {};

  if (draft.title.trim() === '') {
    errors.title = 'A picture needs a title.';
  } else if (draft.title.trim().length > DAM_ASSET_LIMITS.titleMaxLength) {
    errors.title = `The title can be at most ${DAM_ASSET_LIMITS.titleMaxLength} characters.`;
  }
  if (draft.altText.trim().length > DAM_ASSET_LIMITS.altTextMaxLength) {
    errors.altText = `Alt text can be at most ${DAM_ASSET_LIMITS.altTextMaxLength} characters.`;
  }
  if (draft.tagIds.length > DAM_KEEP_MAX_TAGS) {
    errors.tagIds = `A picture can start with at most ${DAM_KEEP_MAX_TAGS} tags.`;
  }

  return errors;
}

/**
 * The body of `POST /dam-assets/from-generated-image`.
 *
 * **Nothing about the bytes, and no workspace**: the server reads the media facts from the staged picture's
 * own row. Empty alt text is left out rather than sent blank, and so are an empty tag list and an absent
 * recipe. The prompt is sent only when the creator asked to keep it, naming the picture it made.
 */
export function encodeDamKeep(
  generatedImageId: string,
  draft: DamKeepDraft,
  prompt: DamKeepPrompt | null,
): Record<string, unknown> {
  const metadata: Record<string, unknown> = { title: draft.title.trim() };
  const altText = draft.altText.trim();
  if (altText !== '') metadata['altText'] = altText;
  if (draft.tagIds.length > 0) metadata['workspaceTagIds'] = [...draft.tagIds];

  const body: Record<string, unknown> = { generatedImageId, metadata };
  if (draft.recipe !== null) body['recipeLink'] = { recipeId: draft.recipe.id };

  if (prompt !== null && draft.keepPrompt) {
    const sent: Record<string, unknown> = {
      channelKey: prompt.channelKey,
      imageKind: prompt.imageKind,
      text: prompt.text,
      source: prompt.source,
      generatedImageId,
    };
    const optional = [
      'label',
      'generatedText',
      'aiProposalId',
      'recipeId',
      'recipeVersionId',
      'promptTemplateId',
      'promptTemplateVersion',
      'promptTemplateBodyChecksum',
    ] as const;
    for (const key of optional) {
      const value = prompt[key];
      if (value !== null && value !== undefined && value !== '') sent[key] = value;
    }

    body['prompt'] = sent;
  }

  return body;
}

/** The server's name for a refused field, mapped to the dialog's. Null for one the dialog has no control for. */
export function damKeepFieldFor(serverField: string): DamKeepField | null {
  // A nested name arrives as `metadata.title` from the binder and as `Title` from the domain's own checks.
  const last = serverField.split('.').pop() ?? '';
  const name = last.charAt(0).toLowerCase() + last.slice(1);

  if (name === 'title' || name === 'altText') return name;
  if (name === 'workspaceTagIds' || name === 'tagIds' || name === 'tags') return 'tagIds';
  if (name === 'recipeId' || name === 'recipeLink') return 'recipe';

  return null;
}

/**
 * True when the answer to a save reads as the asset an *earlier* save made.
 *
 * **An inference, because the route does not say.** A picture is kept exactly once, and a repeat answers `201`
 * with the first asset and applies nothing from the new request. Two things give that away: the title is not
 * the one sent, or a prompt was sent and none was recorded. A repeat that sent the same title and no prompt is
 * indistinguishable from a first save — and harmless to treat as one, since nothing the creator entered
 * differs from what is stored under that title.
 */
export function isDamKeepRepeat(sent: DamKeepDraft, promptSent: boolean, asset: DamCreatedAsset): boolean {
  return asset.title.trim() !== sent.title.trim() || (promptSent && asset.promptRecordId === null);
}
