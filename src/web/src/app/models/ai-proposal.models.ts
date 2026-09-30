// Wire models for the AI proposal lifecycle
// (POST/GET /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/ai-proposals[/{aiProposalId}] and
// .../disposition). Mirrors CreatorPantry.Domain/Modules/Ai/Managers/{AiProposalModels,
// AiDispositionModels}.cs plus the enums beside them — do not add fields the backend doesn't send, and
// keep this file in sync when those ServiceModels change.
//
// The diff is the server's and is the only one. Nothing here compares anything: `beforeValue` was
// computed by the server from the pinned source version and a client that recomputed one could disagree
// with the server about what a creator is approving (AIREC-GR-002).
//
// Enums are decoded **closed**, as every other decoder in this app decodes them, and the cost is the one
// `recipe-version.models.ts` documents: `decodeArray` rejects a whole array on one bad element, so a
// server-side enum member added without widening the union here would blank a list rather than mislabel a
// row. Widen the union in the same change that adds the member.

import { isNumberOrNull, isRecord, isStringOrNull, decodeEnum } from './recipe.models';

/**
 * Decodes an array, rejecting the whole thing if any element fails.
 *
 * The same helper, and the same rationale, as the private one in `recipe-version.models.ts`: a partially
 * decoded diff would understate what changed, which is the one mistake a diff must not make. Duplicated
 * rather than shared because neither file's copy is part of either one's public surface.
 */
function decodeArray<T>(value: unknown, decode: (element: unknown) => T | null): readonly T[] | null {
  if (!Array.isArray(value)) return null;

  const decoded: T[] = [];
  for (const element of value) {
    const item = decode(element);
    if (item === null) return null;
    decoded.push(item);
  }
  return decoded;
}

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

/** Mirrors AiOperationStatus. Five of the eight are terminal; see {@link isTerminalAiStatus}. */
export type AiOperationStatus =
  | 'Requested'
  | 'Running'
  | 'Proposed'
  | 'Accepted'
  | 'PartiallyAccepted'
  | 'Rejected'
  | 'Failed'
  | 'Expired';

const AI_OPERATION_STATUS_VALUES: ReadonlySet<string> = new Set<AiOperationStatus>([
  'Requested',
  'Running',
  'Proposed',
  'Accepted',
  'PartiallyAccepted',
  'Rejected',
  'Failed',
  'Expired',
]);

/**
 * Whether an operation can still change on its own.
 *
 * The one behavioural fact this file states, and it is the server's rule rather than a guess: a terminal
 * operation is one there is nothing left to poll for, so a watcher stops. `Proposed` is deliberately *not*
 * terminal for the operation even though the model is finished with it — a disposition moves it on.
 */
export function isTerminalAiStatus(status: AiOperationStatus): boolean {
  return (
    status === 'Accepted' ||
    status === 'PartiallyAccepted' ||
    status === 'Rejected' ||
    status === 'Failed' ||
    status === 'Expired'
  );
}

/** Mirrors AiTaskType. */
export type AiTaskType =
  | 'Unspecified'
  | 'Diagnostic'
  | 'RecipeConcepts'
  /** AIREC-002's structured first draft. */
  | 'RecipeFirstDraft'
  /** AIREC-003's scoped revision of an existing recipe. */
  | 'RecipeRevision'
  /** AIREC-004's substitution advice for one selected ingredient. Reads a recipe; changes nothing in it. */
  | 'IngredientSubstitution'
  /** AIREC-005's single-goal adaptation — dietary, equipment, yield, or skill level. */
  | 'RecipeAdaptation'
  /** AIREC-006's field-linked review findings. Reads a recipe; changes nothing in it. */
  | 'RecipeReview'
  /** AIREC-008's explanation of an existing proposal. Reads a proposal; changes nothing in it. */
  | 'ProposalExplanation'
  /** RCPUB-001's editorial package for one approved recipe version. Reads a recipe; changes nothing in it. */
  | 'EditorialPackage'
  /** RCPUB-002's SEO package for one approved recipe version. Reads a recipe; changes nothing in it. */
  | 'SeoPackage';

/** Exported so the usage read can decode the same enum rather than mirroring it a second time. */
export const AI_TASK_TYPE_VALUES: ReadonlySet<string> = new Set<AiTaskType>([
  'Unspecified',
  'Diagnostic',
  'RecipeConcepts',
  'RecipeFirstDraft',
  'RecipeRevision',
  'IngredientSubstitution',
  'RecipeAdaptation',
  'RecipeReview',
  'ProposalExplanation',
  'EditorialPackage',
  'SeoPackage',
]);

/**
 * The discriminator a request names for each task the server reports, mirroring `AiTaskCatalog.KnownTasks`.
 *
 * It exists because the two directions of this contract speak different vocabularies: a status reply reports
 * the task as an enum member (`Diagnostic`), and a request may only name an allow-listed discriminator
 * (`diagnostic`). Asking again after a failure therefore needs the mapping, and lowercasing the enum name
 * would be guessing at a server allow-list rather than mirroring it.
 *
 * `Record<AiTaskType, …>` is what makes adding a task to the union a compile error here until its
 * discriminator is written down. `Unspecified` maps to null: an operation that never said what it was doing
 * cannot be re-requested.
 */
export const AI_TASK_DISCRIMINATORS: Readonly<Record<AiTaskType, string | null>> = {
  Unspecified: null,
  Diagnostic: 'diagnostic',
  // Concept requests have their own dedicated route (recipe-concept-requests), not this generic
  // ask-again discriminator, so there is nothing to name here.
  RecipeConcepts: null,
  // Likewise: first drafts are asked for through recipe-draft-requests, which names the task by being that
  // route rather than by carrying a discriminator.
  RecipeFirstDraft: null,
  // And revisions through recipe/{id}/revision-requests. Null here deliberately suppresses the panel's own
  // ask-again affordance for one: re-asking needs the creator's goal, which the panel does not hold and the
  // generic route cannot carry. The revision form is where a creator asks again.
  RecipeRevision: null,
  // And substitutions through recipe/{id}/substitution-requests. Null for a second reason as well as that
  // one: asking again means naming the ingredient, and nothing generic carries it.
  IngredientSubstitution: null,
  // And adaptations through recipe/{id}/adaptation-requests. Null for the same reason as a revision: asking
  // again needs the declared goal, which the generic route cannot carry.
  RecipeAdaptation: null,
  // And reviews through recipe/{id}/review-requests. Null for the same reason a substitution's and an
  // adaptation's are: this generic route cannot fix the Advisory scope a review's own route fixes server-side.
  RecipeReview: null,
  // And explanations through proposal-explanation-requests, which names the source proposal it explains
  // rather than a recipe at all — this generic route is nested under a recipe and has nowhere to put that.
  ProposalExplanation: null,
  // And editorial packages through recipe/{id}/editorial-package-requests. Null because the route fixes the
  // Advisory scope server-side and asking again needs the requested sections, which this generic route cannot carry.
  EditorialPackage: null,
  // And SEO packages through recipe/{id}/seo-package-requests, for the same reason: the route fixes the Advisory
  // scope, and asking again needs the requested sections, which this generic route cannot carry.
  SeoPackage: null,
};

/** Mirrors AiOperationScope. Which parts of the recipe a proposal may touch — a bound the server enforces. */
export type AiOperationScope =
  | 'Unspecified'
  | 'WholeRecipe'
  | 'Ingredients'
  | 'Instructions'
  | 'Metadata'
  | 'Media'
  /**
   * The task is not about parts of an existing recipe at all.
   *
   * Every workspace-level request carries this — concepts and first drafts both — so a client that cannot
   * decode it cannot read either of those features. `decodeEnum` returns null for an unknown member and
   * `decodeAiProposalStatus` fails on it, so the whole status vanishes rather than one field.
   */
  | 'NotApplicable'
  /**
   * The task reads a recipe and proposes no change to it — AIREC-004's substitution advice.
   *
   * Distinct from `NotApplicable`, which means no recipe was named at all. Both permit nothing; this one sits
   * beside a recipe id and a pinned version, so a reader is not told the operation named no recipe while the
   * fields next to it say otherwise.
   */
  | 'Advisory';

const AI_OPERATION_SCOPE_VALUES: ReadonlySet<string> = new Set<AiOperationScope>([
  'Unspecified',
  'WholeRecipe',
  'Ingredients',
  'Instructions',
  'Metadata',
  'Media',
  'NotApplicable',
  'Advisory',
]);

/** Mirrors AiFailureCategory. Required on the wire exactly when the status is `Failed`. */
export type AiFailureCategory =
  | 'Unspecified'
  | 'Validation'
  | 'Quota'
  | 'TemplateUnavailable'
  | 'Provider'
  | 'RateLimited'
  | 'Timeout'
  | 'OutputSchemaInvalid'
  | 'DomainInvalid'
  | 'SafetyBlocked'
  | 'Cancelled'
  | 'LeaseAbandoned'
  | 'AccountSuspended';

const AI_FAILURE_CATEGORY_VALUES: ReadonlySet<string> = new Set<AiFailureCategory>([
  'Unspecified',
  'Validation',
  'Quota',
  'TemplateUnavailable',
  'Provider',
  'RateLimited',
  'Timeout',
  'OutputSchemaInvalid',
  'DomainInvalid',
  'SafetyBlocked',
  'Cancelled',
  'LeaseAbandoned',
  'AccountSuspended',
]);

/** Mirrors AiChangeKind. */
export type AiChangeKind = 'Unspecified' | 'Set' | 'Add' | 'Remove' | 'Move';

const AI_CHANGE_KIND_VALUES: ReadonlySet<string> = new Set<AiChangeKind>([
  'Unspecified',
  'Set',
  'Add',
  'Remove',
  'Move',
]);

/** Mirrors AiChangeTargetKind. */
export type AiChangeTargetKind =
  | 'Unspecified'
  | 'Recipe'
  | 'Ingredient'
  | 'IngredientGroup'
  | 'InstructionStep'
  | 'InstructionGroup'
  | 'Equipment'
  | 'AssetLink'
  | 'Tag'
  | 'RecipeConcept'
  /** AIREC-004's substitution advice. Like `RecipeConcept`, nothing the recipe seam can apply. */
  | 'IngredientSubstitution'
  /** AIREC-006's review findings. Like `IngredientSubstitution`, advice a creator judges, never an edit. */
  | 'RecipeReviewFinding'
  /** AIREC-008's explanation items. Like `RecipeReviewFinding`, never an edit — not even to the proposal it explains. */
  | 'ProposalExplanationItem'
  /** RCPUB-001's editorial package sections. Never an edit to the recipe: content a creator accepts separately. */
  | 'ContentSection';

const AI_CHANGE_TARGET_KIND_VALUES: ReadonlySet<string> = new Set<AiChangeTargetKind>([
  'Unspecified',
  'Recipe',
  'Ingredient',
  'IngredientGroup',
  'InstructionStep',
  'InstructionGroup',
  'Equipment',
  'AssetLink',
  'Tag',
  'RecipeConcept',
  'IngredientSubstitution',
  'RecipeReviewFinding',
  'ProposalExplanationItem',
  'ContentSection',
]);

/** Mirrors AiChangeDisposition. What the creator decided about one change; `Pending` until they decide. */
export type AiChangeDisposition = 'Pending' | 'Accepted' | 'Rejected';

const AI_CHANGE_DISPOSITION_VALUES: ReadonlySet<string> = new Set<AiChangeDisposition>([
  'Pending',
  'Accepted',
  'Rejected',
]);

/**
 * Mirrors AiWarningKind.
 *
 * Assumptions and cautions are one vocabulary on purpose, so a review panel renders one uniform list and an
 * assumption cannot be shown with less weight than a caution. None of these is a safety verdict: a proposal
 * with no `SafetyCaution` has not been found safe, it has simply not been flagged (ai.md).
 *
 * **This union has to keep up with the server's enum, and the cost of it not doing so is total.**
 * `decodeArray` fails the whole array on one undecodable element, so a warning kind this list has not heard
 * of does not lose that one warning — it makes `decodeAiProposalDetail` return null and the entire proposal
 * vanish, leaving a panel saying "Ready for review" with nothing in it. `UnresolvedQuestion` shipped
 * server-side with AIREC-002 and was missing here, which is exactly what that looked like.
 */
export type AiWarningKind =
  | 'Unspecified'
  | 'Assumption'
  | 'CulinaryCaution'
  | 'NonScalableLanguage'
  | 'UnverifiedClaim'
  | 'SafetyCaution'
  /** A question the model declined to guess at, rather than an assumption it made. AIREC-002 emits these. */
  | 'UnresolvedQuestion'
  /** Why one change was proposed. Attached to that change, and carrying no caution. AIREC-003 emits these. */
  | 'Rationale'
  /**
   * A declared goal that could not be met, in full or in part, and why. Distinct from `CulinaryCaution` and
   * `SafetyCaution`, which qualify a change that was made — this says something asked for was not done.
   * AIREC-005 emits these, and requires one on any answer proposing no change at all.
   */
  | 'Limitation';

const AI_WARNING_KIND_VALUES: ReadonlySet<string> = new Set<AiWarningKind>([
  'Unspecified',
  'Assumption',
  'CulinaryCaution',
  'NonScalableLanguage',
  'UnverifiedClaim',
  'SafetyCaution',
  'UnresolvedQuestion',
  'Rationale',
  'Limitation',
]);

/** Mirrors AiDispositionDecision, less `Unspecified` — which is never a decision a client may send. */
export type AiDispositionDecision = 'AcceptAll' | 'AcceptSelected' | 'Reject';

// ---------------------------------------------------------------------------
// Request
// ---------------------------------------------------------------------------

/**
 * The body of `POST .../ai-proposals` — mirrors `RequestAiProposalViewModel`.
 *
 * **What is absent is the contract.** No prompt, no template, no model, no provider parameter, no tool list
 * and no workspace: the type has nowhere to put any of them, exactly as the server's own ViewModel has
 * nowhere to put them. `task` is an allow-listed discriminator the server resolves, never an instruction.
 */
export interface RequestAiProposalRequest {
  readonly task: string;
  readonly scope: AiOperationScope;
  /**
   * The exact version to work from, which must be the recipe's current version. Required: a proposal
   * computed against "latest" cannot be checked for staleness later, and that check is the whole basis of
   * accepting one safely.
   */
  readonly sourceVersionId: string;
}

export function encodeRequestAiProposalRequest(request: RequestAiProposalRequest): Record<string, unknown> {
  return { task: request.task, scope: request.scope, sourceVersionId: request.sourceVersionId };
}

/**
 * The body of `POST .../ai-proposals/{aiProposalId}/disposition` — mirrors
 * `AiProposalDispositionViewModel`.
 *
 * `acceptedChangeIds` is the explicit confirmation AIREC-GR-007 requires, not a convenience for partial
 * acceptance: naming the changes is how the request states what was reviewed. `AcceptAll` must name every
 * change the proposal contains, and a rejection must name none.
 *
 * Nothing here can describe a change — only ids of changes the server itself computed and stored.
 */
export interface AiProposalDispositionRequest {
  readonly decision: AiDispositionDecision;
  readonly acceptedChangeIds: readonly string[];
  readonly wasHelpful: boolean | null;
  readonly comment: string | null;
}

/**
 * Omits `wasHelpful` and `comment` when there is nothing to say, and trims the comment, so a creator who
 * tabbed through the box without typing does not store a blank string as their opinion.
 */
export function encodeAiProposalDispositionRequest(
  request: AiProposalDispositionRequest,
): Record<string, unknown> {
  const comment = (request.comment ?? '').trim();
  const body: Record<string, unknown> = {
    decision: request.decision,
    acceptedChangeIds: [...request.acceptedChangeIds],
  };

  if (request.wasHelpful !== null) body['wasHelpful'] = request.wasHelpful;
  if (comment.length > 0) body['comment'] = comment;

  return body;
}

// ---------------------------------------------------------------------------
// Status and proposal
// ---------------------------------------------------------------------------

/**
 * Mirrors AiProposedChangeServiceModel.
 *
 * `beforeValue` was computed by the server from the pinned version and never supplied by the model.
 * `fieldName` is a plain string rather than a union of `AiDiffFields` members, for the reason
 * `recipe-version.models.ts` gives about comparison fields: adding a proposable field is a compatible server
 * change, and a client that refused to decode an unrecognised one would break the whole review.
 */
export interface AiProposedChange {
  readonly changeId: string;
  readonly changeKind: AiChangeKind;
  readonly targetKind: AiChangeTargetKind;
  readonly targetId: string | null;
  readonly fieldName: string | null;
  readonly beforeValue: string | null;
  readonly afterValue: string | null;
  readonly proposedPosition: number | null;
  readonly disposition: AiChangeDisposition;
}

/** Mirrors AiProposalWarningServiceModel. `changeId` is null for a warning about the proposal as a whole. */
export interface AiProposalWarning {
  readonly kind: AiWarningKind;
  readonly message: string;
  readonly changeId: string | null;
}

/**
 * Mirrors AiProposalDetailServiceModel.
 *
 * `proposalId` is **not** the id a disposition is addressed to — that is the request id this reply arrived
 * under. It is here for correlating what a creator reviewed with the version it wrote.
 *
 * The provenance fields are here because a creator reviewing generated content is entitled to know what
 * produced it. There is no execution telemetry: latency, tokens and cost are operational.
 */
export interface AiProposalDetail {
  readonly proposalId: string;
  readonly outputSchemaVersion: string;
  readonly promptTemplateId: string;
  readonly promptTemplateVersion: string;
  readonly promptTemplateBodyChecksum: string;
  readonly providerName: string;
  readonly modelName: string;
  readonly createdAt: string;
  readonly changes: readonly AiProposedChange[];
  readonly warnings: readonly AiProposalWarning[];
}

/**
 * Mirrors AiProposalStatusServiceModel.
 *
 * `aiProposalRequestId` is the operation's id and the one to poll: a request exists from the moment it is
 * accepted, long before a proposal does. `proposal` is null until the status reaches `Proposed`.
 */
export interface AiProposalStatus {
  readonly aiProposalRequestId: string;
  readonly status: AiOperationStatus;
  readonly taskType: AiTaskType;
  readonly scope: AiOperationScope;
  readonly sourceVersionId: string | null;
  readonly requestedAt: string;
  readonly statusChangedAt: string;
  readonly failureCategory: AiFailureCategory | null;
  readonly proposal: AiProposalDetail | null;
}

/**
 * Mirrors AiProposalDispositionServiceModel.
 *
 * `recipeVersionNumber` is the version *this call* wrote, and is null for three reasons a client cannot tell
 * apart: the proposal was rejected; every accepted change already matched the recipe, so no version was
 * needed; or this was a replay and an earlier call wrote it. A client that needs the number reads the recipe.
 */
export interface AiProposalDispositionResult {
  readonly aiProposalRequestId: string;
  readonly status: AiOperationStatus;
  readonly acceptedChangeCount: number;
  readonly rejectedChangeCount: number;
  readonly recipeVersionNumber: number | null;
  readonly decidedAt: string;
}

function decodeAiProposedChange(value: unknown): AiProposedChange | null {
  if (!isRecord(value)) return null;
  const { changeId, targetId, fieldName, beforeValue, afterValue, proposedPosition } = value;
  const changeKind = decodeEnum<AiChangeKind>(AI_CHANGE_KIND_VALUES, value['changeKind']);
  const targetKind = decodeEnum<AiChangeTargetKind>(AI_CHANGE_TARGET_KIND_VALUES, value['targetKind']);
  const disposition = decodeEnum<AiChangeDisposition>(AI_CHANGE_DISPOSITION_VALUES, value['disposition']);

  if (
    typeof changeId !== 'string' ||
    changeKind === null ||
    targetKind === null ||
    disposition === null ||
    !isStringOrNull(targetId) ||
    !isStringOrNull(fieldName) ||
    !isStringOrNull(beforeValue) ||
    !isStringOrNull(afterValue) ||
    !isNumberOrNull(proposedPosition)
  ) {
    return null;
  }

  return {
    changeId,
    changeKind,
    targetKind,
    targetId,
    fieldName,
    beforeValue,
    afterValue,
    proposedPosition,
    disposition,
  };
}

function decodeAiProposalWarning(value: unknown): AiProposalWarning | null {
  if (!isRecord(value)) return null;
  const { message, changeId } = value;
  const kind = decodeEnum<AiWarningKind>(AI_WARNING_KIND_VALUES, value['kind']);

  if (kind === null || typeof message !== 'string' || !isStringOrNull(changeId)) return null;

  return { kind, message, changeId };
}

function decodeAiProposalDetail(value: unknown): AiProposalDetail | null {
  if (!isRecord(value)) return null;
  const {
    proposalId,
    outputSchemaVersion,
    promptTemplateId,
    promptTemplateVersion,
    promptTemplateBodyChecksum,
    providerName,
    modelName,
    createdAt,
  } = value;
  const changes = decodeArray(value['changes'], decodeAiProposedChange);
  const warnings = decodeArray(value['warnings'], decodeAiProposalWarning);

  if (
    typeof proposalId !== 'string' ||
    typeof outputSchemaVersion !== 'string' ||
    typeof promptTemplateId !== 'string' ||
    typeof promptTemplateVersion !== 'string' ||
    typeof promptTemplateBodyChecksum !== 'string' ||
    typeof providerName !== 'string' ||
    typeof modelName !== 'string' ||
    typeof createdAt !== 'string' ||
    changes === null ||
    warnings === null
  ) {
    return null;
  }

  return {
    proposalId,
    outputSchemaVersion,
    promptTemplateId,
    promptTemplateVersion,
    promptTemplateBodyChecksum,
    providerName,
    modelName,
    createdAt,
    changes,
    warnings,
  };
}

export function decodeAiProposalStatus(value: unknown): AiProposalStatus | null {
  if (!isRecord(value)) return null;
  const { aiProposalRequestId, sourceVersionId, requestedAt, statusChangedAt } = value;
  const status = decodeEnum<AiOperationStatus>(AI_OPERATION_STATUS_VALUES, value['status']);
  const taskType = decodeEnum<AiTaskType>(AI_TASK_TYPE_VALUES, value['taskType']);
  const scope = decodeEnum<AiOperationScope>(AI_OPERATION_SCOPE_VALUES, value['scope']);

  if (
    typeof aiProposalRequestId !== 'string' ||
    status === null ||
    taskType === null ||
    scope === null ||
    !isStringOrNull(sourceVersionId) ||
    typeof requestedAt !== 'string' ||
    typeof statusChangedAt !== 'string'
  ) {
    return null;
  }

  // Both nullable, and both distinguish "sent as null" from "not a category/proposal at all": an explicit
  // null is the ordinary case (no failure, no proposal yet), while a wrong type is a contract violation that
  // must fail rather than be read as absent.
  const rawFailure = value['failureCategory'];
  const failureCategory =
    rawFailure === null || rawFailure === undefined
      ? null
      : decodeEnum<AiFailureCategory>(AI_FAILURE_CATEGORY_VALUES, rawFailure);
  if (rawFailure !== null && rawFailure !== undefined && failureCategory === null) return null;

  const rawProposal = value['proposal'];
  const proposal = rawProposal === null || rawProposal === undefined ? null : decodeAiProposalDetail(rawProposal);
  if (rawProposal !== null && rawProposal !== undefined && proposal === null) return null;

  return {
    aiProposalRequestId,
    status,
    taskType,
    scope,
    sourceVersionId,
    requestedAt,
    statusChangedAt,
    failureCategory,
    proposal,
  };
}

export function decodeAiProposalDispositionResult(value: unknown): AiProposalDispositionResult | null {
  if (!isRecord(value)) return null;
  const { aiProposalRequestId, acceptedChangeCount, rejectedChangeCount, recipeVersionNumber, decidedAt } = value;
  const status = decodeEnum<AiOperationStatus>(AI_OPERATION_STATUS_VALUES, value['status']);

  if (
    typeof aiProposalRequestId !== 'string' ||
    status === null ||
    typeof acceptedChangeCount !== 'number' ||
    typeof rejectedChangeCount !== 'number' ||
    !isNumberOrNull(recipeVersionNumber) ||
    typeof decidedAt !== 'string'
  ) {
    return null;
  }

  return { aiProposalRequestId, status, acceptedChangeCount, rejectedChangeCount, recipeVersionNumber, decidedAt };
}
