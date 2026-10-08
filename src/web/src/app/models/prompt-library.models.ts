// The Prompt Library's API shapes and the view helpers built on them (PROMPT-UI-001 through 003).
//
// Mirrors `PromptSummaryServiceModel`, `PromptDetailServiceModel` and `PromptSearchPageServiceModel`. Nothing
// here names a workspace, a member, a URL, a storage path or bytes, because the routes publish none — a decoder
// that carried an unknown field through would be the one way such a thing could reach a screen, so each one
// builds its result field by field.

import { ContentChannel } from './brand-profile.models';
import { ImageStudioDraft, emptyImageStudioDraft } from './image-studio.models';
import { decodeEnum, isNumberOrNull, isRecord, isStringOrNull } from './recipe.models';

/** `PromptImageKind`, as the API publishes it: the declared names, never the numbers. */
export type PromptImageKind =
  | 'Hero'
  | 'ProcessStep'
  | 'IngredientLayout'
  | 'StyledScene'
  | 'SocialTile'
  | 'PinGraphic'
  | 'DetailShot'
  | 'Other';

/** `PromptRecordSource`. */
export type PromptRecordSource = 'Manual' | 'PhotographyConcept' | 'ImagePromptComposition' | 'ReferenceImageAnalysis';

/** What a creator reads for each kind. Exhaustive by type, so a kind added server-side has to be worded here. */
export const PROMPT_IMAGE_KIND_LABELS: Readonly<Record<PromptImageKind, string>> = {
  Hero: 'Hero shot',
  ProcessStep: 'Process step',
  IngredientLayout: 'Ingredient layout',
  StyledScene: 'Styled scene',
  SocialTile: 'Social tile',
  PinGraphic: 'Pin graphic',
  DetailShot: 'Detail shot',
  Other: 'Other',
};

/** Where a prompt came from, in plain words. `Manual` is the only one no model had a hand in. */
export const PROMPT_SOURCE_LABELS: Readonly<Record<PromptRecordSource, string>> = {
  Manual: 'Written by you',
  PhotographyConcept: 'From a planned look',
  ImagePromptComposition: 'Written for you',
  ReferenceImageAnalysis: 'From a reference photo',
};

const IMAGE_KINDS: ReadonlySet<string> = new Set(Object.keys(PROMPT_IMAGE_KIND_LABELS));
const SOURCES: ReadonlySet<string> = new Set(Object.keys(PROMPT_SOURCE_LABELS));

/** One row of the library: enough to recognise a prompt, and deliberately not the prompt. */
export interface PromptSummary {
  readonly promptRecordId: string;
  readonly channelKey: string;
  readonly imageKind: PromptImageKind;
  /** Truncated by the server. Compare with {@link textLength} before claiming it is the whole prompt. */
  readonly textPreview: string;
  readonly textLength: number;
  readonly label: string | null;
  readonly source: PromptRecordSource;
  readonly recipeId: string | null;
  readonly recipeVersionId: string | null;
  readonly createdAt: string;
}

/** One prompt in full. The only shape that carries the prompt itself. */
export interface PromptDetail {
  readonly promptRecordId: string;
  readonly channelKey: string;
  readonly imageKind: PromptImageKind;
  readonly text: string;
  /** The model's draft, when a model wrote one. Equal to {@link text} when it was used unchanged. */
  readonly generatedText: string | null;
  readonly label: string | null;
  readonly source: PromptRecordSource;
  readonly aiProposalId: string | null;
  readonly recipeId: string | null;
  readonly recipeVersionId: string | null;
  readonly promptTemplateId: string | null;
  readonly promptTemplateVersion: string | null;
  readonly promptTemplateBodyChecksum: string | null;
  readonly createdAt: string;
}

export interface PromptSearchPage {
  readonly items: readonly PromptSummary[];
  /** Null on the last page. */
  readonly nextCursor: string | null;
  /** Every match across all pages, or null when the request asked not to be told. */
  readonly totalCount: number | null;
}

/**
 * What the library asks for. Search and one channel are the only filters the route has, and newest-first is
 * its only ordering — so there is no sort member to send.
 */
export interface PromptSearchQuery {
  readonly search: string;
  /** A `ContentChannel.key`, or null for every channel. */
  readonly channel: string | null;
  /** The previous page's `nextCursor`, or null for the first page. */
  readonly cursor: string | null;
}

export function encodePromptSearchQuery(query: PromptSearchQuery): Record<string, string> {
  const params: Record<string, string> = {};

  const search = query.search.trim();
  if (search.length > 0) params['search'] = search;
  if (query.channel !== null && query.channel.trim() !== '') params['channel'] = query.channel.trim();

  if (query.cursor !== null) {
    params['cursor'] = query.cursor;

    // The total spans every page and cannot change as one is followed, so it is asked for once and carried by
    // the caller. Counting again per page is a second query for an answer already held.
    params['includeTotal'] = 'false';
  }

  return params;
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value !== '';
}

export function decodePromptSummary(value: unknown): PromptSummary | null {
  if (!isRecord(value)) return null;

  const { promptRecordId, channelKey, textPreview, textLength, label, recipeId, recipeVersionId, createdAt } = value;
  const imageKind = decodeEnum<PromptImageKind>(IMAGE_KINDS, value['imageKind']);
  const source = decodeEnum<PromptRecordSource>(SOURCES, value['source']);

  if (
    !isNonEmptyString(promptRecordId) ||
    typeof channelKey !== 'string' ||
    imageKind === null ||
    typeof textPreview !== 'string' ||
    typeof textLength !== 'number' ||
    !isStringOrNull(label) ||
    source === null ||
    !isStringOrNull(recipeId) ||
    !isStringOrNull(recipeVersionId) ||
    !isNonEmptyString(createdAt)
  ) {
    return null;
  }

  return {
    promptRecordId,
    channelKey,
    imageKind,
    textPreview,
    textLength,
    label,
    source,
    recipeId,
    recipeVersionId,
    createdAt,
  };
}

export function decodePromptSearchPage(value: unknown): PromptSearchPage | null {
  if (!isRecord(value)) return null;
  const { items, nextCursor, totalCount } = value;

  if (!Array.isArray(items) || !isStringOrNull(nextCursor) || !isNumberOrNull(totalCount)) return null;

  const decoded = items.map(decodePromptSummary);
  // One unreadable row fails the page rather than silently shortening it: a library that quietly drops a
  // prompt is worse than one that reports it could not be read.
  if (decoded.some((item) => item === null)) return null;

  return { items: decoded as PromptSummary[], nextCursor, totalCount };
}

export function decodePromptDetail(value: unknown): PromptDetail | null {
  if (!isRecord(value)) return null;

  const {
    promptRecordId,
    channelKey,
    text,
    generatedText,
    label,
    aiProposalId,
    recipeId,
    recipeVersionId,
    promptTemplateId,
    promptTemplateVersion,
    promptTemplateBodyChecksum,
    createdAt,
  } = value;
  const imageKind = decodeEnum<PromptImageKind>(IMAGE_KINDS, value['imageKind']);
  const source = decodeEnum<PromptRecordSource>(SOURCES, value['source']);

  if (
    !isNonEmptyString(promptRecordId) ||
    typeof channelKey !== 'string' ||
    imageKind === null ||
    typeof text !== 'string' ||
    !isStringOrNull(generatedText) ||
    !isStringOrNull(label) ||
    source === null ||
    !isStringOrNull(aiProposalId) ||
    !isStringOrNull(recipeId) ||
    !isStringOrNull(recipeVersionId) ||
    !isStringOrNull(promptTemplateId) ||
    !isStringOrNull(promptTemplateVersion) ||
    !isStringOrNull(promptTemplateBodyChecksum) ||
    !isNonEmptyString(createdAt)
  ) {
    return null;
  }

  return {
    promptRecordId,
    channelKey,
    imageKind,
    text,
    generatedText,
    label,
    source,
    aiProposalId,
    recipeId,
    recipeVersionId,
    promptTemplateId,
    promptTemplateVersion,
    promptTemplateBodyChecksum,
    createdAt,
  };
}

/** What a prompt is called on screen. A label is optional, so a prompt without one still needs a name. */
export function promptTitle(label: string | null): string {
  const trimmed = label?.trim() ?? '';

  return trimmed === '' ? 'Untitled prompt' : trimmed;
}

/** True when the row's preview is not the whole prompt, which is the only time an ellipsis is honest. */
export function isPromptPreviewTruncated(summary: PromptSummary): boolean {
  return summary.textLength > summary.textPreview.length;
}

/**
 * The name to show for a channel key.
 *
 * A key the catalogue does not hold — retired and removed, or the catalogue could not be read — is shown as the
 * key itself rather than hidden: the prompt stored it, and saying nothing would be less true than saying that.
 */
export function promptChannelName(channels: readonly ContentChannel[], key: string): string {
  if (key.trim() === '') return 'No channel';

  return channels.find((channel) => channel.key === key)?.displayName ?? key;
}

/**
 * A new Image Studio draft that starts from a library prompt.
 *
 * **A new piece of work, never a change to the prompt it came from.** A saved prompt is immutable, so reusing
 * one means copying its words into a fresh draft and leaving the record exactly as it was.
 *
 * The words arrive as the creator's own (`promptSource: 'creator'`) whatever wrote them originally: they chose
 * this prompt, so nothing in the studio may replace it without asking. The channel is carried only when it is
 * one that can still be chosen — a retired key would be refused the moment the studio tried to use it.
 */
export function imageStudioDraftFromPrompt(
  prompt: PromptDetail,
  channels: readonly ContentChannel[],
  now: Date = new Date(),
): ImageStudioDraft {
  const base = emptyImageStudioDraft(now);
  const channel = channels.find((each) => each.key === prompt.channelKey && each.isActive);

  return {
    ...base,
    config: { ...base.config, channelKey: channel?.key ?? null },
    prompt: { ...base.prompt, finalPrompt: prompt.text, promptSource: 'creator' },
  };
}
