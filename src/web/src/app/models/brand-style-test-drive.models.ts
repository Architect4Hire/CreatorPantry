import {
  AI_FAILURE_CATEGORY_VALUES,
  AI_OPERATION_STATUS_VALUES,
  AI_WARNING_KIND_VALUES,
  AiFailureCategory,
  AiOperationStatus,
  AiWarningKind,
} from './ai-proposal.models';
import { decodeEnum, isRecord } from './recipe.models';

/**
 * 11A.24's read-only style test drive, as the browser reads it.
 *
 * Mirrors `BrandStyleTestDriveServiceModel` and what hangs off it. The server pairs the six stored rows into
 * three comparisons before they reach here, so nothing in this file works out which column a sample belongs
 * to — that is the server's reading of its own rows, not a guess the client makes about them.
 */

/** The longest subject the server accepts. Mirrors `AiPolicy.StyleSampleSubjectMaxLength`. */
export const STYLE_TEST_DRIVE_SUBJECT_MAX = 120;

/** What a client sends to ask for one. The version is required: there is no default here. */
export interface RequestBrandStyleTestDrive {
  readonly guideId: string;
  readonly versionNumber: number;
  /** Optional, short. Omitted uses the platform's own example, which the reply names back. */
  readonly subject?: string;
}

export function encodeRequestBrandStyleTestDrive(request: RequestBrandStyleTestDrive): Record<string, unknown> {
  return {
    guideId: request.guideId,
    versionNumber: request.versionNumber,
    // Omitted rather than sent empty, so the stored request says what the creator chose.
    ...(request.subject && request.subject.trim() !== '' ? { subject: request.subject.trim() } : {}),
  };
}

/** One sample, written both ways. */
export interface BrandStyleSamplePair {
  /** `blogIntro`, `socialCaption` or `imagePrompt` — the server's own names for the three. */
  readonly sample: string;
  /** Written by a call that received no brand context at all. A sample in its own right, not a "before". */
  readonly withoutGuide: string;
  readonly withGuide: string;
  readonly notes: readonly BrandStyleSampleNote[];
}

/** A caution the model attached to one sample, saying which column it is about. */
export interface BrandStyleSampleNote {
  readonly withGuide: boolean;
  readonly kind: AiWarningKind;
  readonly message: string;
}

/** One rule the guide asks for, in the creator's own words. Never a prompt fragment. */
export interface BrandStyleAppliedRule {
  readonly label: string;
  readonly summary: string;
}

/** One passage of the creator's own writing that was sent with the guided call. No passage text. */
export interface BrandStyleCitation {
  readonly documentId: string;
  /** Null when the example has since been removed. The citation still names what was used. */
  readonly title: string | null;
  readonly documentVersionNumber: number;
  readonly passageId: string;
  readonly ordinal: number;
}

/** Something the server has to say about the grounding as a whole, or the model about the whole answer. */
export interface BrandStyleTestDriveNotice {
  readonly kind: AiWarningKind;
  readonly message: string;
}

export interface BrandStyleTestDriveComparison {
  readonly subject: string;
  readonly guideId: string;
  readonly guideVersionNumber: number;
  readonly guideVersionWasActive: boolean;
  readonly modelName: string;
  readonly promptTemplateVersion: string;
  readonly generatedAt: string;
  readonly samples: readonly BrandStyleSamplePair[];
  readonly appliedRules: readonly BrandStyleAppliedRule[];
  readonly citations: readonly BrandStyleCitation[];
  readonly notices: readonly BrandStyleTestDriveNotice[];
  /**
   * True when the guide, the brand profile or the examples have moved since the samples were written, or when
   * they cannot be read at all. The samples are unaffected; what may be out of step is `appliedRules`.
   */
  readonly groundingChangedSince: boolean;
}

export interface BrandStyleTestDrive {
  readonly requestId: string;
  readonly status: AiOperationStatus;
  readonly failureCategory: AiFailureCategory | null;
  readonly requestedAt: string;
  readonly statusChangedAt: string;
  /** Null until the work has produced one, and when it failed. */
  readonly comparison: BrandStyleTestDriveComparison | null;
}

export function decodeBrandStyleTestDrive(value: unknown): BrandStyleTestDrive | null {
  if (!isRecord(value)) return null;

  const { requestId, requestedAt, statusChangedAt } = value;
  const status = decodeEnum<AiOperationStatus>(AI_OPERATION_STATUS_VALUES, value['status']);

  if (
    typeof requestId !== 'string' ||
    status === null ||
    typeof requestedAt !== 'string' ||
    typeof statusChangedAt !== 'string'
  ) {
    return null;
  }

  // Both nullable, and both distinguish "sent as null" from "not a category/comparison at all": an explicit
  // null is the ordinary case, while a wrong type is a contract violation that must fail rather than read as
  // absent. The same rule `decodeAiProposalStatus` applies.
  const rawFailure = value['failureCategory'];
  const failureCategory =
    rawFailure === null || rawFailure === undefined
      ? null
      : decodeEnum<AiFailureCategory>(AI_FAILURE_CATEGORY_VALUES, rawFailure);
  if (rawFailure !== null && rawFailure !== undefined && failureCategory === null) return null;

  const rawComparison = value['comparison'];
  const comparison =
    rawComparison === null || rawComparison === undefined ? null : decodeComparison(rawComparison);
  if (rawComparison !== null && rawComparison !== undefined && comparison === null) return null;

  return { requestId, status, failureCategory, requestedAt, statusChangedAt, comparison };
}

function decodeComparison(value: unknown): BrandStyleTestDriveComparison | null {
  if (!isRecord(value)) return null;

  const { subject, guideId, guideVersionNumber, modelName, promptTemplateVersion, generatedAt } = value;

  if (
    typeof subject !== 'string' ||
    typeof guideId !== 'string' ||
    typeof guideVersionNumber !== 'number' ||
    typeof value['guideVersionWasActive'] !== 'boolean' ||
    typeof modelName !== 'string' ||
    typeof promptTemplateVersion !== 'string' ||
    typeof generatedAt !== 'string' ||
    typeof value['groundingChangedSince'] !== 'boolean'
  ) {
    return null;
  }

  const samples = decodeAll(value['samples'], decodeSamplePair);
  const appliedRules = decodeAll(value['appliedRules'], decodeAppliedRule);
  const citations = decodeAll(value['citations'], decodeCitation);
  const notices = decodeAll(value['notices'], decodeNotice);

  if (samples === null || appliedRules === null || citations === null || notices === null) return null;

  return {
    subject,
    guideId,
    guideVersionNumber,
    guideVersionWasActive: value['guideVersionWasActive'],
    modelName,
    promptTemplateVersion,
    generatedAt,
    samples,
    appliedRules,
    citations,
    notices,
    groundingChangedSince: value['groundingChangedSince'],
  };
}

function decodeSamplePair(value: unknown): BrandStyleSamplePair | null {
  if (!isRecord(value)) return null;

  const { sample, withoutGuide, withGuide } = value;
  const notes = decodeAll(value['notes'], decodeNote);

  return typeof sample === 'string' &&
    typeof withoutGuide === 'string' &&
    typeof withGuide === 'string' &&
    notes !== null
    ? { sample, withoutGuide, withGuide, notes }
    : null;
}

function decodeNote(value: unknown): BrandStyleSampleNote | null {
  if (!isRecord(value)) return null;

  const kind = decodeEnum<AiWarningKind>(AI_WARNING_KIND_VALUES, value['kind']);

  return kind !== null && typeof value['withGuide'] === 'boolean' && typeof value['message'] === 'string'
    ? { withGuide: value['withGuide'], kind, message: value['message'] }
    : null;
}

function decodeAppliedRule(value: unknown): BrandStyleAppliedRule | null {
  if (!isRecord(value)) return null;

  const { label, summary } = value;

  return typeof label === 'string' && typeof summary === 'string' ? { label, summary } : null;
}

function decodeCitation(value: unknown): BrandStyleCitation | null {
  if (!isRecord(value)) return null;

  const { documentId, title, documentVersionNumber, passageId, ordinal } = value;

  return typeof documentId === 'string' &&
    (title === null || title === undefined || typeof title === 'string') &&
    typeof documentVersionNumber === 'number' &&
    typeof passageId === 'string' &&
    typeof ordinal === 'number'
    ? { documentId, title: typeof title === 'string' ? title : null, documentVersionNumber, passageId, ordinal }
    : null;
}

function decodeNotice(value: unknown): BrandStyleTestDriveNotice | null {
  if (!isRecord(value)) return null;

  const kind = decodeEnum<AiWarningKind>(AI_WARNING_KIND_VALUES, value['kind']);

  return kind !== null && typeof value['message'] === 'string' ? { kind, message: value['message'] } : null;
}

/**
 * Every element or nothing.
 *
 * A partly-decoded comparison would be a column with a sample missing from it, which reads as "the model
 * wrote nothing here" — so one unreadable element fails the whole payload, as `decodeArray` does for a
 * proposal. An absent array is an empty one: the server omits nothing, but a reply that did is readable.
 */
function decodeAll<T>(value: unknown, decode: (element: unknown) => T | null): readonly T[] | null {
  if (value === null || value === undefined) return [];
  if (!Array.isArray(value)) return null;

  const decoded: T[] = [];

  for (const element of value) {
    const item = decode(element);
    if (item === null) return null;
    decoded.push(item);
  }

  return decoded;
}
