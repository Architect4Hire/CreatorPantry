// Wire models for the readiness evaluation and the editorial state machine (TESTRUN-004/005):
//   GET  /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/readiness
//   POST /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/readiness-transitions
//
// Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/{RecipeReadinessServiceModel,
// RecipeReadinessTransitionViewModel,RecipeStatusTransitions}.cs. Keep in sync when those change.
//
// Nothing here evaluates a rule. Every verdict, sentence and count in a RecipeReadiness comes from the
// server, which is the only thing that decides them — a client that recomputed `hasBlockers` could disagree
// with the gate that actually refuses an approval.

import { RecipeStatus, decodeEnum, isRecord, isStringOrNull } from './recipe.models';
import { WorkspaceRole } from './auth.models';

// ---------------------------------------------------------------------------
// Vocabulary
// ---------------------------------------------------------------------------

/**
 * Mirrors RecipeReadinessStatus. Four answers, not two.
 *
 * Held **closed**: grouping and the blocker count depend on it, and an unrecognised status quietly falling
 * through to "satisfied" would report a recipe as clear when it is not. That is the one direction this type
 * must never fail in.
 */
export type RecipeReadinessStatus = 'Satisfied' | 'Blocker' | 'Recommendation' | 'NotApplicable';

const READINESS_STATUS_VALUES: ReadonlySet<string> = new Set<RecipeReadinessStatus>([
  'Satisfied',
  'Blocker',
  'Recommendation',
  'NotApplicable',
]);

/**
 * What a piece of evidence points at. Mirrors RecipeReadinessEvidenceKind, and deliberately **tolerant** — a
 * plain string rather than a closed union.
 *
 * Adding a member is a compatible server change (api-contract.md), and a closed decode would reject the whole
 * evaluation over one unfamiliar kind, blanking a creator's entire checklist to avoid mislabelling one line.
 * The same stance `recipe-version.models.ts` takes for `field` and `section`, for the same reason. A kind this
 * app has never heard of is labelled from its own name and carried through untouched.
 */
export type RecipeReadinessEvidenceKind = string;

/** The kinds this app has words for. Anything else is humanised from the wire value. */
export const READINESS_EVIDENCE_KIND_LABELS: Readonly<Record<string, string>> = {
  Recipe: 'Recipe',
  RecipeIngredientLine: 'Ingredient line',
  RecipeVersion: 'Version',
  RecipeTestRun: 'Test',
  TestIssue: 'Issue found in testing',
  RecipeAssetLink: 'Linked media',
  Ingredient: 'Ingredient',
  AiProposal: 'AI proposal',
};

/** Mirrors RecipeTransitionTargetViewModel — the states a transition may name. */
export type RecipeTransitionTarget = 'Draft' | 'InDevelopment' | 'Testing' | 'ReadyForReview' | 'Approved' | 'Archived';

// ---------------------------------------------------------------------------
// Readiness
// ---------------------------------------------------------------------------

/** Mirrors RecipeReadinessEvidenceServiceModel: the exact record and field behind one finding. */
export interface RecipeReadinessEvidence {
  readonly kind: RecipeReadinessEvidenceKind;
  /** Null where the finding is about a record that does not exist — "no version to have tested". */
  readonly recordId: string | null;
  /** Null where the finding is about the record's existence rather than one of its fields. */
  readonly fieldName: string | null;
  /** The creator's own words where the evidence has any. Never rewritten. */
  readonly label: string | null;
}

/**
 * Mirrors RecipeReadinessFindingServiceModel: one rule's verdict.
 *
 * `ruleId` is the stable thing to branch on. `summary` and `detail` are sentences for a reader and may be
 * reworded server-side, so nothing here parses them.
 */
export interface RecipeReadinessFinding {
  readonly ruleId: string;
  readonly status: RecipeReadinessStatus;
  readonly summary: string;
  /** Populated on an unmet rule, null on a satisfied or inapplicable one. */
  readonly detail: string | null;
  readonly evidence: readonly RecipeReadinessEvidence[];
  /** Whether configuration changed this rule's severity from the catalogue's default. */
  readonly severityOverridden: boolean;
}

/**
 * Mirrors RecipeReadinessServiceModel: every rule's verdict at one moment.
 *
 * **Not a status, and never stored as one.** The recipe's editorial state is its own `status`.
 * `concurrencyToken` says which moment this describes — the same token a transition must quote — so a client
 * holding both can tell whether the answer still describes the recipe it is showing.
 *
 * **Not a safety, allergen, nutrition or dietary clearance**, and no combination of findings makes it one.
 */
export interface RecipeReadiness {
  readonly recipeId: string;
  readonly evaluatedVersionId: string | null;
  readonly evaluatedVersionNumber: number | null;
  readonly concurrencyToken: string;
  /** The catalogue's own version, so a result stays interpretable after the rules move. */
  readonly ruleSetVersion: string;
  readonly hasBlockers: boolean;
  readonly blockerCount: number;
  readonly recommendationCount: number;
  /** Every rule that ran, satisfied ones included: the thing this feeds is a checklist. */
  readonly findings: readonly RecipeReadinessFinding[];
  /** Rules configuration switched off. Absent from `findings` — a rule that did not run has not passed. */
  readonly disabledRuleIds: readonly string[];
  /** Ids configuration named that the catalogue does not have, so a typo is discoverable. */
  readonly unknownConfiguredRuleIds: readonly string[];
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}

function decodeStringArray(value: unknown): readonly string[] | null {
  if (!Array.isArray(value)) return null;
  return value.every(isString) ? value : null;
}

function decodeEvidence(value: unknown): RecipeReadinessEvidence | null {
  if (!isRecord(value)) return null;

  const { kind, recordId, fieldName, label } = value;
  if (!isString(kind) || !isStringOrNull(recordId)) return null;

  // Absent and null are the same answer for these two; the server omits them rather than sending null.
  const resolvedField = fieldName === undefined ? null : fieldName;
  const resolvedLabel = label === undefined ? null : label;
  if (!isStringOrNull(resolvedField) || !isStringOrNull(resolvedLabel)) return null;

  return { kind, recordId, fieldName: resolvedField, label: resolvedLabel };
}

function decodeFinding(value: unknown): RecipeReadinessFinding | null {
  if (!isRecord(value)) return null;

  const status = decodeEnum<RecipeReadinessStatus>(READINESS_STATUS_VALUES, value['status']);
  const { ruleId, summary, detail, severityOverridden } = value;

  if (status === null || !isString(ruleId) || !isString(summary)) return null;
  if (!isStringOrNull(detail === undefined ? null : detail)) return null;
  if (typeof severityOverridden !== 'boolean') return null;

  if (!Array.isArray(value['evidence'])) return null;
  const evidence: RecipeReadinessEvidence[] = [];
  for (const item of value['evidence']) {
    const decoded = decodeEvidence(item);
    if (decoded === null) return null;
    evidence.push(decoded);
  }

  return {
    ruleId,
    status,
    summary,
    detail: detail === undefined ? null : (detail as string | null),
    evidence,
    severityOverridden,
  };
}

export function decodeRecipeReadiness(value: unknown): RecipeReadiness | null {
  if (!isRecord(value)) return null;

  const {
    recipeId,
    evaluatedVersionId,
    evaluatedVersionNumber,
    concurrencyToken,
    ruleSetVersion,
    hasBlockers,
    blockerCount,
    recommendationCount,
  } = value;

  if (!isString(recipeId) || !isString(concurrencyToken) || !isString(ruleSetVersion)) return null;
  if (!isStringOrNull(evaluatedVersionId)) return null;
  if (evaluatedVersionNumber !== null && typeof evaluatedVersionNumber !== 'number') return null;
  if (typeof hasBlockers !== 'boolean') return null;
  if (typeof blockerCount !== 'number' || typeof recommendationCount !== 'number') return null;

  const disabledRuleIds = decodeStringArray(value['disabledRuleIds']);
  const unknownConfiguredRuleIds = decodeStringArray(value['unknownConfiguredRuleIds']);
  if (disabledRuleIds === null || unknownConfiguredRuleIds === null) return null;

  if (!Array.isArray(value['findings'])) return null;
  const findings: RecipeReadinessFinding[] = [];
  for (const item of value['findings']) {
    const decoded = decodeFinding(item);
    if (decoded === null) return null;
    findings.push(decoded);
  }

  return {
    recipeId,
    evaluatedVersionId,
    evaluatedVersionNumber,
    concurrencyToken,
    ruleSetVersion,
    hasBlockers,
    blockerCount,
    recommendationCount,
    findings,
    disabledRuleIds,
    unknownConfiguredRuleIds,
  };
}

// ---------------------------------------------------------------------------
// Transitions
// ---------------------------------------------------------------------------

/**
 * Mirrors RecipeReadinessTransitionViewModel.
 *
 * There is no `fromStatus` and no readiness verdict, and neither is an omission: the recipe knows where it is,
 * and the server makes its own evaluation when the target needs one. A body carrying either would be a client
 * asserting a fact the server owns.
 */
export interface RecipeTransitionRequest {
  readonly targetStatus: RecipeTransitionTarget;
  /** Required when reopening, optional otherwise. Trimmed and nulled when blank. */
  readonly reason?: string | null;
  /** The recipe's `concurrencyToken` as the caller last saw it. Required. */
  readonly expectedConcurrencyToken: string;
}

export function encodeRecipeTransitionRequest(request: RecipeTransitionRequest): Record<string, unknown> {
  return {
    targetStatus: request.targetStatus,
    reason: request.reason ?? null,
    expectedConcurrencyToken: request.expectedConcurrencyToken,
  };
}

/** Mirrors RecipePolicy.TransitionReasonMaxLength. */
export const TRANSITION_REASON_MAX_LENGTH = 1000;

// ---------------------------------------------------------------------------
// The machine, mirrored
// ---------------------------------------------------------------------------

/**
 * How a move is confirmed before it is sent.
 *
 * `dialog` for a move that takes a sentence — that is a modal hosting a field with its own validation, which
 * is what `CpDialogComponent` is for. `destructive` for a genuine two-answer question that loses something,
 * which goes through `ConfirmService`. `none` for advancing your own work one step.
 */
export type RecipeTransitionConfirmation = 'none' | 'dialog' | 'destructive';

/**
 * One move a recipe may make, as a surface needs to know it.
 *
 * @see RECIPE_TRANSITION_MACHINE_VERSION for what keeps this honest.
 */
export interface RecipeTransitionMove {
  readonly from: RecipeStatus;
  readonly to: RecipeTransitionTarget;
  readonly minimumRole: WorkspaceRole;
  /** True only for a reopen: the one move a later reader cannot reconstruct from its two states. */
  readonly requiresReason: boolean;
  /** True only for the approval, and the server makes that evaluation itself. */
  readonly requiresReadinessClear: boolean;
  /** True only for the approval, which captures the content it approves. */
  readonly writesVersion: boolean;
  /** The control's label. */
  readonly label: string;
  readonly confirmation: RecipeTransitionConfirmation;
}

/**
 * The machine's version, mirroring `RecipeStatusTransitions.Version`.
 *
 * **This is the drift alarm.** `RecipeStatusTransitions` is the only authority on which moves exist, and it is
 * not published over HTTP — there is no route that lists a recipe's legal targets — so a surface that offers
 * the moves that exist rather than offering all six and letting the server refuse five has to hold a copy.
 * The copy is presentation and copywriting only: the server refuses anything this table gets wrong, and
 * `400 recipes.transition.invalid_request` carries the states the recipe could have gone to, which the UI
 * surfaces rather than swallowing. Raise this string with the server's and the test below will tell you what
 * else to change.
 */
export const RECIPE_TRANSITION_MACHINE_VERSION = '1.0.0';

/** Where a reopened recipe goes. Mirrors `RecipeStatusTransitions.ReopenTarget`. */
export const REOPEN_TARGET: RecipeTransitionTarget = 'InDevelopment';

/**
 * Every legal move, in the order `RecipeStatusTransitions.All` lists them.
 *
 * A move that is not here is not offered; a move that is here is still the server's to refuse.
 */
export const RECIPE_TRANSITIONS: readonly RecipeTransitionMove[] = [
  // Forward, one step at a time. Contributor, because advancing your own work is the work.
  move('Draft', 'InDevelopment', 'Contributor', 'Start developing'),
  move('InDevelopment', 'Testing', 'Contributor', 'Send to testing'),
  move('Testing', 'ReadyForReview', 'Contributor', 'Mark ready for review'),

  // The approval. Editor, gated on readiness, and it writes the version it approves.
  {
    from: 'ReadyForReview',
    to: 'Approved',
    minimumRole: 'Editor',
    requiresReason: false,
    requiresReadinessClear: true,
    writesVersion: true,
    label: 'Approve',
    confirmation: 'dialog',
  },

  // Reopen. Editor and explained, because it withdraws work somebody else advanced.
  reopen('Testing'),
  reopen('ReadyForReview'),
  reopen('Approved'),

  // Archive, from anywhere but the archive. Editor, as REC-006 already required.
  archive('Draft'),
  archive('InDevelopment'),
  archive('Testing'),
  archive('ReadyForReview'),
  archive('Approved'),

  // Restore, to Draft — the one target REC-006 has.
  move('Archived', 'Draft', 'Editor', 'Restore from the archive'),
];

function move(
  from: RecipeStatus,
  to: RecipeTransitionTarget,
  minimumRole: WorkspaceRole,
  label: string,
): RecipeTransitionMove {
  return {
    from,
    to,
    minimumRole,
    requiresReason: false,
    requiresReadinessClear: false,
    writesVersion: false,
    label,
    confirmation: 'none',
  };
}

function reopen(from: RecipeStatus): RecipeTransitionMove {
  return {
    from,
    to: REOPEN_TARGET,
    minimumRole: 'Editor',
    requiresReason: true,
    requiresReadinessClear: false,
    writesVersion: false,
    label: 'Reopen for more work',
    confirmation: 'dialog',
  };
}

function archive(from: RecipeStatus): RecipeTransitionMove {
  return {
    from,
    to: 'Archived',
    minimumRole: 'Editor',
    requiresReason: false,
    requiresReadinessClear: false,
    writesVersion: false,
    label: 'Archive',
    confirmation: 'destructive',
  };
}

const ROLE_RANK: Readonly<Record<WorkspaceRole, number>> = {
  Viewer: 0,
  Contributor: 1,
  Editor: 2,
  Owner: 3,
};

/** Whether a role is at least the one a move needs. Mirrors the server's `>=` on `WorkspaceRole`. */
export function roleAllows(role: WorkspaceRole | null, minimum: WorkspaceRole): boolean {
  return role !== null && ROLE_RANK[role] >= ROLE_RANK[minimum];
}

/** Every move out of one state, in machine order. */
export function transitionsFrom(status: RecipeStatus): readonly RecipeTransitionMove[] {
  return RECIPE_TRANSITIONS.filter((candidate) => candidate.from === status);
}

/** Human words for a state, for a label or a sentence. */
export const RECIPE_STATUS_LABELS: Readonly<Record<RecipeStatus, string>> = {
  Draft: 'Draft',
  InDevelopment: 'In development',
  Testing: 'Testing',
  ReadyForReview: 'Ready for review',
  Approved: 'Approved',
  Archived: 'Archived',
};
