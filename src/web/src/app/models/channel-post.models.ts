// Wire models for AF.6.4's post seam:
// POST/GET /api/v1/workspaces/{workspaceSlug}/channel-post-requests[/{requestId}] and
// GET/PATCH/POST /api/v1/workspaces/{workspaceSlug}/creative-contexts/{contextId}/posts[/{channelKey}[/disposition]].
// Mirrors CreatorPantry.Domain/Modules/Content/Managers/{SocialPackageModels,SocialPackageViewModels}.cs and
// Modules/Ai/Managers/AiChannelPostsRequestModels.cs — do not add fields the backend does not send, and keep
// this file in step with those ServiceModels.
//
// Enums are decoded closed, as every other decoder here does, with the cost `ai-proposal.models.ts` records:
// widen the union in the same change that adds a member server-side.
//
// Nothing here measures a body. The count, the limit and the verdict beside a stored revision were measured
// by the channel's writing profile on the server (AF.6.2), and a client that recomputed one could disagree
// with the server about whether a creator's post fits. The one place a count is computed in the browser is
// {@link channelPostCount}, for the live figure beside a box the creator is typing in — and it says so.

import { AiProposalStatus, decodeAiProposalStatus } from './ai-proposal.models';
import { decodeEnum, isRecord, isNumberOrNull, isStringOrNull } from './recipe.models';

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/** Mirrors `ContentProposalStatus`: where one channel's post stands. */
export type ChannelPostStatus = 'Proposed' | 'Accepted' | 'Rejected' | 'NeedsReview';

const CHANNEL_POST_STATUS_VALUES: ReadonlySet<string> = new Set<ChannelPostStatus>([
  'Proposed',
  'Accepted',
  'Rejected',
  'NeedsReview',
]);

/** Mirrors `ContentRevisionSource`: who wrote the words in one revision. */
export type ChannelPostSource = 'AiGenerated' | 'CreatorEdit' | 'Reaffirmed';

const CHANNEL_POST_SOURCE_VALUES: ReadonlySet<string> = new Set<ChannelPostSource>([
  'AiGenerated',
  'CreatorEdit',
  'Reaffirmed',
]);

/** Mirrors `SocialLimitStatus`: what the channel's writing profile made of the body's length. */
export type ChannelPostLimitStatus = 'NotChecked' | 'Within' | 'Over';

const CHANNEL_POST_LIMIT_STATUS_VALUES: ReadonlySet<string> = new Set<ChannelPostLimitStatus>([
  'NotChecked',
  'Within',
  'Over',
]);

/** Mirrors `ChannelPostDecision`. `Regenerate` is a new request rather than a decision about these words. */
export type ChannelPostDecision = 'Accept' | 'Reject' | 'Regenerate' | 'Reaffirm';

// ---------------------------------------------------------------------------
// Reads
// ---------------------------------------------------------------------------

/** One revision of one channel's post, as `SocialRevisionServiceModel` sends it. */
export interface ChannelPostRevision {
  readonly id: string;
  readonly revisionNumber: number;
  readonly source: ChannelPostSource;
  readonly body: string;
  /** As the channel's profile counts it, which need not be the string's length. Null when unmeasured. */
  readonly characterCount: number | null;
  readonly characterLimit: number | null;
  readonly limitStatus: ChannelPostLimitStatus;
  /** Present on a generated revision, so a creator can see these words were written for them. */
  readonly aiProposalId: string | null;
  readonly createdAt: string;
}

/**
 * One channel's output and where it stands, as `SocialChannelServiceModel` sends it.
 *
 * `isCurrent` is null until something is accepted, and false both for a channel needing review and for an
 * accepted one whose recipe has moved on — the server decides it from the pin rather than from the status, so
 * a client must not infer it from {@link status} either.
 */
export interface ChannelPostChannel {
  readonly channelKey: string;
  readonly status: ChannelPostStatus;
  readonly latest: ChannelPostRevision;
  readonly accepted: ChannelPostRevision | null;
  readonly isCurrent: boolean | null;
  readonly updatedAt: string;
}

/** The posts written for one piece of creative work, as `SocialPackageServiceModel` sends it. */
export interface ChannelPostPackage {
  readonly id: string;
  readonly creativeContextId: string;
  readonly channels: readonly ChannelPostChannel[];
  readonly updatedAt: string;
}

/** A post request's status, and the posts it produced. Mirrors `ChannelPostRequestStatusServiceModel`. */
export interface ChannelPostRequestStatus {
  readonly request: AiProposalStatus;
  /** Null until something has been written for the piece of work. */
  readonly package: ChannelPostPackage | null;
}

// ---------------------------------------------------------------------------
// Writes
// ---------------------------------------------------------------------------

/** What `POST .../channel-post-requests` takes. No prompt, no model, no workspace: all of that is the server's. */
export interface RequestChannelPostsRequest {
  readonly creativeContextId: string;
  /** In the order the posts should come back. Part of the request, so a reorder is not a retry. */
  readonly channelKeys: readonly string[];
}

/** What `PATCH .../posts/{channelKey}` takes. No count and no verdict: the server measures the body. */
export interface EditChannelPostRequest {
  readonly body: string;
  /** The revision the words were composed against, or null for the first words on a channel. */
  readonly expectedLatestRevisionId: string | null;
}

/** What `POST .../posts/{channelKey}/disposition` takes. */
export interface ChannelPostDispositionRequest {
  readonly decision: ChannelPostDecision;
  /** Required to accept or reject, and unused by the other two. */
  readonly revisionId: string | null;
}

// ---------------------------------------------------------------------------
// Stable server codes
// ---------------------------------------------------------------------------

/** `AiChannelPostsRequestErrors.TaskNotEnabled`. A deployment switch, not a client fix. */
export const CHANNEL_POSTS_NOT_ENABLED_CODE = 'ai.channelPosts.not_enabled';

/** `AiChannelPostsRequestErrors.ContextNotFound`. Also the answer for another workspace's work. */
export const CHANNEL_POSTS_CONTEXT_NOT_FOUND_CODE = 'ai.channelPostsContext.not_found';

/** `AiChannelPostsRequestErrors.ChannelRetired`: a first post cannot be written for a retired channel. */
export const CHANNEL_POSTS_CHANNEL_RETIRED_CODE = 'ai.channelPosts.channel.unprocessable';

/** `ContentErrorCodes.SocialStale`: the post moved on since the creator opened it. */
export const CHANNEL_POST_STALE_CODE = 'content.social.stale.conflict';

/** `ContentErrorCodes.SocialDecisionConflict`: nothing is awaiting that decision. */
export const CHANNEL_POST_DECISION_CONFLICT_CODE = 'content.social.decision.conflict';

/** `ContentErrorCodes.SocialSourceStale`: accepted words would be pinned to a superseded recipe version. */
export const CHANNEL_POST_SOURCE_STALE_CODE = 'content.social.source_stale.conflict';

/** `ContentErrorCodes.SocialChannelUnprocessable`. */
export const CHANNEL_POST_CHANNEL_UNPROCESSABLE_CODE = 'content.social.channel.unprocessable';

// ---------------------------------------------------------------------------
// Reading what a channel is showing
// ---------------------------------------------------------------------------

/**
 * The words a creator is looking at for one channel: the accepted ones where there are any, else the newest.
 *
 * **Not simply the latest.** A channel whose newest revision was rejected still stands at whatever was
 * accepted before it, and showing the declined words as the post would contradict the decision the creator
 * just made.
 */
export function channelPostShowing(channel: ChannelPostChannel): ChannelPostRevision {
  return channel.status === 'Rejected' && channel.accepted !== null ? channel.accepted : channel.latest;
}

/**
 * What copying this channel puts on the clipboard, given whatever the creator has unsaved.
 *
 * Precedence, and it is the whole rule: the words in the box they are editing, then the words they accepted,
 * then the newest. **A raw generation is never copied once something newer or accepted exists** — copying is
 * the moment the words leave CreatorPantry, so it is the last place that may hand over a draft nobody chose.
 */
export function channelPostCopyText(channel: ChannelPostChannel, unsavedBody: string | null): string {
  if (unsavedBody !== null && unsavedBody.trim() !== '') return unsavedBody;

  return (channel.accepted ?? channel.latest).body;
}

/** True when these are words a model wrote and the creator has not replaced or accepted them yet. */
export function isGeneratedAwaitingReview(channel: ChannelPostChannel): boolean {
  return channel.status === 'Proposed' && channel.latest.source === 'AiGenerated';
}

/**
 * The live count for a box the creator is typing in.
 *
 * **The browser's own count, and only ever for an unsaved edit.** It is `[...text].length` — code points,
 * not UTF-16 units — so an emoji counts as one rather than two while someone types. The server counts the
 * way each channel counts (an emoji is two on X, several on Threads), so a saved revision's figure is the
 * server's and this one is replaced by it the moment the edit is saved. The two can differ, which is why
 * nothing is ever refused on this number.
 */
export function channelPostCount(text: string): number {
  return [...text].length;
}

/** How a count reads against a limit: "312 of 280" and, when it is over, by how much. */
export function channelPostCountText(count: number, limit: number | null): string {
  if (limit === null) return count === 1 ? '1 character' : `${count} characters`;

  const over = count - limit;

  return over > 0 ? `${count} of ${limit} — ${over} over` : `${count} of ${limit}`;
}

// ---------------------------------------------------------------------------
// Decoders
// ---------------------------------------------------------------------------

export function decodeChannelPostRevision(value: unknown): ChannelPostRevision | null {
  if (!isRecord(value)) return null;

  const { id, revisionNumber, body, characterCount, characterLimit, aiProposalId, createdAt } = value;
  const source = decodeEnum<ChannelPostSource>(CHANNEL_POST_SOURCE_VALUES, value['source']);
  const limitStatus = decodeEnum<ChannelPostLimitStatus>(
    CHANNEL_POST_LIMIT_STATUS_VALUES,
    value['limitStatus'],
  );

  if (
    typeof id !== 'string' ||
    typeof revisionNumber !== 'number' ||
    !Number.isInteger(revisionNumber) ||
    source === null ||
    typeof body !== 'string' ||
    !isNumberOrNull(characterCount) ||
    !isNumberOrNull(characterLimit) ||
    limitStatus === null ||
    !isStringOrNull(aiProposalId) ||
    typeof createdAt !== 'string'
  ) {
    return null;
  }

  return {
    id,
    revisionNumber,
    source,
    body,
    characterCount: characterCount ?? null,
    characterLimit: characterLimit ?? null,
    limitStatus,
    aiProposalId: aiProposalId ?? null,
    createdAt,
  };
}

export function decodeChannelPostChannel(value: unknown): ChannelPostChannel | null {
  if (!isRecord(value)) return null;

  const { channelKey, updatedAt } = value;
  const status = decodeEnum<ChannelPostStatus>(CHANNEL_POST_STATUS_VALUES, value['status']);
  const latest = decodeChannelPostRevision(value['latest']);

  if (typeof channelKey !== 'string' || status === null || latest === null || typeof updatedAt !== 'string') {
    return null;
  }

  // Both nullable, and both tell "sent as null" from "the wrong shape": an explicit null is the ordinary case
  // — nothing accepted yet, so nothing to be current — while a wrong type is a contract violation.
  const rawAccepted = value['accepted'];
  const accepted = rawAccepted === null || rawAccepted === undefined ? null : decodeChannelPostRevision(rawAccepted);
  if (rawAccepted !== null && rawAccepted !== undefined && accepted === null) return null;

  const rawCurrent = value['isCurrent'];
  if (rawCurrent !== null && rawCurrent !== undefined && typeof rawCurrent !== 'boolean') return null;

  return {
    channelKey,
    status,
    latest,
    accepted,
    isCurrent: typeof rawCurrent === 'boolean' ? rawCurrent : null,
    updatedAt,
  };
}

export function decodeChannelPostPackage(value: unknown): ChannelPostPackage | null {
  if (!isRecord(value)) return null;

  const { id, creativeContextId, updatedAt } = value;
  const rawChannels = value['channels'];
  if (
    typeof id !== 'string' ||
    typeof creativeContextId !== 'string' ||
    typeof updatedAt !== 'string' ||
    !Array.isArray(rawChannels)
  ) {
    return null;
  }

  // The whole package fails on one unreadable channel. A partly decoded package would understate what has
  // been written, and a creator would be offered a fresh post for a channel that already has one.
  const channels: ChannelPostChannel[] = [];
  for (const raw of rawChannels) {
    const channel = decodeChannelPostChannel(raw);
    if (channel === null) return null;
    channels.push(channel);
  }

  return { id, creativeContextId, channels, updatedAt };
}

export function decodeChannelPostRequestStatus(value: unknown): ChannelPostRequestStatus | null {
  if (!isRecord(value)) return null;

  const request = decodeAiProposalStatus(value['request']);
  if (request === null) return null;

  const rawPackage = value['package'];
  const decoded = rawPackage === null || rawPackage === undefined ? null : decodeChannelPostPackage(rawPackage);
  if (rawPackage !== null && rawPackage !== undefined && decoded === null) return null;

  return { request, package: decoded };
}

export function encodeRequestChannelPosts(request: RequestChannelPostsRequest): Record<string, unknown> {
  return { creativeContextId: request.creativeContextId, channelKeys: [...request.channelKeys] };
}

export function encodeEditChannelPost(request: EditChannelPostRequest): Record<string, unknown> {
  return { body: request.body, expectedLatestRevisionId: request.expectedLatestRevisionId };
}

export function encodeChannelPostDisposition(request: ChannelPostDispositionRequest): Record<string, unknown> {
  return { decision: request.decision, revisionId: request.revisionId };
}
