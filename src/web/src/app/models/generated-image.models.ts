// The image-generation contracts as the browser reads them: asking for pictures (IMG-003), reading where the
// request got to and what it produced, and declining one (IMG-005/006).
//
// API shapes, not view types. Nothing here names a provider endpoint, a storage container, an object key, a
// checksum or a prompt: the routes do not carry any of them, which is the point of reading them through a typed
// client rather than a URL a response chose. The provider's and model's *names* do travel, and deliberately:
// they are the provenance of a picture a creator might publish, which media.md requires them to be able to see.
//
// Every bound mirrors a server constant by name, so a control cannot accept something the route would refuse.

import { problemBodyOf } from '../core/forms/problem-details';
import { QUOTA_EXHAUSTED_CODE } from './ai-quota.models';
import { decodeEnum, isRecord, isStringOrNull } from './recipe.models';
import { decodeRenditionSize } from './picture-rendition.models';

export type GeneratedImageOperationStatus =
  | 'Unspecified'
  | 'Requested'
  | 'Running'
  | 'Succeeded'
  | 'PartiallySucceeded'
  | 'Failed'
  | 'Cancelled';

const OPERATION_STATUS_VALUES: ReadonlySet<string> = new Set<GeneratedImageOperationStatus>([
  'Unspecified',
  'Requested',
  'Running',
  'Succeeded',
  'PartiallySucceeded',
  'Failed',
  'Cancelled',
]);

/**
 * Where one staged image stands.
 *
 * `Kept` arrives only from the DAM commit — the one write that puts a picture in the library — so it is the
 * authority on whether a picture is permanent, whoever saved it and from wherever. A surface that merely
 * *marks* a picture leaves it `Staged`, and says so rather than implying it has been filed.
 */
export type GeneratedImageStatus = 'Unspecified' | 'Staged' | 'Kept' | 'Rejected' | 'Expired';

const IMAGE_STATUS_VALUES: ReadonlySet<string> = new Set<GeneratedImageStatus>([
  'Unspecified',
  'Staged',
  'Kept',
  'Rejected',
  'Expired',
]);

/** One picture a generation produced. A row exists only once there are bytes to describe. */
export interface StagedImage {
  readonly id: string;
  /** The variant this is, counted from one by the server. */
  readonly variantIndex: number;
  readonly status: GeneratedImageStatus;
  /** Established from the returned bytes at staging, never a label a provider supplied. */
  readonly mediaType: string;
  readonly width: number;
  readonly height: number;
  readonly sizeBytes: number;
  /** The web-size copy's bytes, or null while the server has not made one (or never will). */
  readonly webSizeBytes?: number | null;
  /** The thumbnail's bytes, or null while there is none. */
  readonly thumbnailSizeBytes?: number | null;
  /** When retention may remove the bytes. A staging deadline, not a publication date. */
  readonly retentionExpiresAt: string;
  readonly createdAt: string;
}

export interface GeneratedImageOperation {
  readonly id: string;
  readonly status: GeneratedImageOperationStatus;
  /** How many pictures were asked for. */
  readonly variantCount: number;
  /** How many have landed so far, which is what makes a progress figure honest. */
  readonly stagedCount: number;
  /** What made the pictures. Null until something has. Provenance a creator is entitled to see. */
  readonly providerName: string | null;
  readonly modelName: string | null;
  /** A {@link GENERATED_IMAGE_FAILURE_SENTENCES} key, when this failed. */
  readonly failureCategory: string | null;
  /**
   * Why it failed, in the application's own words.
   *
   * Safe to render: `GeneratedImageFailureCategory` states that nothing a provider said is ever stored, because
   * a provider's error body can quote a prompt back and a prompt is private creator content (ai.md).
   */
  readonly failureSummary: string | null;
  readonly requestedAt: string;
  readonly completedAt: string | null;
}

export interface GeneratedImageOperationDetail extends GeneratedImageOperation {
  /** Ordered by variant. Shorter than {@link GeneratedImageOperation.variantCount} while one is in flight. */
  readonly images: readonly StagedImage[];
}

/**
 * True once there is nothing further to learn by asking again.
 *
 * `PartiallySucceeded` is **finished and a success**: some variants landed, and those are what the creator
 * came for. It is terminal with a caveat, not a failure.
 */
export function isGeneratedImageOperationFinished(status: GeneratedImageOperationStatus): boolean {
  return (
    status === 'Succeeded' || status === 'PartiallySucceeded' || status === 'Failed' || status === 'Cancelled'
  );
}

/** True for an operation that produced nothing at all, which is the only state with no pictures to show. */
export function isGeneratedImageOperationFailed(status: GeneratedImageOperationStatus): boolean {
  return status === 'Failed' || status === 'Cancelled';
}

/** True for a picture that can still be looked at, kept or declined. Every other state is terminal. */
export function isStagedImageActionable(status: GeneratedImageStatus): boolean {
  return status === 'Staged';
}

/**
 * Why a generation stopped, in terms a creator can act on, keyed by `GeneratedImageFailureCategory`.
 *
 * **Each sentence carries its own advice**, rather than a separate "worth retrying" set beside it. The server
 * has one of those — `GeneratedImageFailureCategory.Retryable` — and it answers a different question: whether
 * the *sweep* requeues a lapsed attempt on its own. What a creator needs to know is whether asking again is
 * likely to get a different answer, and for a refused prompt or a host with no deployment configured the
 * honest answer is no even though the button is still there to press.
 *
 * No provider, model, lease, queue or storage vocabulary: a creator has no relationship with any of them, and
 * "every worker died holding the lease" asks exactly the same thing of them as "the endpoint was unreachable".
 */
export const GENERATED_IMAGE_FAILURE_SENTENCES: Readonly<Record<string, string>> = {
  'provider-not-configured':
    'Making pictures is not switched on for this site yet, so nothing was made. Someone with access to the setup needs to turn it on.',
  'model-unidentified':
    "Whatever answered would not say what had made the picture, so we did not keep it. If you might publish a picture, you are entitled to know what made it.",
  'provider-unavailable': 'Something on our side did not answer. Asking again usually works.',
  'rate-limited': 'Too many requests at once. Waiting a moment and asking again usually works.',
  'budget-exhausted':
    "This workspace had already used today's picture allowance when this request's turn came. It comes back at midnight UTC.",
  'provider-refused':
    'This request was turned down, and asking again as it stands would be turned down too. Changing the prompt is the thing to try.',
  'invalid-response': 'Something came back, but there was no picture in it.',
  'unsupported-media-type': 'What came back was not a picture in a format we can use.',
  'corrupt-image': 'What came back started like a picture and did not hold together as one.',
  'image-too-large': 'The picture that came back is larger than we will keep.',
  'malware-detected': 'What came back did not pass our safety check, so none of it was kept.',
  storage: 'We could not put the pictures away safely, so none of them were kept. Asking again usually works.',
  'lease-abandoned': 'Something on our side kept dropping this request. Asking again usually works.',
};

/**
 * What to tell a creator about a failed generation.
 *
 * The server's own summary is preferred, because it was written about *this* operation; the category sentence
 * is the fallback. An unrecognised category still produces something — a stored row can carry a category this
 * build has not heard of, and rendering nothing would drop the only explanation there is.
 */
export function generatedImageFailureSentence(
  failureCategory: string | null,
  failureSummary: string | null,
): string {
  const summary = failureSummary?.trim();
  if (summary) return summary;

  const sentence = failureCategory === null ? undefined : GENERATED_IMAGE_FAILURE_SENTENCES[failureCategory];

  return sentence ?? 'No reason was recorded for this one.';
}

/** The body of an image-generation request. Carries no workspace, no owner and no idempotency key. */
export interface RequestGeneratedImagesRequest {
  /** The prompt as it will be sent, after any edit the creator made. */
  readonly promptText: string;
  /** What to avoid, when the creator's prompt came with a list. Untrusted text, like the prompt. */
  readonly avoidText: string | null;
  readonly variantCount: number;
  /** What the pictures are for, when the surface asking knows. Recorded on the run; it grounds nothing. */
  readonly subject?: GeneratedImageSubject | null;
  /** How good each picture is (AP.3.1). Null or absent asks for the host's default class. */
  readonly qualityClass?: GeneratedImageQualityClass | null;
}

/**
 * What a run of pictures is asked for: the recipe the work is about, the exact version it names, and the
 * piece of work itself. Each is optional, and each is one the work already holds — never something typed.
 */
export interface GeneratedImageSubject {
  readonly recipeId: string | null;
  readonly recipeVersionId: string | null;
  readonly creativeContextId: string | null;
}

/** The subject of a run for this piece of work: the work, and the recipe it names if it names one. */
export function generatedImageSubjectOf(
  creativeContextId: string | null,
  recipe: { readonly recipeId: string; readonly recipeVersionId: string | null } | null,
): GeneratedImageSubject | null {
  if (creativeContextId === null && recipe === null) return null;

  return {
    recipeId: recipe?.recipeId ?? null,
    recipeVersionId: recipe?.recipeVersionId ?? null,
    creativeContextId,
  };
}

/**
 * The wire body.
 *
 * `aiProposalId` is sent as null rather than omitted, so the field is visibly accounted for. It is deliberately
 * not populated: the server does not validate it and `GeneratedImageOperationConfiguration` pins it with a
 * restricted foreign key, so a proposal that has since been swept would fail the insert rather than earn a
 * refusal — and the pipeline's draft keeps the composition's *request* id, never a proposal id. The prompt text
 * sent is the same either way, so nothing a creator sees depends on it.
 */
export function encodeRequestGeneratedImages(request: RequestGeneratedImagesRequest): Record<string, unknown> {
  const body: Record<string, unknown> = {
    promptText: request.promptText,
    avoidText: request.avoidText,
    aiProposalId: null,
    variantCount: request.variantCount,
  };

  // Left out rather than sent null, and a version never without its recipe: the server refuses that pairing.
  const subject = request.subject ?? null;
  if (subject?.recipeId) {
    body['recipeId'] = subject.recipeId;
    if (subject.recipeVersionId) body['recipeVersionId'] = subject.recipeVersionId;
  }
  if (subject?.creativeContextId) body['creativeContextId'] = subject.creativeContextId;

  // Left out rather than sent null, like the subject: a request that names no class is the default's.
  if (request.qualityClass) body['qualityClass'] = request.qualityClass;

  return body;
}

/** `GeneratedImageInputChecks.PromptTextMaxLength`, for both the prompt and the avoid list. */
export const GENERATED_IMAGE_PROMPT_MAX_LENGTH = 4000;

/**
 * The avoid list as one piece of text, or null when there is nothing to avoid.
 *
 * Joined with `"; "`, which is how every proposal row a client reads was assembled, and cut to the column's
 * length rather than refused — a truncated avoid list still says most of what the creator meant, and a refusal
 * here would block a generation over the quieter half of the prompt.
 */
export function avoidTextFor(avoid: readonly string[]): string | null {
  const joined = avoid
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
    .join('; ');

  return joined === '' ? null : joined.slice(0, GENERATED_IMAGE_PROMPT_MAX_LENGTH);
}

/**
 * The refusals a client has to read by name.
 *
 * Every other refusal on these routes is told apart by its status, which `ProblemResults.StatusFor` guarantees
 * from the code's own suffix — `.not_found` is a 404, `.conflict` a 409, `.forbidden` a 403, `.unavailable` a
 * 503. This one shares the 400 with the feature's own validation error and means something quite different, so
 * it is worth naming here.
 */
export const IDEMPOTENCY_KEY_REQUIRED_CODE = 'idempotency.key_required';

/**
 * The account's plan does not allow the quality asked for (AP.4.3).
 *
 * Named for the same reason: it shares the 403 with "you are not allowed to make pictures in this workspace",
 * and the two have nothing to do with each other. Telling a creator on a Trial plan to ask an Owner for
 * permission would send them to someone who cannot help.
 */
export const QUALITY_CLASS_FORBIDDEN_CODE = 'media.generation.quality_class.forbidden';

/**
 * Whether a refusal is about the asking account's allowance or limits rather than a role or the edge (AP.7.1, AP.7.2):
 * a suspension, a spent allowance, a trial's pictures or its end (`ai.quota.*`), and the account's or workspace's
 * own day and in-flight caps (`limits.*`). They arrive as `403`s and `429`s like a role refusal and the edge's rate
 * limiter, which say "ask an Owner" and "slow down" — neither of which is true of these — so each is shown in the
 * server's own words. The edge's own `rate_limited` is not one of them.
 */
export function isAccountRefusalCode(code: string | null): boolean {
  return code !== null && (code.startsWith('ai.quota.') || code.startsWith('limits.'));
}

/** `LimitErrorCodes.BudgetExhausted`, mirrored: a day's cap, the plan's or the workspace's, told apart by `scope`. */
export const LIMITS_BUDGET_EXHAUSTED_CODE = 'limits.budget.exhausted';

/**
 * Which of the three limits refused a request for pictures (DL.9), so each is said as itself: the plan's day is
 * the person's across every workspace, the workspace's day is nobody's plan, and credits are neither a day.
 *
 * `cap` and `resetsAt` are the server's `limit` and `resetsAt` extensions — null when unreadable, so a sentence can
 * still be said without them rather than with a guess.
 */
export type GeneratedImageLimitRefusal =
  | { readonly kind: 'plan-day'; readonly cap: number | null; readonly resetsAt: string | null }
  | { readonly kind: 'workspace-day'; readonly cap: number | null; readonly resetsAt: string | null }
  | { readonly kind: 'credits' };

/**
 * The limit a refusal names, or null when it is none of the three — a suspension, a trial's end or its pictures, an
 * in-flight cap — which keep the server's own sentence. Matched on `code`, then `scope`, never on the status.
 */
export function decodeGeneratedImageLimitRefusal(error: unknown): GeneratedImageLimitRefusal | null {
  const body = problemBodyOf(error);
  if (body === null) return null;

  const code = body['code'];
  if (code === QUOTA_EXHAUSTED_CODE) return { kind: 'credits' };
  if (code !== LIMITS_BUDGET_EXHAUSTED_CODE) return null;

  const limit = countOf(body['limit']);
  const resetsAt = typeof body['resetsAt'] === 'string' ? body['resetsAt'] : null;

  switch (body['scope']) {
    case 'User':
      return { kind: 'plan-day', cap: limit, resetsAt };
    case 'Workspace':
      return { kind: 'workspace-day', cap: limit, resetsAt };
    default:
      return null;
  }
}

/**
 * A whole count, which the contract types as an integer or its string form.
 *
 * Both are published — the snapshot types every int32 as `["integer","string"]` with a numeric pattern — so a
 * decoder that accepted only one of them would be reading half the contract.
 */
function countOf(value: unknown): number | null {
  if (typeof value === 'number') return Number.isInteger(value) ? value : null;
  if (typeof value !== 'string' || value.trim() === '') return null;

  const parsed = Number(value);

  return Number.isInteger(parsed) ? parsed : null;
}

function nonEmptyString(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null;
}

/**
 * One picture, or null for a row this build cannot read.
 *
 * A row rather than the whole run, so one unreadable entry costs its own tile and not the contact sheet: the
 * others are still pictures the creator asked for and can act on.
 */
export function decodeStagedImage(value: unknown): StagedImage | null {
  if (!isRecord(value)) return null;

  const id = nonEmptyString(value['id']);
  const status = decodeEnum<GeneratedImageStatus>(IMAGE_STATUS_VALUES, value['status']);
  const mediaType = nonEmptyString(value['mediaType']);
  const variantIndex = countOf(value['variantIndex']);
  const width = countOf(value['width']);
  const height = countOf(value['height']);
  const sizeBytes = countOf(value['sizeBytes']);
  const retentionExpiresAt = nonEmptyString(value['retentionExpiresAt']);
  const createdAt = nonEmptyString(value['createdAt']);

  if (
    id === null ||
    status === null ||
    mediaType === null ||
    variantIndex === null ||
    width === null ||
    height === null ||
    sizeBytes === null ||
    retentionExpiresAt === null ||
    createdAt === null
  ) {
    return null;
  }

  return {
    id,
    variantIndex,
    status,
    mediaType,
    width,
    height,
    sizeBytes,
    // Optional on the wire and optional here: an older server says nothing, which reads as "no smaller copy".
    webSizeBytes: decodeRenditionSize(value['webSizeBytes']),
    thumbnailSizeBytes: decodeRenditionSize(value['thumbnailSizeBytes']),
    retentionExpiresAt,
    createdAt,
  };
}

function decodeOperationFields(value: unknown): GeneratedImageOperation | null {
  if (!isRecord(value)) return null;

  const id = nonEmptyString(value['id']);
  const status = decodeEnum<GeneratedImageOperationStatus>(OPERATION_STATUS_VALUES, value['status']);
  const variantCount = countOf(value['variantCount']);
  const stagedCount = countOf(value['stagedCount']);
  const requestedAt = nonEmptyString(value['requestedAt']);

  const { providerName, modelName, failureCategory, failureSummary, completedAt } = value;

  if (
    id === null ||
    status === null ||
    variantCount === null ||
    stagedCount === null ||
    requestedAt === null ||
    !isStringOrNull(providerName) ||
    !isStringOrNull(modelName) ||
    !isStringOrNull(failureCategory) ||
    !isStringOrNull(failureSummary) ||
    !isStringOrNull(completedAt)
  ) {
    return null;
  }

  return {
    id,
    status,
    variantCount,
    stagedCount,
    providerName,
    modelName,
    failureCategory,
    failureSummary,
    requestedAt,
    completedAt,
  };
}

/** The 202 body: the operation, with no image identities in it at all. */
export function decodeGeneratedImageOperation(value: unknown): GeneratedImageOperation | null {
  return decodeOperationFields(value);
}

/**
 * The operation plus the pictures it has produced so far.
 *
 * A missing `images` array is a shape this build cannot use and decodes to null, because an operation that
 * claims a staged count and offers no way to reach the pictures is worse than no answer. An *empty* one is
 * ordinary: nothing has landed yet.
 */
export function decodeGeneratedImageOperationDetail(value: unknown): GeneratedImageOperationDetail | null {
  const operation = decodeOperationFields(value);
  if (operation === null || !isRecord(value)) return null;

  const raw = value['images'];
  if (!Array.isArray(raw)) return null;

  const images = raw
    .map((entry) => decodeStagedImage(entry))
    .filter((image): image is StagedImage => image !== null)
    .sort((left, right) => left.variantIndex - right.variantIndex);

  return { ...operation, images };
}

/** Mirrors `GeneratedImageQuoteOutcome`: whether a request for pictures could go ahead right now (AP.2.6). */
export type GeneratedImageQuoteOutcome =
  | 'Affordable'
  | 'Exhausted'
  | 'Suspended'
  | 'Unavailable'
  | 'TrialEnded'
  /** The request would pass the plan's pictures for the day (AP.7.2). */
  | 'DailyImageCap'
  /** The request would pass the trial's pictures in all (AP.7.2). */
  | 'TrialImageCap';

const GENERATED_IMAGE_QUOTE_OUTCOME_VALUES: ReadonlySet<string> = new Set<GeneratedImageQuoteOutcome>([
  'Affordable',
  'Exhausted',
  'Suspended',
  'TrialEnded',
  'Unavailable',
  'DailyImageCap',
  'TrialImageCap',
]);

/**
 * What asking for pictures would cost and leave, before the creator asks. Mirrors
 * `GeneratedImageQuoteServiceModel`.
 *
 * <strong>Every figure is the server's, and nothing here is worked out from them.</strong> The price is the one
 * admission then holds, `remainingAfter` and `affordableVariants` are computed where the allowance lives, and
 * `leavesNearlySpent` is the server's line rather than a threshold kept in this bundle (PRICE-004, USAGE-010).
 */
export interface GeneratedImageQuote {
  readonly outcome: GeneratedImageQuoteOutcome;
  readonly unit: string | null;
  readonly variants: number;
  readonly perPicture: number | null;
  readonly total: number | null;
  readonly allowance: number | null;
  readonly remaining: number | null;
  /** What would be left of the allowance after this request; null unless it is affordable. */
  readonly remainingAfter: number | null;
  /**
   * The pack balance — credits granted beyond the allowance, spent after it and never reset (AP.5.2). Null when the
   * server sent none.
   */
  readonly packRemaining: number | null;
  /** What would be left of the pack balance after this request; null unless it is affordable. */
  readonly packRemainingAfter: number | null;
  /** How many pictures what is left would cover now, by the same test the request is admitted by. */
  readonly affordableVariants: number;
  readonly leavesNearlySpent: boolean;
  /** When the allowance comes back. Render it in `timeZoneId`, never the browser's zone. */
  readonly resetsAt: string | null;
  readonly timeZoneId: string | null;
  /** The picture cap this request would pass, on `DailyImageCap` and `TrialImageCap` only (AP.7.2). */
  readonly pictureCap: number | null;
  /** How many pictures that cap still allows, beside `pictureCap`. */
  readonly picturesLeft: number | null;
  /** When the day's cap turns over; null for the trial's, which never does. */
  readonly pictureCapResetsAt: string | null;
}

function quoteFigure(value: unknown): number | null | undefined {
  if (value === null || value === undefined) return null;
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

/**
 * Decodes a quote, or null when it is not one. A figure that is there but is not a number refuses the whole quote
 * rather than reading as missing: "120 credits" becoming "no price" would tell a creator the opposite of the server.
 */
export function decodeGeneratedImageQuote(value: unknown): GeneratedImageQuote | null {
  if (!isRecord(value)) return null;

  const outcome = decodeEnum<GeneratedImageQuoteOutcome>(GENERATED_IMAGE_QUOTE_OUTCOME_VALUES, value['outcome']);
  const variants = value['variants'];
  const affordableVariants = value['affordableVariants'];
  const leavesNearlySpent = value['leavesNearlySpent'];
  const figures = {
    perPicture: quoteFigure(value['perPicture']),
    total: quoteFigure(value['total']),
    allowance: quoteFigure(value['allowance']),
    remaining: quoteFigure(value['remaining']),
    remainingAfter: quoteFigure(value['remainingAfter']),
    packRemaining: quoteFigure(value['packRemaining']),
    packRemainingAfter: quoteFigure(value['packRemainingAfter']),
    pictureCap: quoteFigure(value['pictureCap']),
    picturesLeft: quoteFigure(value['picturesLeft']),
  };

  if (
    outcome === null ||
    typeof variants !== 'number' ||
    typeof affordableVariants !== 'number' ||
    typeof leavesNearlySpent !== 'boolean' ||
    !isStringOrNull(value['unit']) ||
    !isStringOrNull(value['resetsAt']) ||
    !isStringOrNull(value['timeZoneId']) ||
    !isStringOrNull(value['pictureCapResetsAt'] ?? null) ||
    Object.values(figures).some((figure) => figure === undefined)
  ) {
    return null;
  }

  return {
    outcome,
    unit: (value['unit'] as string | null) ?? null,
    variants,
    perPicture: figures.perPicture ?? null,
    total: figures.total ?? null,
    allowance: figures.allowance ?? null,
    remaining: figures.remaining ?? null,
    remainingAfter: figures.remainingAfter ?? null,
    packRemaining: figures.packRemaining ?? null,
    packRemainingAfter: figures.packRemainingAfter ?? null,
    affordableVariants,
    leavesNearlySpent,
    resetsAt: (value['resetsAt'] as string | null) ?? null,
    timeZoneId: (value['timeZoneId'] as string | null) ?? null,
    pictureCap: figures.pictureCap ?? null,
    picturesLeft: figures.picturesLeft ?? null,
    pictureCapResetsAt: (value['pictureCapResetsAt'] as string | null | undefined) ?? null,
  };
}

/** Mirrors `GeneratedImageQualityClass`: how good a picture is asked to be, never which model makes it (AP.3.1). */
export type GeneratedImageQualityClass = 'Draft' | 'Standard' | 'High' | 'High2K' | 'High4K';

/** Exported so the landing page's catalogue decoder reads the same set, not a second copy of it (AP.4.5). */
export const GENERATED_IMAGE_QUALITY_CLASS_VALUES: ReadonlySet<string> = new Set<GeneratedImageQualityClass>([
  'Draft',
  'Standard',
  'High',
  'High2K',
  'High4K',
]);

/** One class this host offers and what one picture of it costs, in the list's `unit`. */
export interface GeneratedImageQualityClassPrice {
  readonly qualityClass: GeneratedImageQualityClass;
  /** The rate a request at this class is charged per picture — the server's figure, shown and never worked with. */
  readonly perPicture: number;
  readonly isDefault: boolean;
  /**
   * Whether this account's plan allows a picture of this class (AP.4.3). False is locked: still shown, still
   * priced, and refused by the server if asked for anyway.
   */
  readonly allowed: boolean;
  /** The key of the cheapest plan that would allow it; null when it is allowed, or when no plan allows it. */
  readonly requiresPlan: string | null;
  /** That plan's name, to say in the words a reader sees. Null under the same conditions. */
  readonly requiresPlanName: string | null;
}

/**
 * The picture qualities a creator may ask for here, each with its price (AP.3.1, PRICE-013). Mirrors
 * `GeneratedImageQualityClassesServiceModel`.
 *
 * A price list, not the creator's balance: what one request would cost and leave is the quote's.
 */
export interface GeneratedImageQualityClasses {
  /** What a request naming no class is made at; null when the host offers no classes. */
  readonly defaultClass: GeneratedImageQualityClass | null;
  readonly unit: string;
  /** In the server's order, cheapest first. Empty on a host that offers no choice. */
  readonly classes: readonly GeneratedImageQualityClassPrice[];
}

/**
 * Decodes the price list, or null when it is not one.
 *
 * A row naming a class this bundle does not know is dropped rather than refusing the list: a class added on the
 * server is then simply not offered here, which is safe, where refusing would hide every class. A price that is
 * there but not a number refuses the whole list, as the quote's figures do — a creator shown no price for one
 * class would read it as free. A missing or non-boolean `allowed` refuses it for the same reason (AP.4.3): a
 * locked class drawn as an open one offers a choice the server will refuse.
 */
export function decodeGeneratedImageQualityClasses(value: unknown): GeneratedImageQualityClasses | null {
  if (!isRecord(value) || typeof value['unit'] !== 'string' || !Array.isArray(value['classes'])) return null;

  const rawDefault = value['defaultClass'];
  if (rawDefault !== null && typeof rawDefault !== 'string') return null;

  const classes: GeneratedImageQualityClassPrice[] = [];

  for (const row of value['classes'] as unknown[]) {
    if (!isRecord(row)) return null;

    const perPicture = row['perPicture'];
    if (typeof perPicture !== 'number' || !Number.isFinite(perPicture) || typeof row['isDefault'] !== 'boolean') {
      return null;
    }

    // Strictly, like the price beside it: a row whose `allowed` the page cannot read is a tile it cannot say is
    // locked, and drawing it open would offer a choice the server refuses. The plan names may be absent — an
    // allowed class has none — but must be strings when they are there.
    const allowed = row['allowed'];
    const requiresPlan = row['requiresPlan'] ?? null;
    const requiresPlanName = row['requiresPlanName'] ?? null;
    if (
      typeof allowed !== 'boolean' ||
      (requiresPlan !== null && typeof requiresPlan !== 'string') ||
      (requiresPlanName !== null && typeof requiresPlanName !== 'string')
    ) {
      return null;
    }

    const qualityClass = decodeEnum<GeneratedImageQualityClass>(GENERATED_IMAGE_QUALITY_CLASS_VALUES, row['qualityClass']);
    if (qualityClass === null) continue;

    classes.push({ qualityClass, perPicture, isDefault: row['isDefault'], allowed, requiresPlan, requiresPlanName });
  }

  const defaultClass =
    rawDefault === null
      ? null
      : decodeEnum<GeneratedImageQualityClass>(GENERATED_IMAGE_QUALITY_CLASS_VALUES, rawDefault);

  return {
    // Only a default that is offered here and allowed: one this bundle cannot show, or one the plan locks, is no
    // default to pick. The server refuses a catalogue whose plans do not allow the default class, so the second
    // half of that is a guard rather than a state anyone should see.
    defaultClass: classes.some((row) => row.qualityClass === defaultClass && row.allowed) ? defaultClass : null,
    unit: value['unit'],
    classes,
  };
}
