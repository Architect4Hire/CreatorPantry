// Wire models for the long-form document seam (LF.3.3, LF.3.4):
// GET  /api/v1/workspaces/{workspaceSlug}/long-form-drafts/{draftId}/document,
// POST .../{draftId}/acceptance,
// POST .../{draftId}/sections/{sectionId}/writing-requests and .../{draftId}/section-writing-batches,
// PATCH .../{draftId}/sections/{sectionId} and POST .../{draftId}/sections/{sectionId}/settlement.
// Mirrors CreatorPantry.Domain/Modules/Content/Managers/LongFormDocumentModels.cs and LongFormSectionModels.cs,
// and Modules/Ai/Managers/AiLongFormSectionRequestModels.cs — do not add fields the backend does not send.
//
// The enums these shapes use live in `long-form-draft.models.ts`, where the backend's `LongFormWireMirrorTests`
// reads them.
//
// Nothing here decides what is wrong with a document. `needsConfirmation` is the server's list; this file
// carries it, and words it for a creator.

import {
  AI_FAILURE_CATEGORY_VALUES,
  AI_OPERATION_STATUS_VALUES,
  AI_WARNING_KIND_VALUES,
  AiFailureCategory,
  AiOperationStatus,
  AiProposalStatus,
  AiWarningKind,
  decodeAiProposalStatus,
} from './ai-proposal.models';
import {
  LONG_FORM_ACCEPTANCE_ISSUE_VALUES,
  LONG_FORM_DRAFT_STATUS_VALUES,
  LONG_FORM_FINDING_KIND_VALUES,
  LONG_FORM_KIND_VALUES,
  LONG_FORM_REVISION_SOURCE_VALUES,
  LongFormFraming,
  LongFormItemSourceState,
  decodeLongFormFraming,
  decodeLongFormItemSourceState,
  LONG_FORM_SECTION_REQUEST_OUTCOME_VALUES,
  LONG_FORM_SECTION_ROLE_VALUES,
  LONG_FORM_WORD_TARGET_STATUS_VALUES,
  LongFormAcceptanceIssue,
  LongFormDraftStatus,
  LongFormFindingKind,
  LongFormKind,
  LongFormRevisionSource,
  LongFormSectionRequestOutcome,
  LongFormSectionRole,
  LongFormWordTargetStatus,
} from './long-form-draft.models';
import { decodeEnum, isRecord, isStringOrNull } from './recipe.models';

// ---------------------------------------------------------------------------
// ServiceModels
// ---------------------------------------------------------------------------

/**
 * Mirrors `LongFormSectionFinding`: one thing the claim scanner found in a section's text as it reads now
 * (LF.3.2), whoever wrote it. A caution; never a reason the text was not saved.
 */
export interface LongFormSectionFinding {
  readonly code: string;
  readonly kind: LongFormFindingKind;
  readonly message: string;
}

/** Mirrors `LongFormAssembledSectionServiceModel`. */
export interface LongFormDocumentSection {
  readonly id: string;
  readonly role: LongFormSectionRole;
  readonly heading: string;
  readonly intent: string;
  /** The section's text, or null when it has not been written. */
  readonly body: string | null;
  readonly isWritten: boolean;
  readonly settled: boolean;
  readonly characterCount: number;
  readonly overCeiling: boolean;
  /** Whether the text as it reads now was generated or is the creator's own. Null when unwritten. */
  readonly writtenBy: LongFormRevisionSource | null;
  readonly aiProposalId: string | null;
  /** The parts of copy the creator had already accepted that this text was written from (B-36). */
  readonly reusedEditorialParts: readonly string[];
  /** What the claim scanner found in the text as it reads now. Empty when nothing, and when unwritten. */
  readonly findings: readonly LongFormSectionFinding[];
  /** For a newsletter item, the creative-context reference of the recipe it covers. */
  readonly sourceReferenceId: string | null;
  /** For a newsletter item, whether the piece of work still names that reference. Null on every other section. */
  readonly sourceState: LongFormItemSourceState | null;
}

/** Mirrors `LongFormAcceptanceIssueServiceModel`. */
export interface LongFormAcceptanceIssueDetail {
  readonly issue: LongFormAcceptanceIssue;
  /** In plan order. Empty when the issue is about the document as a whole. */
  readonly sectionIds: readonly string[];
}

/** Mirrors `LongFormAssembledDocumentServiceModel`. */
export interface LongFormAssembledDocument {
  readonly draftId: string;
  readonly creativeContextId: string;
  readonly kind: LongFormKind;
  readonly status: LongFormDraftStatus;
  /** Null when the draft has no plan yet. */
  readonly revisionNumber: number | null;
  /** Sent back as `expectedConcurrencyToken` on the next write. */
  readonly concurrencyToken: string;
  /** Every written section in plan order. The same revision always gives the same text. */
  readonly markdown: string;
  readonly wordCount: number;
  readonly characterCount: number;
  readonly targetWords: { readonly min: number; readonly max: number };
  readonly wordTarget: LongFormWordTargetStatus;
  readonly sectionMaxCharacters: number;
  readonly documentMaxCharacters: number;
  readonly documentOverCeiling: boolean;
  readonly sections: readonly LongFormDocumentSection[];
  /** What an acceptance would have to confirm. Empty when the document can be accepted as it stands. */
  readonly needsConfirmation: readonly LongFormAcceptanceIssueDetail[];
  readonly acceptedRevisionNumber: number | null;
  readonly acceptedAt: string | null;
  /** A newsletter issue's subject line and note, or null when it has neither. Beside the Markdown, not in it. */
  readonly framing: LongFormFraming | null;
  /** The items, in plan order, whose recipe the piece of work no longer names. */
  readonly unresolvedItems: readonly string[];
  /**
   * Which sources the accepted piece was written from have changed since (LF.6.1): `RecipeChanged`,
   * `VoiceChanged`. Empty unless the status is `NeedsReview`. Plain strings rather than a closed union, so a
   * reason the server learns later still reads as "something changed" instead of failing the whole document.
   */
  readonly staleReasons: readonly string[];
  /** When the accepted piece was first found out of step; null unless `NeedsReview`. */
  readonly staleSince: string | null;
}

/** Mirrors `LongFormSectionWarningServiceModel`. */
export interface LongFormSectionWarning {
  readonly sectionId: string;
  readonly kind: AiWarningKind;
  /** The server's code for the finding, or null for a caution the model raised about its own work. */
  readonly code: string | null;
  readonly message: string;
  readonly fromModel: boolean;
}

/** Mirrors `LongFormSectionRequestSummaryServiceModel`: the latest request to write one section. */
export interface LongFormSectionRequestSummary {
  readonly sectionId: string;
  readonly requestId: string;
  readonly status: AiOperationStatus;
  readonly failureCategory: AiFailureCategory | null;
  readonly outcome: LongFormSectionRequestOutcome;
  readonly requestedAt: string;
}

/** Mirrors `LongFormDocumentReviewServiceModel`. */
export interface LongFormDocumentReview {
  readonly document: LongFormAssembledDocument;
  readonly warnings: readonly LongFormSectionWarning[];
  readonly requests: readonly LongFormSectionRequestSummary[];
}

/** One section and the request queued to write it. Mirrors `LongFormSectionWritingRequestServiceModel`. */
export interface LongFormSectionWritingRequest {
  readonly sectionId: string;
  readonly request: AiProposalStatus;
}

// ---------------------------------------------------------------------------
// ViewModels
// ---------------------------------------------------------------------------

/** Mirrors `RequestLongFormSectionViewModel`. There is no workspace, draft or section field. */
export interface RequestLongFormSectionRequest {
  readonly revisionNumber: number;
  readonly instruction?: string | null;
}

/** Mirrors `EditLongFormSectionViewModel`. A blank body makes the section not yet written again. */
export interface EditLongFormSectionRequest {
  readonly expectedConcurrencyToken: string;
  readonly body: string;
}

/** Mirrors `SettleLongFormSectionViewModel`. */
export interface SettleLongFormSectionRequest {
  readonly expectedConcurrencyToken: string;
  readonly settled: boolean;
}

/** Mirrors `AcceptLongFormDocumentViewModel`. */
export interface AcceptLongFormDocumentRequest {
  readonly expectedConcurrencyToken: string;
  readonly revisionNumber: number;
  /** Every issue the creator was shown and accepts anyway. */
  readonly confirmed: readonly LongFormAcceptanceIssue[];
}

// ---------------------------------------------------------------------------
// Stable codes and limits
// ---------------------------------------------------------------------------

export const LONG_FORM_SECTION_NOT_ENABLED_CODE = 'ai.longFormSection.not_enabled';
export const LONG_FORM_DRAFT_NOT_FOUND_CODE = 'content.long_form.draft.not_found';
export const LONG_FORM_SECTION_NOT_FOUND_CODE = 'content.long_form.section.not_found';
export const LONG_FORM_REVISION_STALE_CODE = 'content.long_form.revision.stale.conflict';
export const LONG_FORM_SECTION_SETTLED_CODE = 'content.long_form.section.settled.conflict';
export const LONG_FORM_DRAFT_ACCEPTED_CODE = 'content.long_form.draft.accepted.conflict';
export const LONG_FORM_ACCEPTANCE_CONFIRMATION_REQUIRED_CODE =
  'content.long_form.acceptance.confirmation_required.unprocessable';

/** Mirrors `AiPolicy.LongFormSectionInstructionMaxLength`. */
export const LONG_FORM_INSTRUCTION_MAX_LENGTH = 500;

/** What each part of accepted copy is called to a creator. An unknown part is shown as it is named. */
const EDITORIAL_PART_LABELS: Readonly<Record<string, string>> = {
  headnote: 'headnote',
  introduction: 'introduction',
  tips: 'tips',
  substitutions: 'substitutions',
  storageReheating: 'storage and reheating notes',
  faq: 'questions and answers',
  cta: 'call to action',
};

export function longFormEditorialPartLabel(part: string): string {
  return EDITORIAL_PART_LABELS[part] ?? part;
}

/** Whether a document's sections may still be written, edited or settled. */
export function longFormDocumentIsOpen(document: LongFormAssembledDocument): boolean {
  return document.status === 'Outlined' || document.status === 'Writing';
}

// ---------------------------------------------------------------------------
// Decoders
// ---------------------------------------------------------------------------

function decodeStringArray(value: unknown): readonly string[] | null {
  return Array.isArray(value) && value.every((item) => typeof item === 'string') ? (value as string[]) : null;
}

export function decodeLongFormDocumentSection(value: unknown): LongFormDocumentSection | null {
  if (!isRecord(value)) return null;

  const { id, heading, intent, isWritten, settled, characterCount, overCeiling } = value;
  const role = decodeEnum<LongFormSectionRole>(LONG_FORM_SECTION_ROLE_VALUES, value['role']);
  const body = value['body'] ?? null;
  const rawWrittenBy = value['writtenBy'] ?? null;
  const writtenBy =
    rawWrittenBy === null ? null : decodeEnum<LongFormRevisionSource>(LONG_FORM_REVISION_SOURCE_VALUES, rawWrittenBy);
  const aiProposalId = value['aiProposalId'] ?? null;
  const reusedEditorialParts = decodeStringArray(value['reusedEditorialParts'] ?? []);
  const findings = decodeFindings(value['findings'] ?? []);
  const sourceReferenceId = value['sourceReferenceId'] ?? null;
  const sourceState = decodeLongFormItemSourceState(value['sourceState']);

  if (
    typeof id !== 'string' ||
    role === null ||
    typeof heading !== 'string' ||
    typeof intent !== 'string' ||
    !isStringOrNull(body) ||
    typeof isWritten !== 'boolean' ||
    typeof settled !== 'boolean' ||
    typeof characterCount !== 'number' ||
    typeof overCeiling !== 'boolean' ||
    (rawWrittenBy !== null && writtenBy === null) ||
    !isStringOrNull(aiProposalId) ||
    reusedEditorialParts === null ||
    findings === null ||
    !isStringOrNull(sourceReferenceId) ||
    sourceState === undefined
  ) {
    return null;
  }

  return {
    id,
    role,
    heading,
    intent,
    body,
    isWritten,
    settled,
    characterCount,
    overCeiling,
    writtenBy,
    aiProposalId,
    reusedEditorialParts,
    findings,
    sourceReferenceId,
    sourceState,
  };
}

/**
 * A section's findings, or null when any one of them cannot be read. Not dropped one by one: a section shown
 * without one of its cautions reads as cleaner than it is.
 */
function decodeFindings(value: unknown): readonly LongFormSectionFinding[] | null {
  if (!Array.isArray(value)) return null;

  const findings: LongFormSectionFinding[] = [];
  for (const raw of value) {
    if (!isRecord(raw)) return null;

    const kind = decodeEnum<LongFormFindingKind>(LONG_FORM_FINDING_KIND_VALUES, raw['kind']);
    if (typeof raw['code'] !== 'string' || kind === null || typeof raw['message'] !== 'string') return null;

    findings.push({ code: raw['code'], kind, message: raw['message'] });
  }

  return findings;
}

export function decodeLongFormAssembledDocument(value: unknown): LongFormAssembledDocument | null {
  if (!isRecord(value)) return null;

  const {
    draftId,
    creativeContextId,
    concurrencyToken,
    markdown,
    wordCount,
    characterCount,
    sectionMaxCharacters,
    documentMaxCharacters,
    documentOverCeiling,
  } = value;
  const kind = decodeEnum<LongFormKind>(LONG_FORM_KIND_VALUES, value['kind']);
  const status = decodeEnum<LongFormDraftStatus>(LONG_FORM_DRAFT_STATUS_VALUES, value['status']);
  const wordTarget = decodeEnum<LongFormWordTargetStatus>(LONG_FORM_WORD_TARGET_STATUS_VALUES, value['wordTarget']);
  const revisionNumber = value['revisionNumber'] ?? null;
  const acceptedRevisionNumber = value['acceptedRevisionNumber'] ?? null;
  const acceptedAt = value['acceptedAt'] ?? null;
  const target = value['targetWords'];
  const rawSections = value['sections'];
  const rawIssues = value['needsConfirmation'];

  if (
    typeof draftId !== 'string' ||
    typeof creativeContextId !== 'string' ||
    kind === null ||
    status === null ||
    wordTarget === null ||
    (revisionNumber !== null && typeof revisionNumber !== 'number') ||
    typeof concurrencyToken !== 'string' ||
    concurrencyToken === '' ||
    typeof markdown !== 'string' ||
    typeof wordCount !== 'number' ||
    typeof characterCount !== 'number' ||
    typeof sectionMaxCharacters !== 'number' ||
    typeof documentMaxCharacters !== 'number' ||
    typeof documentOverCeiling !== 'boolean' ||
    !isRecord(target) ||
    typeof target['min'] !== 'number' ||
    typeof target['max'] !== 'number' ||
    (acceptedRevisionNumber !== null && typeof acceptedRevisionNumber !== 'number') ||
    !isStringOrNull(acceptedAt) ||
    !Array.isArray(rawSections) ||
    !Array.isArray(rawIssues)
  ) {
    return null;
  }

  // The whole document fails on one unreadable section: shown with a section missing it would read as whole,
  // and accepting it would accept something the creator was never shown.
  const sections: LongFormDocumentSection[] = [];
  for (const raw of rawSections) {
    const section = decodeLongFormDocumentSection(raw);
    if (section === null) return null;
    sections.push(section);
  }

  // And on one unreadable issue, for the mirror of that reason: an issue left out is one never confirmed.
  const needsConfirmation: LongFormAcceptanceIssueDetail[] = [];
  for (const raw of rawIssues) {
    if (!isRecord(raw)) return null;

    const issue = decodeEnum<LongFormAcceptanceIssue>(LONG_FORM_ACCEPTANCE_ISSUE_VALUES, raw['issue']);
    const sectionIds = decodeStringArray(raw['sectionIds']);
    if (issue === null || sectionIds === null) return null;

    needsConfirmation.push({ issue, sectionIds });
  }

  const framing = decodeLongFormFraming(value['framing']);
  const unresolvedItems = decodeStringArray(value['unresolvedItems'] ?? []);
  if (framing === undefined || unresolvedItems === null) return null;

  // Absent on a reply from before LF.6.1, which had nothing to say: read as nothing moved.
  const staleReasons = decodeStringArray(value['staleReasons'] ?? []);
  const staleSince = value['staleSince'] ?? null;
  if (staleReasons === null || !isStringOrNull(staleSince)) return null;

  return {
    draftId,
    creativeContextId,
    kind,
    status,
    revisionNumber,
    concurrencyToken,
    markdown,
    wordCount,
    characterCount,
    targetWords: { min: target['min'], max: target['max'] },
    wordTarget,
    sectionMaxCharacters,
    documentMaxCharacters,
    documentOverCeiling,
    sections,
    needsConfirmation,
    acceptedRevisionNumber,
    acceptedAt,
    framing,
    unresolvedItems,
    staleReasons,
    staleSince,
  };
}

function decodeWarning(value: unknown): LongFormSectionWarning | null {
  if (!isRecord(value)) return null;

  const { sectionId, message, fromModel } = value;
  const kind = decodeEnum<AiWarningKind>(AI_WARNING_KIND_VALUES, value['kind']);
  const code = value['code'] ?? null;

  if (
    typeof sectionId !== 'string' ||
    kind === null ||
    !isStringOrNull(code) ||
    typeof message !== 'string' ||
    typeof fromModel !== 'boolean'
  ) {
    return null;
  }

  return { sectionId, kind, code, message, fromModel };
}

function decodeRequestSummary(value: unknown): LongFormSectionRequestSummary | null {
  if (!isRecord(value)) return null;

  const { sectionId, requestId, requestedAt } = value;
  const status = decodeEnum<AiOperationStatus>(AI_OPERATION_STATUS_VALUES, value['status']);
  const outcome = decodeEnum<LongFormSectionRequestOutcome>(LONG_FORM_SECTION_REQUEST_OUTCOME_VALUES, value['outcome']);
  const rawFailure = value['failureCategory'] ?? null;
  const failureCategory =
    rawFailure === null ? null : decodeEnum<AiFailureCategory>(AI_FAILURE_CATEGORY_VALUES, rawFailure);

  if (
    typeof sectionId !== 'string' ||
    typeof requestId !== 'string' ||
    status === null ||
    outcome === null ||
    (rawFailure !== null && failureCategory === null) ||
    typeof requestedAt !== 'string'
  ) {
    return null;
  }

  return { sectionId, requestId, status, failureCategory, outcome, requestedAt };
}

export function decodeLongFormDocumentReview(value: unknown): LongFormDocumentReview | null {
  if (!isRecord(value) || !Array.isArray(value['warnings']) || !Array.isArray(value['requests'])) return null;

  const document = decodeLongFormAssembledDocument(value['document']);
  if (document === null) return null;

  // A warning this cannot read fails the read rather than being dropped: a document shown without one of
  // its cautions reads as cleaner than it is.
  const warnings: LongFormSectionWarning[] = [];
  for (const raw of value['warnings']) {
    const warning = decodeWarning(raw);
    if (warning === null) return null;
    warnings.push(warning);
  }

  const requests: LongFormSectionRequestSummary[] = [];
  for (const raw of value['requests']) {
    const request = decodeRequestSummary(raw);
    if (request === null) return null;
    requests.push(request);
  }

  return { document, warnings, requests };
}

/** The section and operation of one queued writing request, from either writing route's reply. */
export function decodeLongFormSectionWritingRequest(value: unknown): LongFormSectionWritingRequest | null {
  if (!isRecord(value) || typeof value['sectionId'] !== 'string') return null;

  const request = decodeAiProposalStatus(value['request']);

  return request === null ? null : { sectionId: value['sectionId'], request };
}

/** The issues a refused acceptance says the document has, from its `issues` problem extension. */
export function decodeLongFormAcceptanceIssues(value: unknown): readonly LongFormAcceptanceIssueDetail[] | null {
  if (!Array.isArray(value)) return null;

  const issues: LongFormAcceptanceIssueDetail[] = [];
  for (const raw of value) {
    if (!isRecord(raw)) return null;

    const issue = decodeEnum<LongFormAcceptanceIssue>(LONG_FORM_ACCEPTANCE_ISSUE_VALUES, raw['issue']);
    const sectionIds = decodeStringArray(raw['sectionIds']);
    if (issue === null || sectionIds === null) return null;

    issues.push({ issue, sectionIds });
  }

  return issues;
}

// ---------------------------------------------------------------------------
// Encoders
// ---------------------------------------------------------------------------

/** An instruction is sent only when there is one, so a first write and a plain rewrite are the same request. */
export function encodeRequestLongFormSection(request: RequestLongFormSectionRequest): Record<string, unknown> {
  const instruction = request.instruction?.trim() ?? '';

  return {
    revisionNumber: request.revisionNumber,
    ...(instruction === '' ? {} : { instruction }),
  };
}

export function encodeEditLongFormSection(request: EditLongFormSectionRequest): Record<string, unknown> {
  // The body exactly as typed: not trimmed, not normalised. The creator's text is theirs.
  return { expectedConcurrencyToken: request.expectedConcurrencyToken, body: request.body };
}

export function encodeSettleLongFormSection(request: SettleLongFormSectionRequest): Record<string, unknown> {
  return { expectedConcurrencyToken: request.expectedConcurrencyToken, settled: request.settled };
}

export function encodeAcceptLongFormDocument(request: AcceptLongFormDocumentRequest): Record<string, unknown> {
  return {
    expectedConcurrencyToken: request.expectedConcurrencyToken,
    revisionNumber: request.revisionNumber,
    confirmed: [...request.confirmed],
  };
}
