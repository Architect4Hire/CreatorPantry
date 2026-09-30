// Wire models for the recipe test-run write seam (TESTRUN-001/002):
//   POST  /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/test-runs
//   PATCH /api/v1/workspaces/{workspaceSlug}/recipes/{recipeId}/test-runs/{testRunId}
//
// Mirrors CreatorPantry.Domain/Modules/Recipes/Managers/{CreateRecipeTestRunViewModel,
// UpdateRecipeTestRunViewModel,RecipeTestRunServiceModel}.cs. Do not add fields the backend does not
// send or accept, and keep this file in sync when those models change.
//
// The reading half of the aggregate — the paged history, its summary counts, and issue resolution — is
// deliberately absent. Those belong to the test-history surface, and a decoder written here against a
// route this client does not call yet would be a contract nobody had checked.

import { PatchField, decodeEnum, isNumberOrNull, isRecord, isStringOrNull } from './recipe.models';

export { absent, submitted } from './recipe.models';
export type { PatchField } from './recipe.models';

// ---------------------------------------------------------------------------
// Vocabulary
// ---------------------------------------------------------------------------

/**
 * Mirrors TestRunOutcome. `NotStated` is a value, not an absence: a run with no issues recorded is not
 * thereby a success, and nothing in this client may read it as one.
 */
export type TestRunOutcome = 'NotStated' | 'Succeeded' | 'SucceededWithIssues' | 'Failed';

export const TEST_RUN_OUTCOME_VALUES: ReadonlySet<string> = new Set<TestRunOutcome>([
  'NotStated',
  'Succeeded',
  'SucceededWithIssues',
  'Failed',
]);

/** Mirrors TestObservationKind. `Unspecified` is a legitimate answer; `Other` says the tester looked and none fitted. */
export type TestObservationKind =
  | 'Unspecified'
  | 'Texture'
  | 'Flavour'
  | 'Appearance'
  | 'Aroma'
  | 'Timing'
  | 'Difficulty'
  | 'Other';

export const TEST_OBSERVATION_KIND_VALUES: ReadonlySet<string> = new Set<TestObservationKind>([
  'Unspecified',
  'Texture',
  'Flavour',
  'Appearance',
  'Aroma',
  'Timing',
  'Difficulty',
  'Other',
]);

/**
 * Mirrors TestIssueSeverity, which has no zero and no default. Describes the recipe's fitness as a draft
 * and never its safety: a `Blocking` issue is not a safety finding, and the absence of one is not a
 * clearance (recipes.md).
 */
export type TestIssueSeverity = 'Minor' | 'Major' | 'Blocking';

export const TEST_ISSUE_SEVERITY_VALUES: ReadonlySet<string> = new Set<TestIssueSeverity>([
  'Minor',
  'Major',
  'Blocking',
]);

/** Mirrors TestIssueResolutionKind. Read-only here: this client records no resolutions. */
export type TestIssueResolutionKind = 'Fixed' | 'WontFix' | 'NotReproduced';

export const TEST_ISSUE_RESOLUTION_KIND_VALUES: ReadonlySet<string> = new Set<TestIssueResolutionKind>([
  'Fixed',
  'WontFix',
  'NotReproduced',
]);

// ---------------------------------------------------------------------------
// Request models
// ---------------------------------------------------------------------------

/**
 * Mirrors TestObservationInputViewModel. Position in the list is the note's order.
 *
 * `id` names a note this run already owns, to edit in place. Omit it for a new note — and on a create,
 * sending one at all is refused server-side, since nothing exists yet for it to name.
 */
export interface TestObservationInput {
  readonly id?: string | null;
  readonly kind?: TestObservationKind | null;
  readonly text: string;
}

/** Mirrors TestIssueInputViewModel. Position in the list is the issue's order. */
export interface TestIssueInput {
  readonly id?: string | null;
  /**
   * How badly it affects the recipe. Nullable on the wire because the ViewModel's is, and that is what makes
   * an unanswered severity reportable: `null` is refused by the validator at this row's own
   * `issues[i].severity`, whereas an invented placeholder would fail JSON binding at a path no field owns.
   */
  readonly severity: TestIssueSeverity | null;
  readonly title: string;
  readonly description?: string | null;
  /**
   * The observation this issue came from, as a position in the **same request's** `observations` list.
   *
   * An index rather than an id, because on a create the notes it points at have no ids yet. On an update it
   * may only be sent when `observations` is sent in the same request, since the positions mean that list.
   */
  readonly observationIndex?: number | null;
}

/**
 * Mirrors CreateRecipeTestRunViewModel. `sourceVersionNumber` is required — a test is evidence about an
 * exact set of words, so there is no "as it currently stands" to fall back on.
 *
 * Every field is optional on the wire, exactly as the ViewModel's are: required-ness is a server validation
 * rule reported against a named field, not something this type enforces by being un-constructable.
 */
export interface CreateRecipeTestRunRequest {
  readonly sourceVersionNumber: number;
  readonly testedAt: string;
  readonly outcome?: TestRunOutcome | null;
  readonly rating?: number | null;
  readonly environmentNotes?: string | null;
  readonly equipmentNotes?: string | null;
  readonly summaryNotes?: string | null;
  readonly actualYieldText?: string | null;
  readonly actualYieldQuantity?: number | null;
  readonly actualYieldUnitId?: string | null;
  readonly actualPrepTimeMinutes?: number | null;
  readonly actualCookTimeMinutes?: number | null;
  readonly actualRestTimeMinutes?: number | null;
  readonly actualTotalTimeMinutes?: number | null;
  readonly observations?: readonly TestObservationInput[] | null;
  readonly issues?: readonly TestIssueInput[] | null;
}

/**
 * Mirrors UpdateRecipeTestRunViewModel: a JSON Merge Patch. An absent field is left alone, a submitted one
 * is set, and a submitted `null` clears it — which is why every content field is a `PatchField`, and why
 * `encodeUpdateRecipeTestRunRequest` omits absent keys from the JSON entirely.
 *
 * There is no `sourceVersionNumber`. Repointing a test at another version would not be an edit but a claim
 * that a different thing happened, and the route accepts no such field.
 */
export interface UpdateRecipeTestRunRequest {
  readonly expectedConcurrencyToken: string;
  readonly testedAt: PatchField<string>;
  readonly outcome: PatchField<TestRunOutcome>;
  readonly rating: PatchField<number | null>;
  readonly environmentNotes: PatchField<string | null>;
  readonly equipmentNotes: PatchField<string | null>;
  readonly summaryNotes: PatchField<string | null>;
  readonly actualYieldText: PatchField<string | null>;
  readonly actualYieldQuantity: PatchField<number | null>;
  readonly actualYieldUnitId: PatchField<string | null>;
  readonly actualPrepTimeMinutes: PatchField<number | null>;
  readonly actualCookTimeMinutes: PatchField<number | null>;
  readonly actualRestTimeMinutes: PatchField<number | null>;
  readonly actualTotalTimeMinutes: PatchField<number | null>;
  /** Replaces rather than merges: the submitted list becomes the run's complete set. `[]` clears it. */
  readonly observations: PatchField<readonly TestObservationInput[]>;
  /** @see observations — with one exception: a resolved issue cannot be dropped by omitting it. */
  readonly issues: PatchField<readonly TestIssueInput[]>;
}

function encodeObservation(observation: TestObservationInput): Record<string, unknown> {
  return {
    ...(observation.id === undefined || observation.id === null ? {} : { id: observation.id }),
    kind: observation.kind ?? null,
    text: observation.text,
  };
}

function encodeIssue(issue: TestIssueInput): Record<string, unknown> {
  return {
    ...(issue.id === undefined || issue.id === null ? {} : { id: issue.id }),
    severity: issue.severity,
    title: issue.title,
    description: issue.description ?? null,
    observationIndex: issue.observationIndex ?? null,
  };
}

export function encodeCreateRecipeTestRunRequest(request: CreateRecipeTestRunRequest): Record<string, unknown> {
  return {
    sourceVersionNumber: request.sourceVersionNumber,
    testedAt: request.testedAt,
    outcome: request.outcome ?? null,
    rating: request.rating ?? null,
    environmentNotes: request.environmentNotes ?? null,
    equipmentNotes: request.equipmentNotes ?? null,
    summaryNotes: request.summaryNotes ?? null,
    actualYieldText: request.actualYieldText ?? null,
    actualYieldQuantity: request.actualYieldQuantity ?? null,
    actualYieldUnitId: request.actualYieldUnitId ?? null,
    actualPrepTimeMinutes: request.actualPrepTimeMinutes ?? null,
    actualCookTimeMinutes: request.actualCookTimeMinutes ?? null,
    actualRestTimeMinutes: request.actualRestTimeMinutes ?? null,
    actualTotalTimeMinutes: request.actualTotalTimeMinutes ?? null,
    observations: (request.observations ?? []).map(encodeObservation),
    issues: (request.issues ?? []).map(encodeIssue),
  };
}

/**
 * Builds the PATCH body, omitting every absent field.
 *
 * The omission is the whole point: a key present with `null` clears the field, so writing one out for a
 * field the creator never touched would blank data they could not see. Nothing here decides what is absent
 * — the caller does, by comparing against the run it loaded.
 */
export function encodeUpdateRecipeTestRunRequest(request: UpdateRecipeTestRunRequest): Record<string, unknown> {
  const body: Record<string, unknown> = { expectedConcurrencyToken: request.expectedConcurrencyToken };

  const put = <T>(key: string, field: PatchField<T>, encode: (value: T) => unknown = (value) => value): void => {
    if (field.submitted) body[key] = encode(field.value);
  };

  put('testedAt', request.testedAt);
  put('outcome', request.outcome);
  put('rating', request.rating);
  put('environmentNotes', request.environmentNotes);
  put('equipmentNotes', request.equipmentNotes);
  put('summaryNotes', request.summaryNotes);
  put('actualYieldText', request.actualYieldText);
  put('actualYieldQuantity', request.actualYieldQuantity);
  put('actualYieldUnitId', request.actualYieldUnitId);
  put('actualPrepTimeMinutes', request.actualPrepTimeMinutes);
  put('actualCookTimeMinutes', request.actualCookTimeMinutes);
  put('actualRestTimeMinutes', request.actualRestTimeMinutes);
  put('actualTotalTimeMinutes', request.actualTotalTimeMinutes);
  put('observations', request.observations, (observations) => observations.map(encodeObservation));
  put('issues', request.issues, (issues) => issues.map(encodeIssue));

  return body;
}

// ---------------------------------------------------------------------------
// Response models
// ---------------------------------------------------------------------------

/** Mirrors TestObservationServiceModel. */
export interface TestObservation {
  readonly id: string;
  readonly kind: TestObservationKind;
  readonly text: string;
  readonly sortOrder: number;
}

/** Mirrors TestIssueResolutionServiceModel. Write-once server-side: read here, never edited. */
export interface TestIssueResolution {
  readonly id: string;
  readonly kind: TestIssueResolutionKind;
  readonly notes: string | null;
  readonly resolvedByMembershipId: string;
  readonly resolvedAt: string;
  readonly resolutionRecipeVersionId: string | null;
  readonly predatingVersionOverrideReason: string | null;
}

/** Mirrors TestIssueServiceModel. A null `resolution` *is* the unresolved state; there is no second flag. */
export interface TestIssue {
  readonly id: string;
  readonly severity: TestIssueSeverity;
  readonly title: string;
  readonly description: string | null;
  readonly testObservationId: string | null;
  readonly sortOrder: number;
  readonly resolution: TestIssueResolution | null;
}

/** Mirrors RecipeTestRunServiceModel: one recorded test as a caller reads it back. */
export interface RecipeTestRun {
  readonly id: string;
  readonly recipeId: string;
  /** The exact version that was cooked. Not changeable by an edit. */
  readonly recipeVersionId: string;
  readonly testedAt: string;
  readonly testedByMembershipId: string;
  readonly outcome: TestRunOutcome;
  readonly rating: number | null;
  readonly environmentNotes: string | null;
  readonly equipmentNotes: string | null;
  readonly summaryNotes: string | null;
  readonly actualYieldText: string | null;
  readonly actualYieldQuantity: number | null;
  readonly actualYieldUnitId: string | null;
  readonly actualPrepTimeMinutes: number | null;
  readonly actualCookTimeMinutes: number | null;
  readonly actualRestTimeMinutes: number | null;
  /** What the whole thing took, as recorded. Never the sum of the three above. */
  readonly actualTotalTimeMinutes: number | null;
  readonly createdByMembershipId: string;
  readonly updatedByMembershipId: string;
  readonly createdAt: string;
  readonly updatedAt: string;
  /** Opaque. Stored and sent back on the next edit; never parsed, compared or ordered by. */
  readonly concurrencyToken: string;
  readonly observations: readonly TestObservation[];
  readonly issues: readonly TestIssue[];
}

/**
 * Mirrors CreatedRecipeTestRunServiceModel — what a create answers with.
 *
 * `observationIds` and `issueIds` are in **submitted order**, which is what lets a client that has just
 * created a run carry straight on editing it without a read: it knows what it sent, and this says what each
 * row is now called. There is no route that reads one run back, so that pairing is the only way in.
 */
export interface CreatedRecipeTestRun {
  readonly testRunId: string;
  readonly recipeId: string;
  readonly recipeVersionId: string;
  readonly sourceVersionNumber: number;
  readonly outcome: TestRunOutcome;
  readonly testedAt: string;
  readonly createdAt: string;
  readonly concurrencyToken: string;
  readonly observationIds: readonly string[];
  readonly issueIds: readonly string[];
}

function isString(value: unknown): value is string {
  return typeof value === 'string';
}

/**
 * Decodes an array, rejecting the whole thing if any element fails.
 *
 * The same stance the version comparison takes, and for a sharper reason here: a partially decoded issue
 * list would hide a problem a tester recorded, and an edit composed against it would then delete the issue
 * it never showed.
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

function decodeTestObservation(value: unknown): TestObservation | null {
  if (!isRecord(value)) return null;

  const kind = decodeEnum<TestObservationKind>(TEST_OBSERVATION_KIND_VALUES, value['kind']);
  const { id, text, sortOrder } = value;

  if (!isString(id) || kind === null || !isString(text) || typeof sortOrder !== 'number') return null;

  return { id, kind, text, sortOrder };
}

function decodeTestIssueResolution(value: unknown): TestIssueResolution | null {
  if (!isRecord(value)) return null;

  const kind = decodeEnum<TestIssueResolutionKind>(TEST_ISSUE_RESOLUTION_KIND_VALUES, value['kind']);
  const { id, notes, resolvedByMembershipId, resolvedAt, resolutionRecipeVersionId, predatingVersionOverrideReason } =
    value;

  if (!isString(id) || kind === null || !isString(resolvedByMembershipId) || !isString(resolvedAt)) return null;
  if (!isStringOrNull(notes) || !isStringOrNull(resolutionRecipeVersionId)) return null;
  if (!isStringOrNull(predatingVersionOverrideReason)) return null;

  return {
    id,
    kind,
    notes,
    resolvedByMembershipId,
    resolvedAt,
    resolutionRecipeVersionId,
    predatingVersionOverrideReason,
  };
}

function decodeTestIssue(value: unknown): TestIssue | null {
  if (!isRecord(value)) return null;

  const severity = decodeEnum<TestIssueSeverity>(TEST_ISSUE_SEVERITY_VALUES, value['severity']);
  const { id, title, description, testObservationId, sortOrder, resolution } = value;

  if (!isString(id) || severity === null || !isString(title) || typeof sortOrder !== 'number') return null;
  if (!isStringOrNull(description) || !isStringOrNull(testObservationId)) return null;

  // Absent and explicitly null both mean unresolved; anything else must decode or the whole issue fails,
  // because an issue shown as unresolved when it is not is the one mistake that loses a decision.
  const decodedResolution =
    resolution === null || resolution === undefined ? null : decodeTestIssueResolution(resolution);
  if (resolution !== null && resolution !== undefined && decodedResolution === null) return null;

  return { id, severity, title, description, testObservationId, sortOrder, resolution: decodedResolution };
}

export function decodeRecipeTestRun(value: unknown): RecipeTestRun | null {
  if (!isRecord(value)) return null;

  const outcome = decodeEnum<TestRunOutcome>(TEST_RUN_OUTCOME_VALUES, value['outcome']);
  const observations = decodeArray(value['observations'], decodeTestObservation);
  const issues = decodeArray(value['issues'], decodeTestIssue);

  const {
    id,
    recipeId,
    recipeVersionId,
    testedAt,
    testedByMembershipId,
    rating,
    environmentNotes,
    equipmentNotes,
    summaryNotes,
    actualYieldText,
    actualYieldQuantity,
    actualYieldUnitId,
    actualPrepTimeMinutes,
    actualCookTimeMinutes,
    actualRestTimeMinutes,
    actualTotalTimeMinutes,
    createdByMembershipId,
    updatedByMembershipId,
    createdAt,
    updatedAt,
    concurrencyToken,
  } = value;

  if (outcome === null || observations === null || issues === null) return null;
  if (!isString(id) || !isString(recipeId) || !isString(recipeVersionId) || !isString(testedAt)) return null;
  if (!isString(testedByMembershipId) || !isString(createdByMembershipId) || !isString(updatedByMembershipId)) {
    return null;
  }
  if (!isString(createdAt) || !isString(updatedAt) || !isString(concurrencyToken)) return null;
  if (!isNumberOrNull(rating) || !isNumberOrNull(actualYieldQuantity)) return null;
  if (!isNumberOrNull(actualPrepTimeMinutes) || !isNumberOrNull(actualCookTimeMinutes)) return null;
  if (!isNumberOrNull(actualRestTimeMinutes) || !isNumberOrNull(actualTotalTimeMinutes)) return null;
  if (!isStringOrNull(environmentNotes) || !isStringOrNull(equipmentNotes) || !isStringOrNull(summaryNotes)) {
    return null;
  }
  if (!isStringOrNull(actualYieldText) || !isStringOrNull(actualYieldUnitId)) return null;

  return {
    id,
    recipeId,
    recipeVersionId,
    testedAt,
    testedByMembershipId,
    outcome,
    rating,
    environmentNotes,
    equipmentNotes,
    summaryNotes,
    actualYieldText,
    actualYieldQuantity,
    actualYieldUnitId,
    actualPrepTimeMinutes,
    actualCookTimeMinutes,
    actualRestTimeMinutes,
    actualTotalTimeMinutes,
    createdByMembershipId,
    updatedByMembershipId,
    createdAt,
    updatedAt,
    concurrencyToken,
    observations,
    issues,
  };
}

export function decodeCreatedRecipeTestRun(value: unknown): CreatedRecipeTestRun | null {
  if (!isRecord(value)) return null;

  const outcome = decodeEnum<TestRunOutcome>(TEST_RUN_OUTCOME_VALUES, value['outcome']);
  const observationIds = decodeArray(value['observationIds'], (id) => (isString(id) ? id : null));
  const issueIds = decodeArray(value['issueIds'], (id) => (isString(id) ? id : null));

  const { testRunId, recipeId, recipeVersionId, sourceVersionNumber, testedAt, createdAt, concurrencyToken } = value;

  if (outcome === null || observationIds === null || issueIds === null) return null;
  if (!isString(testRunId) || !isString(recipeId) || !isString(recipeVersionId)) return null;
  if (typeof sourceVersionNumber !== 'number') return null;
  if (!isString(testedAt) || !isString(createdAt) || !isString(concurrencyToken)) return null;

  return {
    testRunId,
    recipeId,
    recipeVersionId,
    sourceVersionNumber,
    outcome,
    testedAt,
    createdAt,
    concurrencyToken,
    observationIds,
    issueIds,
  };
}

// ---------------------------------------------------------------------------
// Test history (TESTRUN-003)
// ---------------------------------------------------------------------------

/**
 * Mirrors TestRunIssueFilter. Whether the test still has a problem nobody has decided anything about.
 *
 * `AllResolved` is also true of a run that raised no issues at all — vacuous but correct, since there is
 * nothing on it left to decide. A creator looking for "tests that found nothing wrong" wants the tester's own
 * verdict, `Succeeded`, which is a different question and a different filter.
 */
export type TestRunIssueFilter = 'HasUnresolved' | 'AllResolved';

/**
 * Mirrors TestRunSummaryServiceModel: one row of the history.
 *
 * **Counts, not contents.** The three counts are the most a row says about what a test recorded, and
 * `attachmentCount` the most it can say about photographs nothing can yet authorize. `summaryNotes` is the one
 * prose field a row carries — the conditions and equipment notes are not here, and neither are the
 * observations or the issues themselves.
 */
export interface TestRunSummary {
  readonly id: string;
  readonly recipeVersionId: string;
  /** The version that was cooked, as the recipe's history lists it. */
  readonly versionNumber: number;
  readonly testedAt: string;
  readonly testedByMembershipId: string;
  /** Null when the membership behind it can no longer be named — authorship outlives membership. */
  readonly testedByName: string | null;
  readonly outcome: TestRunOutcome;
  readonly rating: number | null;
  readonly summaryNotes: string | null;
  readonly actualYieldText: string | null;
  readonly actualYieldQuantity: number | null;
  readonly actualYieldUnitId: string | null;
  readonly actualPrepTimeMinutes: number | null;
  readonly actualCookTimeMinutes: number | null;
  readonly actualRestTimeMinutes: number | null;
  readonly actualTotalTimeMinutes: number | null;
  readonly observationCount: number;
  readonly issueCount: number;
  readonly unresolvedIssueCount: number;
  readonly attachmentCount: number;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly concurrencyToken: string;
}

/**
 * Mirrors TestRunHistorySummaryServiceModel: the counts beside the page.
 *
 * Describes the **filtered** set, so narrowing the filters narrows it. It is **never a readiness verdict** —
 * that is a separate deterministic evaluation with its own route.
 */
export interface TestRunHistorySummary {
  readonly totalCount: number;
  /** Every outcome named, even at zero. */
  readonly byOutcome: Readonly<Record<string, number>>;
  readonly runsWithUnresolvedIssues: number;
  readonly unresolvedIssueCount: number;
}

/** Mirrors the cursor page of test summaries plus its summary block. */
export interface TestRunHistoryPage {
  readonly items: readonly TestRunSummary[];
  readonly nextCursor: string | null;
  /** Absent when `includeSummary=false` was asked for. */
  readonly summary: TestRunHistorySummary | null;
}

/**
 * The history query as this client holds it.
 *
 * `versions` is the creator's raw text ("3, 5") rather than a parsed list: the server splits and validates it
 * and names `version` when it cannot, and a second parser here would be a second thing to disagree with.
 *
 * There is deliberately **no `limit`**. The server clamps out-of-range values and has its own default, so
 * mirroring a page size here would be one more number to keep in step for no gain.
 */
export interface TestRunHistoryQuery {
  readonly versions?: string;
  readonly testedBy?: readonly string[];
  readonly outcomes?: readonly TestRunOutcome[];
  /** Inclusive lower bound on when the cooking happened, as an instant. */
  readonly testedFrom?: string | null;
  /** Exclusive upper bound on when the cooking happened, as an instant. */
  readonly testedBefore?: string | null;
  readonly issues?: TestRunIssueFilter | null;
  readonly includeSummary?: boolean;
  readonly cursor?: string | null;
}

/** Query-string parameters, omitting every filter that is not set. */
export function encodeTestRunHistoryQuery(query: TestRunHistoryQuery): Record<string, string> {
  const params: Record<string, string> = {};

  const versions = query.versions?.trim();
  if (versions) params['version'] = versions;
  if (query.testedBy?.length) params['testedBy'] = query.testedBy.join(',');
  if (query.outcomes?.length) params['outcome'] = query.outcomes.join(',');
  if (query.testedFrom) params['testedFrom'] = query.testedFrom;
  if (query.testedBefore) params['testedBefore'] = query.testedBefore;
  if (query.issues) params['issues'] = query.issues;

  // Only when false: true is the server's default, so sending it would be noise on every first page.
  if (query.includeSummary === false) params['includeSummary'] = 'false';
  if (query.cursor) params['cursor'] = query.cursor;

  return params;
}

function decodeTestRunSummary(value: unknown): TestRunSummary | null {
  if (!isRecord(value)) return null;

  const outcome = decodeEnum<TestRunOutcome>(TEST_RUN_OUTCOME_VALUES, value['outcome']);
  const {
    id,
    recipeVersionId,
    versionNumber,
    testedAt,
    testedByMembershipId,
    testedByName,
    rating,
    summaryNotes,
    actualYieldText,
    actualYieldQuantity,
    actualYieldUnitId,
    actualPrepTimeMinutes,
    actualCookTimeMinutes,
    actualRestTimeMinutes,
    actualTotalTimeMinutes,
    observationCount,
    issueCount,
    unresolvedIssueCount,
    attachmentCount,
    createdAt,
    updatedAt,
    concurrencyToken,
  } = value;

  if (outcome === null) return null;
  if (!isString(id) || !isString(recipeVersionId) || !isString(testedAt)) return null;
  if (!isString(testedByMembershipId) || !isString(createdAt) || !isString(updatedAt)) return null;
  if (!isString(concurrencyToken) || !isStringOrNull(testedByName)) return null;
  if (typeof versionNumber !== 'number') return null;
  if (!isNumberOrNull(rating) || !isNumberOrNull(actualYieldQuantity)) return null;
  if (!isNumberOrNull(actualPrepTimeMinutes) || !isNumberOrNull(actualCookTimeMinutes)) return null;
  if (!isNumberOrNull(actualRestTimeMinutes) || !isNumberOrNull(actualTotalTimeMinutes)) return null;
  if (!isStringOrNull(summaryNotes) || !isStringOrNull(actualYieldText)) return null;
  if (!isStringOrNull(actualYieldUnitId)) return null;
  if (typeof observationCount !== 'number' || typeof issueCount !== 'number') return null;
  if (typeof unresolvedIssueCount !== 'number' || typeof attachmentCount !== 'number') return null;

  return {
    id,
    recipeVersionId,
    versionNumber,
    testedAt,
    testedByMembershipId,
    testedByName,
    outcome,
    rating,
    summaryNotes,
    actualYieldText,
    actualYieldQuantity,
    actualYieldUnitId,
    actualPrepTimeMinutes,
    actualCookTimeMinutes,
    actualRestTimeMinutes,
    actualTotalTimeMinutes,
    observationCount,
    issueCount,
    unresolvedIssueCount,
    attachmentCount,
    createdAt,
    updatedAt,
    concurrencyToken,
  };
}

function decodeTestRunHistorySummary(value: unknown): TestRunHistorySummary | null {
  if (!isRecord(value)) return null;

  const { totalCount, byOutcome, runsWithUnresolvedIssues, unresolvedIssueCount } = value;
  if (typeof totalCount !== 'number' || typeof runsWithUnresolvedIssues !== 'number') return null;
  if (typeof unresolvedIssueCount !== 'number' || !isRecord(byOutcome)) return null;

  const counts: Record<string, number> = {};
  for (const [outcome, count] of Object.entries(byOutcome)) {
    if (typeof count !== 'number') return null;
    counts[outcome] = count;
  }

  return { totalCount, byOutcome: counts, runsWithUnresolvedIssues, unresolvedIssueCount };
}

export function decodeTestRunHistoryPage(value: unknown): TestRunHistoryPage | null {
  if (!isRecord(value) || !Array.isArray(value['items'])) return null;
  if (!isStringOrNull(value['nextCursor'])) return null;

  const items: TestRunSummary[] = [];
  for (const item of value['items']) {
    const decoded = decodeTestRunSummary(item);
    if (decoded === null) return null;
    items.push(decoded);
  }

  const rawSummary = value['summary'];
  const summary = rawSummary === null || rawSummary === undefined ? null : decodeTestRunHistorySummary(rawSummary);
  if (rawSummary !== null && rawSummary !== undefined && summary === null) return null;

  return { items, nextCursor: value['nextCursor'], summary };
}

// ---------------------------------------------------------------------------
// Resolving an issue (TESTRUN-002)
// ---------------------------------------------------------------------------

/**
 * Mirrors ResolveTestIssueViewModel: what was done about a problem a test found.
 *
 * **Nothing here describes the issue.** The issue says what went wrong; this says what was done about it, and
 * a body that could restate the problem would be a second copy of a fact that can then disagree with the
 * first. There is no concurrency token, because nothing is being overwritten.
 */
export interface ResolveTestIssueRequest {
  readonly kind: TestIssueResolutionKind;
  readonly notes?: string | null;
  /** The version carrying the correction, by number. Only a `Fixed` resolution may name one. */
  readonly resolutionVersionNumber?: number | null;
  /** Why a version at or before the tested one was named anyway. */
  readonly predatingVersionOverrideReason?: string | null;
}

export function encodeResolveTestIssueRequest(request: ResolveTestIssueRequest): Record<string, unknown> {
  return {
    kind: request.kind,
    notes: request.notes ?? null,
    resolutionVersionNumber: request.resolutionVersionNumber ?? null,
    predatingVersionOverrideReason: request.predatingVersionOverrideReason ?? null,
  };
}

/** Mirrors ResolvedTestIssueServiceModel — what a caller is told after resolving one issue. */
export interface ResolvedTestIssue {
  readonly testRunId: string;
  readonly testIssueId: string;
  readonly resolution: TestIssueResolution;
}

export function decodeResolvedTestIssue(value: unknown): ResolvedTestIssue | null {
  if (!isRecord(value)) return null;

  const { testRunId, testIssueId, resolution } = value;
  if (!isString(testRunId) || !isString(testIssueId)) return null;

  const decoded = decodeTestIssueResolution(resolution);
  return decoded === null ? null : { testRunId, testIssueId, resolution: decoded };
}
