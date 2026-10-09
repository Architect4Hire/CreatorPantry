// The image-generation contracts as the browser reads them: asking for pictures (IMG-003), reading where the
// request got to and what it produced, and declining one (IMG-005/006).
//
// API shapes, not view types. Nothing here names a provider endpoint, a storage container, an object key, a
// checksum or a prompt: the routes do not carry any of them, which is the point of reading them through a typed
// client rather than a URL a response chose. The provider's and model's *names* do travel, and deliberately:
// they are the provenance of a picture a creator might publish, which media.md requires them to be able to see.
//
// Every bound mirrors a server constant by name, so a control cannot accept something the route would refuse.

import { decodeEnum, isRecord, isStringOrNull } from './recipe.models';

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
  return {
    promptText: request.promptText,
    avoidText: request.avoidText,
    aiProposalId: null,
    variantCount: request.variantCount,
  };
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
 * The one refusal a client has to read by name.
 *
 * Every other refusal on these routes is told apart by its status, which `ProblemResults.StatusFor` guarantees
 * from the code's own suffix — `.not_found` is a 404, `.conflict` a 409, `.forbidden` a 403, `.unavailable` a
 * 503. This one shares the 400 with the feature's own validation error and means something quite different, so
 * it is the only code worth naming here.
 */
export const IDEMPOTENCY_KEY_REQUIRED_CODE = 'idempotency.key_required';

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

  return { id, variantIndex, status, mediaType, width, height, sizeBytes, retentionExpiresAt, createdAt };
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
