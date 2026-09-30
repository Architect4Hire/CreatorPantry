namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>Stable error codes for recipe routes. Renaming a code is a breaking API change.</summary>
public static class RecipeErrorCodes
{
    /// <summary>The request body failed shape or format validation.</summary>
    public const string RecipeInvalidRequest = "recipes.recipe.invalid_request";

    /// <summary>The caller is a member of the workspace but their role does not permit this.</summary>
    /// <remarks>
    /// Distinct from <see cref="RecipeNotFound"/> on purpose. Being refused for role is safe to disclose to
    /// someone already known to belong here; it is not existence disclosure, because they can already see
    /// that the workspace is real.
    /// </remarks>
    public const string RecipeForbidden = "recipes.recipe.forbidden";

    /// <summary>
    /// Unknown recipe, or one belonging to another workspace: deliberately indistinguishable, so that a
    /// caller cannot learn a recipe exists by being refused it (tenancy.md).
    /// </summary>
    public const string RecipeNotFound = "recipes.recipe.not_found";

    /// <summary>
    /// A write — an edit or a restore — quoted a concurrency token the recipe has moved past: someone else
    /// saved first, or this caller composed their change against a stale read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>.conflict</c> suffix is what <c>ProblemResults.StatusFor</c> maps to 409, and 409 is the
    /// honest status — the request is well formed and permitted, and would be accepted against the state the
    /// caller thought they had.
    /// </para>
    /// <para>
    /// The problem body deliberately carries no current token and no server content. The creator's attempted
    /// edit never left their editor — and a refused restore cost them only the asking — so nothing is lost,
    /// and a read returns both the state they are missing and a fresh token. Returning that state through an
    /// error path would mean widening
    /// <c>OperationError</c> for every code in the system so that one of them could answer a question a
    /// <c>GET</c> already answers.
    /// </para>
    /// </remarks>
    public const string RecipeConflict = "recipes.recipe.conflict";

    /// <summary>
    /// The recipe is archived, and the operation asked would change its content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its own code rather than <see cref="RecipeConflict"/>, although both are 409s, because the remedies
    /// are opposites. A stale token says "re-read and try again", and retrying is exactly right. This says
    /// "bring the recipe back first", and retrying will fail forever — a client that could not tell them
    /// apart would loop on one and give up on the other.
    /// </para>
    /// <para>
    /// 409 and not 403: the caller's role is not the problem, and an Editor gets this too. The request is
    /// well formed and permitted, and would be accepted the moment the recipe is unarchived — which is what
    /// a conflict means.
    /// </para>
    /// <para>
    /// Returned by the edit seam and by a version restore. <em>Not</em> by a duplicate: copying an archived
    /// recipe writes a different recipe and is the ordinary way to pick shelved work back up. See
    /// <see cref="RecipePolicy.AcceptsContentChanges"/>, which is where the rule itself lives.
    /// </para>
    /// </remarks>
    public const string RecipeArchivedConflict = "recipes.archived.conflict";

    /// <summary>
    /// The transition asked for is not a move the machine has, or it is missing something the move requires
    /// (TESTRUN-005).
    /// </summary>
    /// <remarks>
    /// Covers an invalid jump — <c>Draft → Approved</c>, or a move to the state the recipe is already in —
    /// and a reopen with no reason. Both are things a caller asked for and is told about, which is why they
    /// are one code: the difference between them is a sentence in the response, not a branch a client takes.
    /// </remarks>
    public const string TransitionInvalidRequest = "recipes.transition.invalid_request";

    /// <summary>
    /// The move exists and the caller's role is below its bar (TESTRUN-005).
    /// </summary>
    /// <remarks>
    /// 403 and not 409: the request is well formed and the recipe is in the right state, and it is the caller
    /// who cannot make this move — a Contributor asking for an approval. Distinct from
    /// <see cref="TransitionInvalidRequest"/> so that a client can tell "nobody may do this from here" from
    /// "you may not do this".
    /// </remarks>
    public const string TransitionForbidden = "recipes.transition.forbidden";

    /// <summary>
    /// An approval was asked for over a readiness evaluation that still has blockers (TESTRUN-004).
    /// </summary>
    /// <remarks>
    /// 409: the request is well formed and permitted, and would be accepted once the blockers are cleared,
    /// which is what a conflict means — the same reading <see cref="RecipeArchivedConflict"/> takes. The
    /// blocking rule ids travel in the response so a client can say which ones rather than send the caller
    /// back to the readiness screen to guess.
    /// </remarks>
    public const string TransitionBlockedConflict = "recipes.transition.blocked.conflict";

    /// <summary>
    /// A version number the recipe has none of. The recipe itself was found and is readable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="RecipeNotFound"/>, and safe to be. Existence hiding protects workspaces and
    /// recipes; by the time this code can be returned the caller has already been shown that this recipe
    /// exists and may read its history, so telling them version 9 is not among its versions discloses nothing
    /// they could not learn by listing them. Collapsing the two would make a client unable to tell "you cannot
    /// see this recipe" from "check the version numbers", which are different problems with different
    /// remedies.
    /// </para>
    /// <para>
    /// Also returned for a version that exists but has no archived document. No write path produces one — every
    /// version this API writes stores its snapshot in the same transaction — so this is a state reachable only
    /// through platform maintenance, and "there is nothing here to compare" is the same answer either way.
    /// </para>
    /// </remarks>
    public const string VersionNotFound = "recipes.version.not_found";

    /// <summary>
    /// The version comparison query named something that is not a version number: a missing <c>from</c> or
    /// <c>to</c>, or one below 1.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecipeInvalidRequest"/> for the reason <see cref="SearchInvalidRequest"/> is:
    /// it describes a query string rather than a body, and the field names in the problem body are the query
    /// parameters the caller actually sent.
    /// </remarks>
    public const string ComparisonInvalidRequest = "recipes.comparison.invalid_request";

    /// <summary>
    /// A search filter this module defines and does not accept — an unknown status, readiness, review state or
    /// sort key, a malformed id in a comma-separated list, or an inverted date range.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecipeInvalidRequest"/> because it describes a query string rather than a body,
    /// and the field names in the problem body are the query parameters the caller actually sent.
    /// </remarks>
    public const string SearchInvalidRequest = "recipes.search.invalid_request";

    /// <summary>
    /// The cursor was issued for a different workspace, a different ordering, or a different set of filters
    /// than the request it arrived on.
    /// </summary>
    /// <remarks>
    /// Its own code, mirroring <c>reference.cursor.invalid_request</c>, because the client's remedy is specific:
    /// start the list again without a cursor. A generic bad-request code would leave a paging client retrying
    /// the same rejected cursor. Replaying one is a mistake rather than an attack — everything a cursor names is
    /// content the caller could already read — but answering it with a page would silently skip or repeat rows,
    /// which is the failure the keyset design exists to prevent.
    /// </remarks>
    public const string CursorInvalidRequest = "recipes.cursor.invalid_request";

    /// <summary>
    /// A scaling request named neither or both of a multiplier and a target yield, named a non-positive one,
    /// or named a target yield the recipe's own yield cannot be scaled toward (it has no structured number).
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecipeInvalidRequest"/> for the reason <see cref="ComparisonInvalidRequest"/>
    /// is: the field names in the problem body are this request's own (<c>multiplier</c>,
    /// <c>targetYieldQuantity</c>), not a generic recipe edit's.
    /// </remarks>
    public const string ScalingInvalidRequest = "recipes.scaling.invalid_request";

    /// <summary>
    /// A unit-conversion request named units that cannot be bridged as asked: different dimensions with no
    /// density approved for the pair, a temperature unit (its own operation — 7.8), or a unit id that is
    /// unknown or no longer offered.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecipeInvalidRequest"/> for the reason <see cref="ScalingInvalidRequest"/> is:
    /// the field names in the problem body are this request's own (<c>fromUnitId</c>, <c>toUnitId</c>).
    /// </remarks>
    public const string UnitConversionInvalidRequest = "recipes.unitConversion.invalid_request";

    /// <summary>A temperature-conversion request named a value, precision, or scale that could not be resolved.</summary>
    public const string TemperatureConversionInvalidRequest = "recipes.temperatureConversion.invalid_request";

    /// <summary>
    /// A yield-reconciliation request named a batch yield, serving count, serving size, or pan/vessel capacity
    /// that was zero or negative, and slipped past the validator's own field-specific checks.
    /// </summary>
    public const string YieldRecalculationInvalidRequest = "recipes.yieldRecalculation.invalid_request";

    /// <summary>A display-normalization request named a value, unit, or precision that could not be rendered.</summary>
    public const string DisplayNormalizationInvalidRequest = "recipes.displayNormalization.invalid_request";

    /// <summary>
    /// A test-run submission failed shape or format validation, or named a <c>testedAt</c> in the future.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RecipeInvalidRequest"/> for the reason <see cref="ScalingInvalidRequest"/> is:
    /// the field names in the problem body are this request's own — <c>sourceVersionNumber</c>,
    /// <c>testedAt</c>, <c>observations[0].text</c>, <c>issues[2].severity</c> — and not a recipe edit's. A
    /// client showing a creator where they went wrong needs the path it actually sent.
    /// </remarks>
    public const string TestRunInvalidRequest = "recipes.testRun.invalid_request";

    /// <summary>
    /// Unknown test run, or one belonging to another workspace or another recipe: deliberately
    /// indistinguishable, so a caller cannot learn a test exists by being refused it (tenancy.md).
    /// </summary>
    /// <remarks>
    /// Its own code rather than <see cref="RecipeNotFound"/>, and safe to be: by the time this can be returned
    /// the caller has been shown that the recipe exists and that they may read it, so "that test is not one of
    /// this recipe's" discloses nothing they could not learn by listing them. Collapsing the two would leave a
    /// client unable to tell "you cannot see this recipe" from "check the test id".
    /// </remarks>
    public const string TestRunNotFound = "recipes.testRun.not_found";

    /// <summary>
    /// An edit to a test run quoted a concurrency token the run has moved past: somebody else finished the
    /// write-up first, or this caller composed their change against a stale read.
    /// </summary>
    /// <remarks>
    /// Its own code rather than <see cref="RecipeConflict"/> because the resource is a different one, and a
    /// client showing "this test changed since you opened it" must not be able to confuse it with the recipe
    /// having changed. The body carries no current state, for the reason <see cref="RecipeConflict"/> gives.
    /// </remarks>
    public const string TestRunConflict = "recipes.testRun.conflict";

    /// <summary>An issue id that is not one of this test run's. The run itself was found and is readable.</summary>
    public const string TestIssueNotFound = "recipes.testIssue.not_found";

    /// <summary>
    /// A resolution request named a version that cannot be the correction, or asked for something the
    /// resolution shape does not allow.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TestRunInvalidRequest"/> because the field names in the problem body are this
    /// request's own — <c>resolutionVersionNumber</c>, <c>predatingVersionOverrideReason</c> — and a client
    /// showing a creator where they went wrong needs the path it actually sent.
    /// </remarks>
    public const string TestIssueInvalidRequest = "recipes.testIssue.invalid_request";

    /// <summary>
    /// The issue already has a resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 409 and its own code, because the remedy is specific and is not "retry": the decision has been recorded
    /// and it is immutable, so a client that treated this like a stale token would loop forever. What a creator
    /// who disagrees does instead is record another test.
    /// </para>
    /// <para>
    /// Reached only by a genuinely second resolution. A client replaying its own request with the same
    /// idempotency key is answered from the idempotency record with the original response, never with this.
    /// </para>
    /// </remarks>
    public const string TestIssueResolvedConflict = "recipes.testIssue.resolved.conflict";

    /// <summary>
    /// An edit tried to drop an issue that has already been resolved.
    /// </summary>
    /// <remarks>
    /// 409 rather than 400, because the request is well formed and would be accepted for any unresolved issue.
    /// Its own code because the remedy is particular: keep the issue in the submitted list. A resolution records
    /// a decision somebody took, and removing the issue would erase the decision along with it — which is also
    /// why the database refuses the delete when the resolution is loaded.
    /// </remarks>
    public const string TestIssueResolvedRemovalConflict = "recipes.testIssue.removal.conflict";

    public const string JsonLdExportInvalidRequest = "recipes.jsonLdExport.invalid_request";

    public const string JsonLdExportNotApprovedConflict = "recipes.jsonLdExport.notApproved.conflict";

    public const string JsonLdExportIncompleteUnprocessable = "recipes.jsonLdExport.incomplete.unprocessable";

    public const string MarkdownExportInvalidRequest = "recipes.markdownExport.invalid_request";

    public const string MarkdownExportNotApprovedConflict = "recipes.markdownExport.notApproved.conflict";

    public const string MarkdownExportIncompleteUnprocessable = "recipes.markdownExport.incomplete.unprocessable";
}
