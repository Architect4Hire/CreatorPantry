// Wire models for LF.2.2's long-form draft seam:
// GET/POST /api/v1/workspaces/{workspaceSlug}/long-form-drafts,
// GET .../long-form-drafts/{draftId},
// POST .../{draftId}/outline-requests and GET .../{draftId}/outline-requests/{requestId},
// PATCH .../{draftId}/plan and POST .../{draftId}/plan/approval.
// Mirrors CreatorPantry.Domain/Modules/Content/Managers/LongFormDraftModels.cs and
// Modules/Ai/Managers/AiLongFormOutlineRequestModels.cs — do not add fields the backend does not send, and
// keep this file in step with those ServiceModels.
//
// Enums are decoded closed, as every other decoder here does, with the cost `ai-proposal.models.ts` records:
// widen the union in the same change that adds a member server-side. `LongFormWireMirrorTests` on the backend
// reads this file and fails when an enum or the role table below falls out of step with the server.
//
// Nothing here decides whether a plan is valid. The server checks a plan against the long-form shape rules
// and says what stands in the way (`planCheck`); the role table below exists only so the screen can offer
// the roles a plan still has room for, and the server remains the judge of a plan that is sent.

import { AiProposalStatus, decodeAiProposalStatus } from './ai-proposal.models';
import { decodeEnum, isRecord, isStringOrNull } from './recipe.models';

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/** Mirrors `LongFormKind`. */
export type LongFormKind = 'BlogArticle' | 'NewsletterIssue';

export const LONG_FORM_KIND_VALUES: ReadonlySet<string> = new Set<LongFormKind>([
  'BlogArticle',
  'NewsletterIssue',
]);

/** Mirrors `LongFormDraftStatus`: where a draft stands. Not a publication state. */
export type LongFormDraftStatus = 'Draft' | 'Outlined' | 'Writing' | 'Assembled' | 'Accepted' | 'NeedsReview';

export const LONG_FORM_DRAFT_STATUS_VALUES: ReadonlySet<string> = new Set<LongFormDraftStatus>([
  'Draft',
  'Outlined',
  'Writing',
  'Assembled',
  'Accepted',
  'NeedsReview',
]);

/** Mirrors `LongFormSectionRole`: what a section of a long-form piece is for. */
export type LongFormSectionRole =
  | 'Hook'
  | 'Context'
  | 'WhyItWorks'
  | 'IngredientNotes'
  | 'MethodNarrative'
  | 'Tips'
  | 'Variations'
  | 'Storage'
  | 'Faq'
  | 'Cta'
  | 'Closing'
  | 'Body'
  | 'Greeting'
  | 'Lead'
  | 'Item'
  | 'Aside'
  | 'Signoff';

export const LONG_FORM_SECTION_ROLE_VALUES: ReadonlySet<string> = new Set<LongFormSectionRole>([
  'Hook',
  'Context',
  'WhyItWorks',
  'IngredientNotes',
  'MethodNarrative',
  'Tips',
  'Variations',
  'Storage',
  'Faq',
  'Cta',
  'Closing',
  'Body',
  'Greeting',
  'Lead',
  'Item',
  'Aside',
  'Signoff',
]);

/** Mirrors `ContentRevisionSource`, as a long-form revision carries it. */
export type LongFormRevisionSource = 'AiGenerated' | 'CreatorEdit' | 'Reaffirmed';

export const LONG_FORM_REVISION_SOURCE_VALUES: ReadonlySet<string> = new Set<LongFormRevisionSource>([
  'AiGenerated',
  'CreatorEdit',
  'Reaffirmed',
]);

/** Mirrors `LongFormWordTargetStatus`: where a document's word count sits against its kind's target. */
export type LongFormWordTargetStatus = 'NotAssessed' | 'Within' | 'Under' | 'Over';

export const LONG_FORM_WORD_TARGET_STATUS_VALUES: ReadonlySet<string> = new Set<LongFormWordTargetStatus>([
  'NotAssessed',
  'Within',
  'Under',
  'Over',
]);

/** Mirrors `LongFormAcceptanceIssue`: what a creator must acknowledge to accept an unfinished document. */
export type LongFormAcceptanceIssue = 'UnwrittenSections' | 'OverCeiling';

export const LONG_FORM_ACCEPTANCE_ISSUE_VALUES: ReadonlySet<string> = new Set<LongFormAcceptanceIssue>([
  'UnwrittenSections',
  'OverCeiling',
]);

/** Mirrors `LongFormFindingKind`: what kind of thing the claim scanner found in a section. */
export type LongFormFindingKind = 'UnverifiedClaim' | 'SafetyCaution' | 'Limitation';

export const LONG_FORM_FINDING_KIND_VALUES: ReadonlySet<string> = new Set<LongFormFindingKind>([
  'UnverifiedClaim',
  'SafetyCaution',
  'Limitation',
]);

/** Mirrors `LongFormSectionRequestOutcome`: where the latest request to write a section has got to. */
export type LongFormSectionRequestOutcome = 'Waiting' | 'Written' | 'Superseded' | 'Failed';

export const LONG_FORM_SECTION_REQUEST_OUTCOME_VALUES: ReadonlySet<string> = new Set<LongFormSectionRequestOutcome>([
  'Waiting',
  'Written',
  'Superseded',
  'Failed',
]);

// ---------------------------------------------------------------------------
// The role table
// ---------------------------------------------------------------------------

/** How often one role may appear in a plan of one kind. `max` is null for a role that may repeat freely. */
export interface LongFormRoleRule {
  readonly role: LongFormSectionRole;
  readonly max: number | null;
}

/**
 * Mirrors `LongFormPolicy.RolesFor`, in the server's own order.
 *
 * Here so the screen can offer only the roles a plan still has room for. It is a convenience and never the
 * verdict: a plan is checked by the server when it is sent, and `LongFormWireMirrorTests` fails the backend
 * build when this table and the policy disagree.
 */
export const LONG_FORM_ROLE_RULES: Readonly<Record<LongFormKind, readonly LongFormRoleRule[]>> = {
  BlogArticle: [
    { role: 'Hook', max: 1 },
    { role: 'Context', max: 1 },
    { role: 'WhyItWorks', max: 1 },
    { role: 'IngredientNotes', max: 1 },
    { role: 'MethodNarrative', max: 1 },
    { role: 'Tips', max: 1 },
    { role: 'Variations', max: 1 },
    { role: 'Storage', max: 1 },
    { role: 'Faq', max: 1 },
    { role: 'Cta', max: 1 },
    { role: 'Closing', max: 1 },
    { role: 'Body', max: 4 },
  ],
  NewsletterIssue: [
    { role: 'Greeting', max: 1 },
    { role: 'Lead', max: 1 },
    { role: 'Item', max: null },
    { role: 'Aside', max: 1 },
    { role: 'Closing', max: 1 },
    { role: 'Signoff', max: 1 },
  ],
};

/** Mirrors `LongFormPolicy.HeadingMaxCharacters`. */
export const LONG_FORM_HEADING_MAX_CHARACTERS = 120;

/** Mirrors `LongFormPolicy.IntentMaxCharacters`. */
export const LONG_FORM_INTENT_MAX_CHARACTERS = 200;

/** What a creator is shown for each role. Wording only; the role itself is what travels. */
export const LONG_FORM_ROLE_LABELS: Readonly<Record<LongFormSectionRole, string>> = {
  Hook: 'Opening',
  Context: 'Background',
  WhyItWorks: 'Why it works',
  IngredientNotes: 'Ingredient notes',
  MethodNarrative: 'The method',
  Tips: 'Tips',
  Variations: 'Variations',
  Storage: 'Storing and reheating',
  Faq: 'Questions and answers',
  Cta: 'Call to action',
  Closing: 'Closing',
  Body: 'Your own section',
  Greeting: 'Greeting',
  Lead: 'Lead',
  Item: 'Recipe',
  Aside: 'Aside',
  Signoff: 'Sign-off',
};

/**
 * The roles a plan of this kind still has room for, in the server's order.
 *
 * `Item` is never offered: an item covers one of the piece's recipes, and binding one to its recipe is not
 * something this screen can do (LF.4.1). Items already in a plan stay, and can be renamed and reordered.
 */
export function longFormRolesAvailable(
  kind: LongFormKind,
  used: readonly LongFormSectionRole[],
): readonly LongFormSectionRole[] {
  return LONG_FORM_ROLE_RULES[kind]
    .filter((rule) => rule.role !== 'Item')
    .filter((rule) => rule.max === null || used.filter((role) => role === rule.role).length < rule.max)
    .map((rule) => rule.role);
}

// ---------------------------------------------------------------------------
// ServiceModels
// ---------------------------------------------------------------------------

/** Mirrors `LongFormPlannedSectionServiceModel`. */
export interface LongFormPlannedSection {
  readonly id: string;
  readonly role: LongFormSectionRole;
  readonly heading: string;
  readonly intent: string;
  /** Whether the section has a body yet. The body itself is not part of a plan read. */
  readonly isWritten: boolean;
  readonly settled: boolean;
  /** For a newsletter item, the creative-context reference of the recipe it covers. */
  readonly sourceReferenceId: string | null;
}

/** Mirrors `LongFormRevisionServiceModel`. */
export interface LongFormRevision {
  readonly revisionNumber: number;
  readonly source: LongFormRevisionSource;
  readonly createdAt: string;
  readonly sections: readonly LongFormPlannedSection[];
}

/** Mirrors `LongFormPlanCheckFindingServiceModel`. `code` is one of the server's `content.long_form.*` codes. */
export interface LongFormPlanCheckFinding {
  readonly code: string;
  /** Null when the finding is about the plan as a whole. */
  readonly sectionId: string | null;
}

/** Mirrors `LongFormPlanCheckServiceModel`: whether the latest plan could be approved as it stands. */
export interface LongFormPlanCheck {
  readonly isValid: boolean;
  readonly policyVersion: string;
  readonly findings: readonly LongFormPlanCheckFinding[];
}

/** Mirrors `LongFormDraftServiceModel`. */
export interface LongFormDraft {
  readonly id: string;
  readonly creativeContextId: string;
  readonly kind: LongFormKind;
  readonly status: LongFormDraftStatus;
  /** Sent back as `expectedConcurrencyToken` on the next write. */
  readonly concurrencyToken: string;
  readonly createdAt: string;
  readonly updatedAt: string;
  /** Null until an outline has landed. */
  readonly latest: LongFormRevision | null;
  /** Null exactly when `latest` is. */
  readonly planCheck: LongFormPlanCheck | null;
}

/** Mirrors `LongFormDraftSummaryServiceModel`: one row of the recent list. */
export interface LongFormDraftSummary {
  readonly id: string;
  readonly creativeContextId: string;
  readonly kind: LongFormKind;
  readonly status: LongFormDraftStatus;
  readonly latestRevisionNumber: number | null;
  readonly updatedAt: string;
}

export interface LongFormDraftPage {
  readonly items: readonly LongFormDraftSummary[];
  readonly nextCursor: string | null;
}

/** Mirrors `StartedLongFormDraftServiceModel`: a draft, and the outline request just made for it. */
export interface StartedLongFormDraft {
  readonly draft: LongFormDraft;
  readonly request: AiProposalStatus;
}

/** Mirrors `LongFormOutlineRequestStatusServiceModel`. */
export interface LongFormOutlineRequestStatus {
  readonly request: AiProposalStatus;
  readonly draft: LongFormDraft;
}

// ---------------------------------------------------------------------------
// ViewModels
// ---------------------------------------------------------------------------

/** Mirrors `RequestLongFormOutlineViewModel`. */
export interface RequestLongFormOutlineRequest {
  readonly targetWords?: number | null;
  readonly wantedRoles?: readonly LongFormSectionRole[];
}

/** Mirrors `StartLongFormDraftViewModel`. There is no workspace field, and there must never be one. */
export interface StartLongFormDraftRequest extends RequestLongFormOutlineRequest {
  readonly creativeContextId: string;
  readonly kind: LongFormKind;
}

/** Mirrors `LongFormPlanSectionInputViewModel`. `id` is omitted for a section being added. */
export interface LongFormPlanSectionInput {
  readonly id: string | null;
  readonly role: LongFormSectionRole;
  readonly heading: string;
  readonly intent: string;
  readonly sourceReferenceId: string | null;
}

/** Mirrors `PatchLongFormPlanViewModel`: the whole plan, in order. */
export interface PatchLongFormPlanRequest {
  readonly expectedConcurrencyToken: string;
  readonly sections: readonly LongFormPlanSectionInput[];
}

/** Mirrors `ApproveLongFormPlanViewModel`. */
export interface ApproveLongFormPlanRequest {
  readonly expectedConcurrencyToken: string;
  readonly revisionNumber: number;
}

export interface LongFormDraftListQuery {
  readonly limit?: number;
  readonly cursor?: string | null;
  readonly kind?: LongFormKind | null;
  readonly creativeContextId?: string | null;
}

// ---------------------------------------------------------------------------
// Stable error codes
// ---------------------------------------------------------------------------

export const LONG_FORM_OUTLINE_NOT_ENABLED_CODE = 'ai.longFormOutline.not_enabled';

/** The piece of work is unknown, or another workspace's. One answer for both. */
export const LONG_FORM_CONTEXT_UNPROCESSABLE_CODE = 'content.long_form.context.unprocessable';

export const LONG_FORM_DRAFT_STALE_CODE = 'content.long_form.draft.stale.conflict';

/** The draft has no plan yet, so there is nothing to edit or approve. */
export const LONG_FORM_PLAN_NOT_STARTED_CODE = 'content.long_form.plan.not_started.conflict';

/** The draft's sections are already being written, so its plan is closed. */
export const LONG_FORM_PLAN_LOCKED_CODE = 'content.long_form.plan.locked.conflict';

/** Approval of a plan that does not meet the shape rules. The field errors are keyed `sections[n]`. */
export const LONG_FORM_PLAN_UNPROCESSABLE_CODE = 'content.long_form.plan.unprocessable';

/** What each `planCheck` finding means to a creator. An unknown code gets the last line. */
export function longFormFindingText(code: string): string {
  switch (code) {
    case 'content.long_form.sections.none':
      return 'A plan needs at least one section.';
    case 'content.long_form.role.not_allowed':
      return 'This kind of piece does not have a section like this one.';
    case 'content.long_form.role.repeated':
      return 'This kind of section appears more often than it may.';
    case 'content.long_form.heading.blank':
      return 'Give this section a heading.';
    case 'content.long_form.heading.too_long':
      return `Keep this heading to ${LONG_FORM_HEADING_MAX_CHARACTERS} characters.`;
    case 'content.long_form.intent.blank':
      return 'Say in one line what this section is for.';
    case 'content.long_form.intent.not_one_line':
      return 'Keep what this section is for to one line.';
    case 'content.long_form.intent.too_long':
      return `Keep what this section is for to ${LONG_FORM_INTENT_MAX_CHARACTERS} characters.`;
    default:
      return 'This section needs another look before the plan can be approved.';
  }
}

/** Whether a draft's plan may still be changed. Once sections are being written, it may not. */
export function longFormPlanIsOpen(draft: LongFormDraft): boolean {
  return draft.status === 'Draft' || draft.status === 'Outlined';
}

// ---------------------------------------------------------------------------
// Decoders
// ---------------------------------------------------------------------------

export function decodeLongFormPlannedSection(value: unknown): LongFormPlannedSection | null {
  if (!isRecord(value)) return null;

  const { id, heading, intent, isWritten, settled, sourceReferenceId } = value;
  const role = decodeEnum<LongFormSectionRole>(LONG_FORM_SECTION_ROLE_VALUES, value['role']);

  if (
    typeof id !== 'string' ||
    role === null ||
    typeof heading !== 'string' ||
    typeof intent !== 'string' ||
    typeof isWritten !== 'boolean' ||
    typeof settled !== 'boolean' ||
    !isStringOrNull(sourceReferenceId ?? null)
  ) {
    return null;
  }

  return { id, role, heading, intent, isWritten, settled, sourceReferenceId: (sourceReferenceId as string | null) ?? null };
}

export function decodeLongFormRevision(value: unknown): LongFormRevision | null {
  if (!isRecord(value)) return null;

  const { revisionNumber, createdAt } = value;
  const source = decodeEnum<LongFormRevisionSource>(LONG_FORM_REVISION_SOURCE_VALUES, value['source']);
  const rawSections = value['sections'];

  if (
    typeof revisionNumber !== 'number' ||
    !Number.isInteger(revisionNumber) ||
    source === null ||
    typeof createdAt !== 'string' ||
    !Array.isArray(rawSections)
  ) {
    return null;
  }

  // The whole revision fails on one unreadable section. A plan shown with a section missing would read as
  // the whole plan, and saving it would remove the section the screen could not read.
  const sections: LongFormPlannedSection[] = [];
  for (const raw of rawSections) {
    const section = decodeLongFormPlannedSection(raw);
    if (section === null) return null;
    sections.push(section);
  }

  return { revisionNumber, source, createdAt, sections };
}

export function decodeLongFormPlanCheck(value: unknown): LongFormPlanCheck | null {
  if (!isRecord(value)) return null;

  const { isValid, policyVersion } = value;
  const rawFindings = value['findings'];

  if (typeof isValid !== 'boolean' || typeof policyVersion !== 'string' || !Array.isArray(rawFindings)) {
    return null;
  }

  const findings: LongFormPlanCheckFinding[] = [];
  for (const raw of rawFindings) {
    if (!isRecord(raw) || typeof raw['code'] !== 'string' || !isStringOrNull(raw['sectionId'] ?? null)) {
      return null;
    }

    findings.push({ code: raw['code'], sectionId: (raw['sectionId'] as string | null) ?? null });
  }

  return { isValid, policyVersion, findings };
}

export function decodeLongFormDraft(value: unknown): LongFormDraft | null {
  if (!isRecord(value)) return null;

  const { id, creativeContextId, concurrencyToken, createdAt, updatedAt } = value;
  const kind = decodeEnum<LongFormKind>(LONG_FORM_KIND_VALUES, value['kind']);
  const status = decodeEnum<LongFormDraftStatus>(LONG_FORM_DRAFT_STATUS_VALUES, value['status']);

  if (
    typeof id !== 'string' ||
    typeof creativeContextId !== 'string' ||
    kind === null ||
    status === null ||
    typeof concurrencyToken !== 'string' ||
    concurrencyToken === '' ||
    typeof createdAt !== 'string' ||
    typeof updatedAt !== 'string'
  ) {
    return null;
  }

  // Both nullable, and both tell "sent as null" from "the wrong shape": a draft with no plan yet is the
  // ordinary case, while a plan that cannot be read is a contract violation and not an empty draft.
  const rawLatest = value['latest'];
  const latest = rawLatest === null || rawLatest === undefined ? null : decodeLongFormRevision(rawLatest);
  if (rawLatest !== null && rawLatest !== undefined && latest === null) return null;

  const rawCheck = value['planCheck'];
  const planCheck = rawCheck === null || rawCheck === undefined ? null : decodeLongFormPlanCheck(rawCheck);
  if (rawCheck !== null && rawCheck !== undefined && planCheck === null) return null;

  return { id, creativeContextId, kind, status, concurrencyToken, createdAt, updatedAt, latest, planCheck };
}

export function decodeLongFormDraftSummary(value: unknown): LongFormDraftSummary | null {
  if (!isRecord(value)) return null;

  const { id, creativeContextId, updatedAt } = value;
  const kind = decodeEnum<LongFormKind>(LONG_FORM_KIND_VALUES, value['kind']);
  const status = decodeEnum<LongFormDraftStatus>(LONG_FORM_DRAFT_STATUS_VALUES, value['status']);
  const latestRevisionNumber = value['latestRevisionNumber'] ?? null;

  if (
    typeof id !== 'string' ||
    typeof creativeContextId !== 'string' ||
    kind === null ||
    status === null ||
    (latestRevisionNumber !== null && typeof latestRevisionNumber !== 'number') ||
    typeof updatedAt !== 'string'
  ) {
    return null;
  }

  return { id, creativeContextId, kind, status, latestRevisionNumber, updatedAt };
}

export function decodeLongFormDraftPage(value: unknown): LongFormDraftPage | null {
  if (!isRecord(value) || !Array.isArray(value['items']) || !isStringOrNull(value['nextCursor'] ?? null)) {
    return null;
  }

  const items: LongFormDraftSummary[] = [];
  for (const raw of value['items']) {
    const item = decodeLongFormDraftSummary(raw);
    if (item === null) return null;
    items.push(item);
  }

  return { items, nextCursor: (value['nextCursor'] as string | null) ?? null };
}

export function decodeStartedLongFormDraft(value: unknown): StartedLongFormDraft | null {
  if (!isRecord(value)) return null;

  const draft = decodeLongFormDraft(value['draft']);
  const request = decodeAiProposalStatus(value['request']);

  return draft === null || request === null ? null : { draft, request };
}

export function decodeLongFormOutlineRequestStatus(value: unknown): LongFormOutlineRequestStatus | null {
  if (!isRecord(value)) return null;

  const request = decodeAiProposalStatus(value['request']);
  const draft = decodeLongFormDraft(value['draft']);

  return draft === null || request === null ? null : { request, draft };
}

// ---------------------------------------------------------------------------
// Encoders
// ---------------------------------------------------------------------------

/** Optional inputs are sent only when set, so a request that never mentions one is what it always was. */
export function encodeRequestLongFormOutline(request: RequestLongFormOutlineRequest): Record<string, unknown> {
  return {
    ...(request.targetWords === null || request.targetWords === undefined ? {} : { targetWords: request.targetWords }),
    ...(request.wantedRoles && request.wantedRoles.length > 0 ? { wantedRoles: [...request.wantedRoles] } : {}),
  };
}

export function encodeStartLongFormDraft(request: StartLongFormDraftRequest): Record<string, unknown> {
  return {
    creativeContextId: request.creativeContextId,
    kind: request.kind,
    ...encodeRequestLongFormOutline(request),
  };
}

export function encodePatchLongFormPlan(request: PatchLongFormPlanRequest): Record<string, unknown> {
  return {
    expectedConcurrencyToken: request.expectedConcurrencyToken,
    sections: request.sections.map((section) => ({
      // An id only for a section the plan already has; a new one is sent without, and the server mints it.
      ...(section.id === null ? {} : { id: section.id }),
      role: section.role,
      heading: section.heading,
      intent: section.intent,
      ...(section.sourceReferenceId === null ? {} : { sourceReferenceId: section.sourceReferenceId }),
    })),
  };
}

export function encodeApproveLongFormPlan(request: ApproveLongFormPlanRequest): Record<string, unknown> {
  return {
    expectedConcurrencyToken: request.expectedConcurrencyToken,
    revisionNumber: request.revisionNumber,
  };
}
