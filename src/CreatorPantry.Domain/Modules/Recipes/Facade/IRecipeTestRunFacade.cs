using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// The application boundary for the test kitchen: recording what happened when a creator actually cooked one
/// of their recipes.
/// </summary>
/// <remarks>
/// <para>
/// A facade of its own rather than more methods on <see cref="IRecipeFacade"/>. The test kitchen is a
/// coherent sub-domain with its own aggregate, its own lifecycle and its own routes, and
/// <see cref="IRecipeFacade"/> already carries every recipe operation there is. The module has room for more
/// than one boundary — <see cref="IIngredientParsingFacade"/> is the other.
/// </para>
/// <para>
/// Like every facade here, this is the boundary reused by controllers, workers and AI plugins, which is why
/// the role check lives inside it rather than only on the MVC policy.
/// </para>
/// </remarks>
public interface IRecipeTestRunFacade
{
    /// <summary>
    /// Records one test of one exact version of a recipe.
    /// </summary>
    /// <param name="userId">The authenticated caller, for the idempotency scope. Never taken from input.</param>
    /// <param name="recipeId">The recipe under test, from the route.</param>
    /// <param name="model">The test as submitted. Carries no workspace, tester, or audit field.</param>
    /// <param name="idempotencyKey">
    /// Optional. A repeat of the same key and the same test returns the original response rather than
    /// recording a second run; the same key with a different test is refused as key reuse.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Requires <see cref="WorkspaceRole.Contributor"/>, the same bar as creating a recipe and duplicating
    /// one. Recording a test adds the creator's own material and changes no canonical content, which is what
    /// separates it from the archive and restore commands the Editor bar exists for.
    /// </remarks>
    Task<IdempotentOutcome<CreatedRecipeTestRunServiceModel>> CreateAsync(
        string userId,
        Guid recipeId,
        CreateRecipeTestRunViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes part of one recorded test and returns it as it now stands.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="WorkspaceRole.Contributor"/>, the same bar as recording one. Correcting a write-up
    /// is the same kind of act as making it, and an Editor bar here would leave a contributor able to record a
    /// test and unable to finish it.
    /// </remarks>
    Task<IdempotentOutcome<RecipeTestRunServiceModel>> UpdateAsync(
        string userId,
        Guid recipeId,
        Guid testRunId,
        UpdateRecipeTestRunViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records what was done about one issue a test found.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Requires <see cref="WorkspaceRole.Editor"/>, a step above recording a test. Resolving an issue is a
    /// judgement that a problem in the recipe has been dealt with — it is what a readiness evaluation will read
    /// and what a reviewer will trust — and the decision is immutable once written. Recording an observation is
    /// reporting; closing one is deciding.
    /// </para>
    /// <para>
    /// A separate command from the update, and atomically so: it writes one row, touches neither the issue nor
    /// the observation behind it, and needs no concurrency token because nothing is being overwritten.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<ResolvedTestIssueServiceModel>> ResolveIssueAsync(
        string userId,
        Guid recipeId,
        Guid testRunId,
        Guid issueId,
        ResolveTestIssueViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one recorded test whole — its notes, its problems, and what was decided about each.
    /// </summary>
    /// <param name="recipeId">The recipe the test belongs to, from the route.</param>
    /// <param name="testRunId">The test to read, from the route.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The test, or <c>recipes.recipe.not_found</c> for a recipe the caller may not see, or
    /// <c>recipes.testRun.not_found</c> for a test that is not one of that recipe's.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A read, so no idempotency key and nothing to replay. The lowest role there is, like the history: every
    /// member may read what the workspace's testers recorded. There is no validation step either — both ids are
    /// route-bound Guids, so there is no shape left to refuse.
    /// </para>
    /// <para>
    /// <strong>The companion to <see cref="ListAsync"/>, and the reason it can stay summaries.</strong> A row
    /// carries counts where a test has notes and problems; this is where their contents live, so a client reads
    /// the history to choose and this to open. It is also what makes a correction possible from anything other
    /// than the response to a write: the concurrency token an edit has to quote comes back here.
    /// </para>
    /// <para>
    /// <strong>No tester name, unlike a history row.</strong> A row is a list entry a reader scans, so it is worth
    /// one lookup per page to name the person on it; this is one test the reader has already chosen, and the
    /// membership ids it carries are the same ones the history publishes. Adding the name would mean a Tenancy
    /// call on every read of a test for a line the client already has beside it.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeTestRunServiceModel>> GetAsync(
        Guid recipeId,
        Guid testRunId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists one recipe's recorded tests, most recently cooked first, filtered and paged.
    /// </summary>
    /// <param name="recipeId">The recipe whose history to read, from the route.</param>
    /// <param name="model">
    /// The filters, cursor and page size. Carries no workspace and no recipe — see
    /// <see cref="TestRunHistoryViewModel"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// The page with its counts, or a refusal: <c>recipes.testRun.invalid_request</c> for a filter that cannot be
    /// read, <c>recipes.cursor.invalid_request</c> for a cursor issued for a different workspace, recipe or set of
    /// filters, and <c>recipes.recipe.not_found</c> for a recipe the caller may not see.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A read, so there is no idempotency key and nothing to replay. The lowest role there is — every member of a
    /// workspace may read what its testers recorded; deciding an issue is closed is what needs an Editor.
    /// </para>
    /// <para>
    /// <strong>The names are resolved here and nowhere below.</strong> Business hands back the tester memberships
    /// it could not spend, and this is the only layer that may exchange them for people — facade to facade is the
    /// only way into Tenancy.
    /// </para>
    /// </remarks>
    Task<OperationResult<TestRunHistoryPageServiceModel>> ListAsync(
        Guid recipeId,
        TestRunHistoryViewModel model,
        CancellationToken cancellationToken);
}

internal sealed class RecipeTestRunFacade(
    IValidator<CreateRecipeTestRunViewModel> createValidator,
    IValidator<UpdateRecipeTestRunViewModel> updateValidator,
    IValidator<ResolveTestIssueViewModel> resolveValidator,
    IValidator<TestRunHistoryViewModel> historyValidator,
    IRecipeTestRunBusiness business,
    IWorkspaceContext workspace,
    IMeasurementFacade measurement,
    IWorkspaceFacade workspaces,
    IIdempotentCommandExecutor idempotency) : IRecipeTestRunFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string CreateOperation = "recipes.testRuns.create";

    private const string UpdateOperation = "recipes.testRuns.update";

    private const string ResolveIssueOperation = "recipes.testRuns.resolveIssue";

    private const string CannotRecord = "That test could not be recorded as described.";

    private const string CannotUpdate = "That test could not be changed as described.";

    private const string CannotResolve = "That issue could not be resolved as described.";

    private const string CannotListHistory = "That test history cannot be read as described.";

    public async Task<IdempotentOutcome<CreatedRecipeTestRunServiceModel>> CreateAsync(
        string userId,
        Guid recipeId,
        CreateRecipeTestRunViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Authorization first, so a caller who may not do this learns that rather than which of their fields
        // is invalid. Checked here and not only at the controller policy because this boundary is also reached
        // by workers and AI plugins, which no MVC policy protects.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused<CreatedRecipeTestRunServiceModel>(
                RecipeErrorCodes.RecipeForbidden,
                "You do not have permission to record tests in this workspace.");
        }

        var validation = await createValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused<CreatedRecipeTestRunServiceModel>(OperationError.Validation(
                RecipeErrorCodes.TestRunInvalidRequest,
                CannotRecord,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // One value, two uses: what gets hashed as the fingerprint and what Business reads the request from.
        // Canonicalizing here rather than hashing the raw body is what makes "same fingerprint" and "same
        // test" the same statement instead of two that can drift.
        var canonical = CanonicalCreateTestRun.From(model);

        // The one cross-module reference this request can carry. Verified facade to facade, which is both the
        // only way this module may read the unit catalogue and the only way to learn the dimension that has to
        // be stored beside the id — nothing below the facade will do it, and an unverified id becomes a
        // foreign-key violation at save time, a 500 where the honest answer is a 400 naming the field.
        MeasurementDimension? yieldUnitDimension = null;
        if (canonical.ActualYieldUnitId is { } unitId)
        {
            yieldUnitDimension = await measurement.FindUsableUnitDimensionAsync(unitId, cancellationToken);
            if (yieldUnitDimension is null)
            {
                return Refused<CreatedRecipeTestRunServiceModel>(OperationError.Validation(
                    RecipeErrorCodes.TestRunInvalidRequest,
                    CannotRecord,
                    [(nameof(CreateRecipeTestRunViewModel.ActualYieldUnitId), "That unit is not available.")]));
            }
        }

        // No asset verification, because nothing here names an asset. This route accepts no attachments while
        // there is no media facade to authorize one against — see CreateRecipeTestRunViewModel.

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                CreateOperation,
                idempotencyKey,
                // The recipe and everything the caller recorded — see CanonicalCreateTestRun.Fingerprint.
                Fingerprint: canonical.Fingerprint(recipeId),
                // Accepted, not required, as on the create and duplicate routes: api-contract.md says
                // retryable commands accept a key, and demanding one would refuse every client that does not
                // send one. Worth sending here as much as on a duplicate — without it, a retry after a lost
                // response leaves the creator with two records of one bake.
                KeyRequired: false),
            token => business.CreateAsync(recipeId, canonical, yieldUnitDimension, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<RecipeTestRunServiceModel>> UpdateAsync(
        string userId,
        Guid recipeId,
        Guid testRunId,
        UpdateRecipeTestRunViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused<RecipeTestRunServiceModel>(
                RecipeErrorCodes.RecipeForbidden,
                "You do not have permission to change tests in this workspace.");
        }

        var validation = await updateValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused<RecipeTestRunServiceModel>(OperationError.Validation(
                RecipeErrorCodes.TestRunInvalidRequest,
                CannotUpdate,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var canonical = CanonicalUpdateTestRun.From(model);

        // Only when the body named a unit, and only to learn its dimension. A submitted null is a request to
        // clear the pair, which needs no lookup and must not be turned into one.
        MeasurementDimension? yieldUnitDimension = null;
        if (canonical.ActualYieldUnitId.IsSubmitted && canonical.ActualYieldUnitId.Value is { } unitId)
        {
            yieldUnitDimension = await measurement.FindUsableUnitDimensionAsync(unitId, cancellationToken);
            if (yieldUnitDimension is null)
            {
                return Refused<RecipeTestRunServiceModel>(OperationError.Validation(
                    RecipeErrorCodes.TestRunInvalidRequest,
                    CannotUpdate,
                    [(nameof(UpdateRecipeTestRunViewModel.ActualYieldUnitId), "That unit is not available.")]));
            }
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                UpdateOperation,
                idempotencyKey,
                // The run, the token it was composed against, and every field it asked to change — see
                // CanonicalUpdateTestRun.Fingerprint.
                Fingerprint: canonical.Fingerprint(testRunId),
                KeyRequired: false),
            token => business.UpdateAsync(recipeId, testRunId, canonical, yieldUnitDimension, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<ResolvedTestIssueServiceModel>> ResolveIssueAsync(
        string userId,
        Guid recipeId,
        Guid testRunId,
        Guid issueId,
        ResolveTestIssueViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Editor, a step above recording a test — see the interface remarks for why closing an issue is a
        // different kind of act from reporting one.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Refused<ResolvedTestIssueServiceModel>(
                RecipeErrorCodes.RecipeForbidden,
                "You do not have permission to resolve test issues in this workspace.");
        }

        var validation = await resolveValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused<ResolvedTestIssueServiceModel>(OperationError.Validation(
                RecipeErrorCodes.TestIssueInvalidRequest,
                CannotResolve,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var canonical = CanonicalResolveTestIssue.From(model);

        // No unit and no asset to verify: a resolution names a version, and Business resolves that through the
        // workspace-filtered history rather than through another module.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                ResolveIssueOperation,
                idempotencyKey,
                Fingerprint: canonical.Fingerprint(issueId),
                // Accepted, not required — but worth sending more than on most routes. A resolution is
                // write-once, so a retry after a lost response is otherwise answered "already resolved" by the
                // caller's own earlier attempt, which reads as somebody else having decided.
                KeyRequired: false),
            token => business.ResolveIssueAsync(recipeId, testRunId, issueId, canonical, token),
            cancellationToken);
    }

    /// <remarks>
    /// Straight through, and deliberately so: nothing is validated because both ids are route-bound Guids,
    /// nothing is cached because a test moves whenever anybody corrects it, and nothing is enriched because
    /// this route publishes no name. A facade that adds nothing is still the boundary — workers and AI plugins
    /// reach this seam here, not through Business.
    /// </remarks>
    public Task<OperationResult<RecipeTestRunServiceModel>> GetAsync(
        Guid recipeId,
        Guid testRunId,
        CancellationToken cancellationToken) =>
        business.GetAsync(recipeId, testRunId, cancellationToken);

    public async Task<OperationResult<TestRunHistoryPageServiceModel>> ListAsync(
        Guid recipeId,
        TestRunHistoryViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await historyValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            // The cursor's own code when that is what failed, so a paging client knows to start again rather than
            // retrying a cursor that will never be accepted. The property names come from the validator's
            // OverridePropertyName, which is what keeps this matching the query parameter a caller sent.
            var code = validation.Errors.Any(failure =>
                failure.PropertyName.Equals("cursor", StringComparison.Ordinal))
                ? RecipeErrorCodes.CursorInvalidRequest
                : RecipeErrorCodes.TestRunInvalidRequest;

            return OperationResult<TestRunHistoryPageServiceModel>.Failure(OperationError.Validation(
                code,
                CannotListHistory,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The workspace comes from the resolved context and the recipe from the route; the model has a field for
        // neither. Both are bound into the cursor's scope, so a cursor cannot be replayed across recipes any more
        // than across workspaces.
        if (!TestRunHistoryQueryFactory.TryCreate(
            model, workspace.WorkspaceId, recipeId, out var criteria, out var error))
        {
            return OperationResult<TestRunHistoryPageServiceModel>.Failure(error!);
        }

        var result = await business.ListAsync(criteria!, cancellationToken);

        return result.Succeeded
            ? OperationResult<TestRunHistoryPageServiceModel>.Success(
                await WithTestersAsync(result.Value!, cancellationToken))
            : OperationResult<TestRunHistoryPageServiceModel>.Failure(result.Error!);
    }

    /// <summary>
    /// Exchanges each row's tester membership for the display name of the person behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same enrichment, in the same place and for the same reasons, as the version history's authorship: the
    /// ids are not knowable until the page has been read, and Business cannot spend them because naming a
    /// membership means reading Tenancy's rows and Auth's beyond them. Facade to facade is the only way across.
    /// </para>
    /// <para>
    /// <strong>One lookup per page, not per row.</strong> The distinct testers of a page of history are usually one
    /// or two people, so this is a single read whatever the page size.
    /// </para>
    /// <para>
    /// <strong>An unresolved membership leaves the name null rather than failing the read.</strong> A test outlives
    /// the membership that cooked it by design — authorship is recorded precisely so it survives someone leaving —
    /// so a history that refused to load because a tester had gone would punish the creator for a collaborator's
    /// departure. The id stays either way, so the filter still works on a tester nobody can name.
    /// </para>
    /// </remarks>
    private async Task<TestRunHistoryPageServiceModel> WithTestersAsync(
        TestRunHistoryPageResult result,
        CancellationToken cancellationToken)
    {
        if (result.Page.Items.Count == 0)
        {
            return result.Page;
        }

        var names = await workspaces.FindMemberDisplayNamesAsync(
            [.. result.TesterMembershipByTestRunId.Values.Distinct()], cancellationToken);

        return result.Page with
        {
            Items =
            [
                .. result.Page.Items.Select(row => row with
                {
                    TestedByName =
                        result.TesterMembershipByTestRunId.TryGetValue(row.Id, out var membershipId)
                        && names.TryGetValue(membershipId, out var name)
                            ? name
                            : null,
                }),
            ],
        };
    }

    private static IdempotentOutcome<T> Refused<T>(string code, string message) =>
        Refused<T>(new OperationError(code, message, new Dictionary<string, string[]>()));

    private static IdempotentOutcome<T> Refused<T>(OperationError error) =>
        new(OperationResult<T>.Failure(error), Replayed: false);
}
