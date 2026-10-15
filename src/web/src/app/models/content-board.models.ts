import { decodeEnum, isNumberOrNull, isRecord, isStringOrNull } from './recipe.models';

/** `ContentCardColumn`: the four columns of the content board, in the order a card travels through them. */
export type ContentBoardColumn = 'ToDo' | 'InProcess' | 'ReadyForReview' | 'Approved';

/** The columns left to right. The order is the server's enum order, and the board draws no other. */
export const CONTENT_BOARD_COLUMNS: readonly ContentBoardColumn[] = ['ToDo', 'InProcess', 'ReadyForReview', 'Approved'];

const COLUMNS: ReadonlySet<string> = new Set<ContentBoardColumn>(CONTENT_BOARD_COLUMNS);

/** How each column is said to a creator. */
export const CONTENT_BOARD_COLUMN_LABELS: Record<ContentBoardColumn, string> = {
  ToDo: 'To do',
  InProcess: 'In process',
  ReadyForReview: 'Ready for review',
  Approved: 'Approved',
};

/** `DayOfWeek`, as the routes publish and accept it. */
export type ContentBoardDay = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday';

export const CONTENT_BOARD_DAYS: readonly ContentBoardDay[] = [
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
  'Sunday',
];

const DAYS: ReadonlySet<string> = new Set<ContentBoardDay>(CONTENT_BOARD_DAYS);

/** The records a card points at, by id. Each is null when the card names none of that kind. */
export interface ContentCardLineageIds {
  readonly creativeContextId: string | null;
  readonly recipeId: string | null;
  readonly recipeVersionId: string | null;
  readonly promptRecordId: string | null;
  readonly mediaAssetId: string | null;
  readonly mediaAssetVersionNumber: number | null;
  readonly socialPackageId: string | null;
}

/**
 * What has been delivered for a card. **The server sends null today** — no publication record exists yet — and
 * the board says "Not scheduled" for it rather than guessing from the column.
 */
export interface ContentCardPublishing {
  readonly publicationCount: number;
  readonly publishedCount: number;
  readonly lastPublishedAt: string | null;
}

/** One card as the board lists it (`ContentCardSummaryServiceModel`). */
export interface ContentCard {
  readonly id: string;
  readonly title: string;
  readonly descriptionPreview: string | null;
  readonly descriptionLength: number;
  readonly column: ContentBoardColumn;
  readonly channelKey: string | null;
  /** `yyyy-MM-dd`, a calendar date in the workspace's own zone. */
  readonly plannedDate: string | null;
  readonly plannedDay: ContentBoardDay | null;
  readonly cuisineCode: string | null;
  readonly dishTypeCode: string | null;
  readonly methodCode: string | null;
  readonly lineage: ContentCardLineageIds;
  /** Times the card changed column. Putting it in a new place in the same column is not one. */
  readonly moveCount: number;
  readonly hasPackage: boolean;
  readonly publishing: ContentCardPublishing | null;
  readonly createdAt: string;
  readonly updatedAt: string;
  /** What an edit, a move or a delete quotes. Opaque. */
  readonly concurrencyToken: string;
}

export interface ContentBoardColumnCount {
  readonly column: ContentBoardColumn;
  readonly count: number;
}

/** One page of the board, with every matching column's count across all pages. */
export interface ContentCardPage {
  readonly items: readonly ContentCard[];
  readonly nextCursor: string | null;
  readonly columns: readonly ContentBoardColumnCount[];
}

export type ContentCardLineageKind = 'CreativeContext' | 'Recipe' | 'PromptRecord' | 'MediaAsset' | 'SocialPackage';

const LINEAGE_KINDS: ReadonlySet<string> = new Set<ContentCardLineageKind>([
  'CreativeContext',
  'Recipe',
  'PromptRecord',
  'MediaAsset',
  'SocialPackage',
]);

/** One record a card points at, named where the server could name it. */
export interface ContentCardLineageEntry {
  readonly kind: ContentCardLineageKind;
  readonly id: string;
  readonly versionId: string | null;
  readonly versionNumber: number | null;
  readonly label: string | null;
  /** False when the record can no longer be read. The card still says what it pointed at. */
  readonly available: boolean;
}

/** One time the card entered a column. The first has no `fromColumn`: it is the card's creation. */
export interface ContentCardMove {
  readonly sequence: number;
  readonly fromColumn: ContentBoardColumn | null;
  readonly toColumn: ContentBoardColumn;
  readonly note: string | null;
  readonly occurredAt: string;
}

/** One card in full (`ContentCardDetailServiceModel`). The recipe package is 13.7's and is not read here. */
export interface ContentCardDetail {
  readonly card: ContentCard;
  readonly description: string | null;
  readonly lineage: readonly ContentCardLineageEntry[];
  readonly history: readonly ContentCardMove[];
  readonly historyTruncated: boolean;
}

/** The filters the board reads with. Each narrows it; together they combine with AND. */
export interface ContentBoardFilters {
  readonly channel: string | null;
  readonly day: ContentBoardDay | null;
  /** `yyyy-MM-dd`. */
  readonly date: string | null;
}

export const NO_BOARD_FILTERS: ContentBoardFilters = { channel: null, day: null, date: null };

/** What a creator types for a new card, or changes on one. Lineage is not edited here. */
export interface ContentCardFields {
  readonly title: string;
  readonly description: string | null;
  readonly channelKey: string | null;
  readonly plannedDate: string | null;
}

/** Where a card should be: a column, and the card it goes immediately before — or null for the bottom. */
export interface ContentCardPlace {
  readonly column: ContentBoardColumn;
  readonly beforeCardId: string | null;
  /** Kept on the history entry. Only a change of column takes one. */
  readonly note?: string | null;
}

/**
 * The card the Content Pipeline put on the board for one piece of work (13.7a), as the pipeline's last step
 * needs it: enough to say that it is there, what it is called and where it stands.
 */
export interface PipelineBoardCard {
  readonly id: string;
  readonly title: string;
  readonly column: ContentBoardColumn;
}

export function encodeContentBoardQuery(
  filters: ContentBoardFilters,
  column: ContentBoardColumn | null,
  cursor: string | null,
  limit: number | null,
): Record<string, string> {
  const params: Record<string, string> = {};
  if (filters.channel) params['channel'] = filters.channel;
  if (filters.day) params['day'] = filters.day;
  if (filters.date) params['date'] = filters.date;
  if (column) params['column'] = column;
  if (cursor) params['cursor'] = cursor;
  if (limit !== null) params['limit'] = String(limit);

  return params;
}

/** A new card. Only what was filled in is sent, so the server's own defaults stand for the rest. */
/**
 * The records a new card links, by id (13A.20). Each names something this workspace already owns, which the server
 * checks through its owner before anything is written; one left out is not linked.
 */
export interface ContentCardLineageInput {
  readonly creativeContextId?: string | null;
  readonly recipeId?: string | null;
  readonly recipeVersionId?: string | null;
  readonly mediaAssetId?: string | null;
  readonly mediaAssetVersionNumber?: number | null;
  readonly socialPackageId?: string | null;
}

export function encodeNewContentCard(
  fields: ContentCardFields,
  column: ContentBoardColumn,
  lineage: ContentCardLineageInput = {},
): Record<string, unknown> {
  const linked = Object.fromEntries(Object.entries(lineage).filter(([, value]) => value !== null && value !== undefined));

  return {
    title: fields.title,
    column,
    ...(fields.description ? { description: fields.description } : {}),
    ...(fields.channelKey ? { channelKey: fields.channelKey } : {}),
    ...(fields.plannedDate ? { plannedDate: fields.plannedDate } : {}),
    ...linked,
  };
}

/**
 * A merge patch: only the fields that differ from `before` are named, and a field emptied is sent as null.
 * A field left out is untouched by the server, so an edit never rewrites what the creator did not change.
 */
export function encodeContentCardPatch(
  before: ContentCardFields,
  after: ContentCardFields,
  expectedConcurrencyToken: string,
): Record<string, unknown> {
  const patch: Record<string, unknown> = { expectedConcurrencyToken };
  if (after.title !== before.title) patch['title'] = after.title;
  if (after.description !== before.description) patch['description'] = after.description;
  if (after.channelKey !== before.channelKey) patch['channelKey'] = after.channelKey;
  if (after.plannedDate !== before.plannedDate) patch['plannedDate'] = after.plannedDate;

  return patch;
}

/** True when a patch names nothing but its token: there is nothing to send. */
export function contentCardFieldsEqual(one: ContentCardFields, other: ContentCardFields): boolean {
  return (
    one.title === other.title &&
    one.description === other.description &&
    one.channelKey === other.channelKey &&
    one.plannedDate === other.plannedDate
  );
}

export function encodeContentCardMove(place: ContentCardPlace, expectedConcurrencyToken: string): Record<string, unknown> {
  return {
    expectedConcurrencyToken,
    column: place.column,
    ...(place.beforeCardId ? { beforeCardId: place.beforeCardId } : {}),
    ...(place.note ? { note: place.note } : {}),
  };
}

function decodeLineageIds(raw: unknown): ContentCardLineageIds | null {
  if (!isRecord(raw)) return null;

  const {
    creativeContextId,
    recipeId,
    recipeVersionId,
    promptRecordId,
    mediaAssetId,
    mediaAssetVersionNumber,
    socialPackageId,
  } = raw;

  return isStringOrNull(creativeContextId) &&
    isStringOrNull(recipeId) &&
    isStringOrNull(recipeVersionId) &&
    isStringOrNull(promptRecordId) &&
    isStringOrNull(mediaAssetId) &&
    isNumberOrNull(mediaAssetVersionNumber) &&
    isStringOrNull(socialPackageId)
    ? {
        creativeContextId,
        recipeId,
        recipeVersionId,
        promptRecordId,
        mediaAssetId,
        mediaAssetVersionNumber,
        socialPackageId,
      }
    : null;
}

/** Absent and null both mean "nothing delivered"; present and unreadable fails the card. */
function decodePublishing(raw: unknown): ContentCardPublishing | null | undefined {
  if (raw === null || raw === undefined) return null;
  if (!isRecord(raw)) return undefined;

  const { publicationCount, publishedCount, lastPublishedAt } = raw;

  return typeof publicationCount === 'number' && typeof publishedCount === 'number' && isStringOrNull(lastPublishedAt)
    ? { publicationCount, publishedCount, lastPublishedAt }
    : undefined;
}

/** Null for a body that is not a card: a caller shows nothing rather than half of one. */
export function decodeContentCard(raw: unknown): ContentCard | null {
  if (!isRecord(raw)) return null;

  const column = decodeEnum<ContentBoardColumn>(COLUMNS, raw['column']);
  const lineage = decodeLineageIds(raw['lineage']);
  const publishing = decodePublishing(raw['publishing']);
  const rawDay = raw['plannedDay'];
  const plannedDay = rawDay === null || rawDay === undefined ? null : decodeEnum<ContentBoardDay>(DAYS, rawDay);
  const {
    id,
    title,
    descriptionPreview,
    descriptionLength,
    channelKey,
    plannedDate,
    cuisineCode,
    dishTypeCode,
    methodCode,
    moveCount,
    hasPackage,
    createdAt,
    updatedAt,
    concurrencyToken,
  } = raw;

  if (
    typeof id !== 'string' ||
    id === '' ||
    typeof title !== 'string' ||
    column === null ||
    lineage === null ||
    publishing === undefined ||
    (rawDay !== null && rawDay !== undefined && plannedDay === null) ||
    !isStringOrNull(descriptionPreview) ||
    typeof descriptionLength !== 'number' ||
    !isStringOrNull(channelKey) ||
    !isStringOrNull(plannedDate) ||
    !isStringOrNull(cuisineCode) ||
    !isStringOrNull(dishTypeCode) ||
    !isStringOrNull(methodCode) ||
    typeof moveCount !== 'number' ||
    typeof hasPackage !== 'boolean' ||
    typeof createdAt !== 'string' ||
    typeof updatedAt !== 'string' ||
    typeof concurrencyToken !== 'string' ||
    concurrencyToken === ''
  ) {
    return null;
  }

  return {
    id,
    title,
    descriptionPreview,
    descriptionLength,
    column,
    channelKey,
    plannedDate,
    plannedDay,
    cuisineCode,
    dishTypeCode,
    methodCode,
    lineage,
    moveCount,
    hasPackage,
    publishing,
    createdAt,
    updatedAt,
    concurrencyToken,
  };
}

/** The whole page fails on one unreadable card: a partly read column would silently drop a creator's work. */
export function decodeContentCardPage(raw: unknown): ContentCardPage | null {
  if (!isRecord(raw) || !Array.isArray(raw['items']) || !Array.isArray(raw['columns'])) return null;
  if (!isStringOrNull(raw['nextCursor'])) return null;

  const items: ContentCard[] = [];
  for (const each of raw['items']) {
    const card = decodeContentCard(each);
    if (card === null) return null;
    items.push(card);
  }

  const columns: ContentBoardColumnCount[] = [];
  for (const each of raw['columns']) {
    if (!isRecord(each)) return null;

    const column = decodeEnum<ContentBoardColumn>(COLUMNS, each['column']);
    const count = each['count'];
    if (column === null || typeof count !== 'number') return null;
    columns.push({ column, count });
  }

  return { items, nextCursor: raw['nextCursor'], columns };
}

function decodeLineageEntry(raw: unknown): ContentCardLineageEntry | null {
  if (!isRecord(raw)) return null;

  const kind = decodeEnum<ContentCardLineageKind>(LINEAGE_KINDS, raw['kind']);
  const { id, versionId, versionNumber, label, available } = raw;

  return kind !== null &&
    typeof id === 'string' &&
    isStringOrNull(versionId) &&
    isNumberOrNull(versionNumber) &&
    isStringOrNull(label) &&
    typeof available === 'boolean'
    ? { kind, id, versionId, versionNumber, label, available }
    : null;
}

function decodeMove(raw: unknown): ContentCardMove | null {
  if (!isRecord(raw)) return null;

  const rawFrom = raw['fromColumn'];
  const fromColumn = rawFrom === null || rawFrom === undefined ? null : decodeEnum<ContentBoardColumn>(COLUMNS, rawFrom);
  const toColumn = decodeEnum<ContentBoardColumn>(COLUMNS, raw['toColumn']);
  const { sequence, note, occurredAt } = raw;

  return typeof sequence === 'number' &&
    toColumn !== null &&
    !(rawFrom !== null && rawFrom !== undefined && fromColumn === null) &&
    isStringOrNull(note) &&
    typeof occurredAt === 'string'
    ? { sequence, fromColumn, toColumn, note, occurredAt }
    : null;
}

export function decodeContentCardDetail(raw: unknown): ContentCardDetail | null {
  if (!isRecord(raw) || !Array.isArray(raw['lineage']) || !Array.isArray(raw['history'])) return null;

  const card = decodeContentCard(raw['card']);
  const { description, historyTruncated } = raw;
  if (card === null || !isStringOrNull(description) || typeof historyTruncated !== 'boolean') return null;

  const lineage: ContentCardLineageEntry[] = [];
  for (const each of raw['lineage']) {
    const entry = decodeLineageEntry(each);
    if (entry === null) return null;
    lineage.push(entry);
  }

  const history: ContentCardMove[] = [];
  for (const each of raw['history']) {
    const move = decodeMove(each);
    if (move === null) return null;
    history.push(move);
  }

  return { card, description, lineage, history, historyTruncated };
}

/** Null for a body that is not a card: a caller shows nothing rather than half of one. */
export function decodePipelineBoardCard(raw: unknown): PipelineBoardCard | null {
  if (!isRecord(raw)) return null;

  const card = raw['card'];
  if (!isRecord(card)) return null;

  const id = card['id'];
  const title = card['title'];
  const column = decodeEnum<ContentBoardColumn>(COLUMNS, card['column']);

  return typeof id === 'string' && id !== '' && typeof title === 'string' && column !== null
    ? { id, title, column }
    : null;
}
