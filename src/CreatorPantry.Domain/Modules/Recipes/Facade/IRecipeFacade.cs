using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Vocabulary.Facade;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// The application boundary for recipes, reused by controllers, workers and AI plugins alike.
/// </summary>
public interface IRecipeFacade
{
    /// <summary>Creates a recipe in the workspace resolved for this scope.</summary>
    /// <param name="idempotencyKey">
    /// The caller's <c>Idempotency-Key</c>, when one was sent. A replay of the same key and the same payload
    /// returns the original outcome without creating a second recipe or a second version.
    /// </param>
    Task<IdempotentOutcome<CreatedRecipeServiceModel>> CreateAsync(
        string userId,
        CreateRecipeViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one recipe of the workspace resolved for this scope.
    /// </summary>
    /// <returns>
    /// The recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> — the single answer for
    /// a recipe that does not exist and one that belongs to another workspace.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>No role gate, deliberately.</strong> <see cref="WorkspaceRole.Viewer"/> is <c>0</c>, so every
    /// resolved membership already satisfies "may read", and a <c>Role &lt; Viewer</c> check would be a branch
    /// that can never be taken. Having a resolved workspace context <em>is</em> the authorization here: it
    /// exists only because a slug was matched to an active membership. The route also carries the
    /// <c>WorkspaceViewer</c> policy, which matters for the HTTP caller and not for this method.
    /// </para>
    /// <para>
    /// <strong>Not cached.</strong> A recipe is read far more often than it is written, so a cache is tempting
    /// — but it would hand every future write seam an invalidation obligation, and the first one to forget it
    /// would show a creator their own edit missing from their own recipe. No module caches yet; when one does,
    /// this is the layer that would.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a partial edit to one recipe of the workspace resolved for this scope, and returns the recipe
    /// as it now stands.
    /// </summary>
    /// <param name="recipeId">The recipe to edit, resolved from the route.</param>
    /// <param name="model">
    /// The fields the caller is changing. Unmentioned fields are left alone; see
    /// <see cref="UpdateRecipeViewModel"/> for the three states a field can be in.
    /// </param>
    /// <param name="idempotencyKey">
    /// The caller's <c>Idempotency-Key</c>, when one was sent. Optional, and worth sending: without it, a
    /// client whose edit committed but whose response was lost retries, is told the token is stale, and
    /// cannot tell "someone else edited this" from "my own edit already landed". With it, the retry returns
    /// the original answer.
    /// </param>
    /// <returns>
    /// The edited recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeForbidden"/>,
    /// <see cref="RecipeErrorCodes.RecipeInvalidRequest"/>, <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    Task<IdempotentOutcome<RecipeDetailServiceModel>> UpdateAsync(
        string userId,
        Guid recipeId,
        UpdateRecipeViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies changes a creator accepted from an AI proposal, as one new version that records the proposal it
    /// came from.
    /// </summary>
    /// <param name="expectedVersionId">
    /// The version the proposal was computed against. A recipe that has moved past it is a conflict, never a
    /// silent rebase.
    /// </param>
    /// <param name="aiProposalId">The proposal the creator accepted, recorded on the version.</param>
    /// <param name="changes">The accepted changes, translated into this module's vocabulary by the caller.</param>
    /// <returns>
    /// The edited recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeForbidden"/>,
    /// <see cref="RecipeErrorCodes.RecipeNotFound"/>, <see cref="RecipeErrorCodes.RecipeInvalidRequest"/>,
    /// <see cref="RecipeErrorCodes.RecipeConflict"/> or
    /// <see cref="RecipeErrorCodes.RecipeArchivedConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The application boundary an accepted proposal comes through</strong>, and the only one. The AI
    /// module has no other way into a recipe: it cannot reach this module's repositories, data layer or business
    /// rules, so an accepted change is subject to the same role check, the same invariants and the same archived
    /// refusal as an edit a creator typed. That is what AIREC-GR-007 asks for, expressed structurally rather
    /// than promised in a comment.
    /// </para>
    /// <para>
    /// <strong>No idempotency key, and no role for one.</strong> Unlike <see cref="UpdateAsync"/>, this is never
    /// called by a client — the caller is the AI module, inside a transaction it owns, having already decided
    /// that this proposal has not been dispositioned before. A second idempotency record around that would be
    /// guarding a decision already guarded, and would need its own scope and key to do it.
    /// </para>
    /// <para>
    /// <strong>It does not save on its own terms.</strong> The write happens on the request's shared
    /// <c>DbContext</c>, so it enlists in whatever transaction the caller has open — which is what lets a
    /// recipe version and the proposal's dispositions commit together or not at all.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> ApplyProposedChangesAsync(
        Guid recipeId,
        Guid expectedVersionId,
        Guid aiProposalId,
        IReadOnlyList<ProposedRecipeChange> changes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of the recipes of the workspace resolved for this scope.
    /// </summary>
    /// <returns>
    /// The page, or a failure carrying <see cref="RecipeErrorCodes.SearchInvalidRequest"/> when a filter cannot
    /// be parsed, or <see cref="RecipeErrorCodes.CursorInvalidRequest"/> when the cursor was issued for a
    /// different workspace, ordering or set of filters.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>No role gate</strong>, and <strong>no 404</strong>. Viewer is the lowest role, so a resolved
    /// context is already the authorization; and an empty library and filters that match nothing are both an
    /// empty page. There is nothing here whose existence needs hiding — the caller's membership of this
    /// workspace is not in doubt by the time this runs.
    /// </para>
    /// <para>
    /// <strong>Not cached.</strong> The restriction on this route permits a cache only with explicit
    /// workspace-scoped invalidation, and a search is the worst possible first candidate: its key would have to
    /// include every filter, ordering and cursor, so one recipe edit would have to invalidate an unbounded family
    /// of keys that no write seam can enumerate. <c>CachedPageReader</c> is doubly unavailable — it keys through
    /// <c>CacheKeys.Global</c>, which would put one workspace's recipe titles where every other workspace reads.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeSearchPageServiceModel>> SearchAsync(
        RecipeSearchViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of one recipe's history, newest first, in the workspace resolved for this scope.
    /// </summary>
    /// <param name="recipeId">The recipe whose history to read, resolved from the route.</param>
    /// <returns>
    /// The page, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> when the recipe is not
    /// visible here, or <see cref="RecipeErrorCodes.CursorInvalidRequest"/> when the cursor was issued for a
    /// different recipe or workspace.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>No role gate</strong>, for the reason <see cref="GetDetailAsync"/> gives: <c>Viewer</c> is
    /// <c>0</c>, so a resolved workspace context already is the authorization, and someone who may read a
    /// recipe may read how it came to say what it says.
    /// </para>
    /// <para>
    /// <strong>Not cached</strong>, although this is the most cacheable read in the module — the rows are
    /// immutable, so only the newest page can ever change. It is still not worth it: every edit would have to
    /// invalidate an unbounded family of cursor-keyed entries, and a creator seeing their own save missing
    /// from their own history is precisely the failure a cache must not introduce. <see cref="GetDetailAsync"/>
    /// makes the same call for the same reason.
    /// </para>
    /// </remarks>
    Task<OperationResult<CursorPageServiceModel<RecipeVersionHistoryServiceModel>>> GetVersionHistoryAsync(
        Guid recipeId,
        RecipeVersionHistoryViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Compares two of one recipe's versions in the workspace resolved for this scope.
    /// </summary>
    /// <param name="recipeId">The recipe whose versions to compare, resolved from the route.</param>
    /// <param name="model">Which two versions, by number. Carries no workspace and no recipe.</param>
    /// <returns>
    /// The comparison, or a failure carrying <see cref="RecipeErrorCodes.ComparisonInvalidRequest"/> when the
    /// query does not name two version numbers, <see cref="RecipeErrorCodes.RecipeNotFound"/> when the recipe
    /// is not visible here, or <see cref="RecipeErrorCodes.VersionNotFound"/> when one of the numbers names no
    /// version of it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>No role gate</strong>, for the reason <see cref="GetDetailAsync"/> gives: <c>Viewer</c> is
    /// <c>0</c>, so a resolved workspace context already is the authorization, and someone who may read a
    /// recipe may read what changed between two of its versions.
    /// </para>
    /// <para>
    /// <strong>Read-only and uncached.</strong> Both versions are immutable, so this is the most cacheable read
    /// in the module and is still not cached — see <see cref="GetVersionHistoryAsync"/> for why. Nothing here
    /// writes, and comparing two versions leaves no record that it happened.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid recipeId,
        RecipeVersionComparisonViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic scaling preview for one recipe of the workspace resolved for this scope, by
    /// multiplier or by target yield, against one explicit version (ING-003).
    /// </summary>
    /// <param name="recipeId">The recipe to read from, resolved from the route.</param>
    /// <param name="model">Which version to scale, and by how much.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.ScalingInvalidRequest"/>,
    /// <see cref="RecipeErrorCodes.RecipeNotFound"/>, or <see cref="RecipeErrorCodes.VersionNotFound"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Read-only and uncached</strong>, for the same reasons as <see cref="CompareVersionsAsync"/>:
    /// nothing here writes, and the version scaled is immutable, so caching would trade a cheap calculation for
    /// an invalidation obligation nothing yet needs.
    /// </para>
    /// <para>
    /// <strong>No role gate</strong>, matching every other read in this module — see <see cref="GetDetailAsync"/>.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeScalingResultServiceModel>> ScaleAsync(
        Guid recipeId,
        ScaleRecipeViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic unit-conversion preview for one recipe of the workspace resolved for this
    /// scope, in the context of one explicit version (ING-004).
    /// </summary>
    /// <param name="recipeId">The recipe to read from, resolved from the route.</param>
    /// <param name="model">Which version, the quantity to convert, and the units to convert between.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.UnitConversionInvalidRequest"/>,
    /// <see cref="RecipeErrorCodes.RecipeNotFound"/>, or <see cref="RecipeErrorCodes.VersionNotFound"/>.
    /// </returns>
    /// <remarks>
    /// <strong>Read-only, uncached, and no role gate</strong> — the same reasoning as <see cref="ScaleAsync"/>.
    /// Both units are resolved here, not in Business: they come from the request rather than the recipe, and
    /// resolving another module's data is a facade-to-facade call Business may not make (backend.md).
    /// </remarks>
    Task<OperationResult<RecipeUnitConversionResultServiceModel>> ConvertUnitsAsync(
        Guid recipeId,
        ConvertUnitsViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic temperature-conversion preview for one recipe of the workspace resolved for
    /// this scope, in the context of one explicit version (CALC-003).
    /// </summary>
    /// <param name="recipeId">The recipe to read from, resolved from the route.</param>
    /// <param name="model">Which version, the structured value, and the scales to convert between.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.TemperatureConversionInvalidRequest"/>,
    /// <see cref="RecipeErrorCodes.RecipeNotFound"/>, or <see cref="RecipeErrorCodes.VersionNotFound"/>.
    /// </returns>
    /// <remarks>
    /// <strong>Read-only, uncached, and no role gate</strong> — the same reasoning as <see cref="ScaleAsync"/>.
    /// No cross-module lookup: a temperature scale is an enum, not a reference id.
    /// </remarks>
    Task<OperationResult<RecipeTemperatureConversionResultServiceModel>> ConvertTemperatureAsync(
        Guid recipeId,
        ConvertTemperatureViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic yield-reconciliation preview for one recipe of the workspace resolved for this
    /// scope, in the context of one explicit version (ING-005).
    /// </summary>
    /// <param name="recipeId">The recipe to read from, resolved from the route.</param>
    /// <param name="model">Which version, and the creator's explicit batch yield, serving count, serving size, and pan capacity.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.YieldRecalculationInvalidRequest"/>,
    /// <see cref="RecipeErrorCodes.RecipeNotFound"/>, or <see cref="RecipeErrorCodes.VersionNotFound"/>.
    /// </returns>
    /// <remarks>
    /// <strong>Read-only, uncached, and no role gate</strong> — the same reasoning as <see cref="ScaleAsync"/>.
    /// No cross-module lookup: Business resolves the dimension itself from the recipe's own stored yield unit.
    /// </remarks>
    Task<OperationResult<RecipeYieldReconciliationResultServiceModel>> RecalculateYieldAsync(
        Guid recipeId,
        RecalculateYieldViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Computes a deterministic display-normalization preview for one recipe of the workspace resolved for
    /// this scope, in the context of one explicit version (ING-006).
    /// </summary>
    /// <param name="recipeId">The recipe to read from, resolved from the route.</param>
    /// <param name="model">Which version, the value (and optional range upper bound), the unit, and the presentation choices.</param>
    /// <returns>
    /// The computed preview, or a failure carrying <see cref="RecipeErrorCodes.DisplayNormalizationInvalidRequest"/>,
    /// <see cref="RecipeErrorCodes.RecipeNotFound"/>, or <see cref="RecipeErrorCodes.VersionNotFound"/>.
    /// </returns>
    /// <remarks>
    /// <strong>Read-only, uncached, and no role gate</strong> — the same reasoning as <see cref="ScaleAsync"/>.
    /// The unit is resolved here, not in Business, for the same reason <see cref="ConvertUnitsAsync"/> gives.
    /// </remarks>
    Task<OperationResult<RecipeQuantityDisplayResultServiceModel>> NormalizeDisplayAsync(
        Guid recipeId,
        NormalizeDisplayViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Puts one recipe of the workspace resolved for this scope back to what one of its versions said, and
    /// returns the recipe as it now stands.
    /// </summary>
    /// <param name="recipeId">The recipe to restore, resolved from the route.</param>
    /// <param name="versionNumber">Which version's content to put back, resolved from the route.</param>
    /// <param name="model">
    /// The state the restore was composed against and why the creator did it. Carries no workspace, no
    /// recipe, no version and no content.
    /// </param>
    /// <param name="idempotencyKey">
    /// The caller's <c>Idempotency-Key</c>, when one was sent. Optional, and worth sending: without it, a
    /// client whose restore committed but whose response was lost retries, is told the token is stale, and
    /// cannot tell "someone else edited this" from "my own restore already landed". With it, the retry
    /// returns the original answer.
    /// </param>
    /// <returns>
    /// The restored recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeForbidden"/>,
    /// <see cref="RecipeErrorCodes.RecipeInvalidRequest"/>, <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.VersionNotFound"/> or <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Editor, not Contributor</strong> — the bar an edit clears. Restoring is not one more edit: it
    /// discards, in one request and without naming them, every change made since the version chosen, and
    /// whoever made those changes was permitted to. That is a judgement about a recipe's history rather than
    /// a contribution to its content, and REC-009 puts it with the roles that curate.
    /// </para>
    /// <para>
    /// <strong>No cross-module reference verification</strong>, unlike <see cref="CreateAsync"/> and
    /// <see cref="UpdateAsync"/>, which check every submitted vocabulary id before Business is reached. There
    /// is nothing submitted here to check: the ids come from the recipe's own archive.
    /// <see cref="IRecipeBusiness.RestoreVersionAsync"/> records why they are restored as they stand.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<RecipeDetailServiceModel>> RestoreVersionAsync(
        string userId,
        Guid recipeId,
        int versionNumber,
        RestoreRecipeVersionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Copies one recipe of the workspace resolved for this scope into a new, independent recipe.
    /// </summary>
    /// <param name="recipeId">The recipe to copy from, resolved from the route.</param>
    /// <param name="model">The copy's title and the version to copy. Carries no workspace and no content.</param>
    /// <param name="idempotencyKey">
    /// The caller's <c>Idempotency-Key</c>, when one was sent. A replay of the same key and the same request
    /// returns the original outcome without creating a second recipe.
    /// </param>
    /// <returns>
    /// The new recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeForbidden"/>,
    /// <see cref="RecipeErrorCodes.RecipeInvalidRequest"/>, <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.VersionNotFound"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Contributor, the same bar as <see cref="CreateAsync"/>.</strong> A duplicate is a new recipe,
    /// and someone permitted to add recipes to a workspace is permitted to start one from an existing recipe
    /// they can already read. It is deliberately <em>not</em> the Editor bar
    /// <see cref="RestoreVersionAsync"/> carries: that one discards a collaborator's work, while this one
    /// takes nothing away from anybody.
    /// </para>
    /// <para>
    /// <strong>No concurrency token and no conflict</strong> — the only recipe write of which that is true.
    /// See <see cref="DuplicateRecipeViewModel"/>.
    /// </para>
    /// <para>
    /// <strong>No cross-module reference verification</strong>, for the reason
    /// <see cref="RestoreVersionAsync"/> gives: there is nothing submitted here to verify, because every id
    /// the copy writes comes from the source recipe's own archive.
    /// </para>
    /// </remarks>
    Task<IdempotentOutcome<CreatedRecipeServiceModel>> DuplicateAsync(
        string userId,
        Guid recipeId,
        DuplicateRecipeViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Shelves one recipe of the workspace resolved for this scope, and returns it as it now stands.
    /// </summary>
    /// <param name="recipeId">The recipe to archive, resolved from the route.</param>
    /// <param name="model">The state the command was composed against. Carries nothing else.</param>
    /// <returns>
    /// The recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeForbidden"/>,
    /// <see cref="RecipeErrorCodes.RecipeInvalidRequest"/>, <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Editor, the bar a restore carries and an edit does not.</strong> Archiving takes a recipe out
    /// of everyone's library and stops every collaborator from editing it — a judgement about whether the
    /// workspace is done with a recipe, not a contribution to one. REC-006 names that role and it is the
    /// same reasoning <see cref="RestoreVersionAsync"/> gives.
    /// </para>
    /// <para>
    /// <strong>No idempotency key, and none is needed.</strong> A repeat is answered as the no-op it is
    /// rather than by creating anything or refusing anything — which is the guarantee a key would have
    /// bought. See <see cref="IRecipeBusiness.ArchiveAsync"/>.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(
        string userId,
        Guid recipeId,
        RecipeLifecycleViewModel model,
        CancellationToken cancellationToken);

    /// <summary>
    /// Brings one recipe of the workspace resolved for this scope back from the archive.
    /// </summary>
    /// <inheritdoc cref="ArchiveAsync" path="/param"/>
    /// <returns>
    /// The recipe, or a failure carrying the same codes <see cref="ArchiveAsync"/> returns.
    /// </returns>
    /// <remarks>The mirror of <see cref="ArchiveAsync"/>, at the same role bar and with the same shape.</remarks>
    Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(
        string userId,
        Guid recipeId,
        RecipeLifecycleViewModel model,
        CancellationToken cancellationToken);
}

internal sealed class RecipeFacade(
    IValidator<CreateRecipeViewModel> createValidator,
    IValidator<UpdateRecipeViewModel> updateValidator,
    IValidator<RecipeSearchViewModel> searchValidator,
    IValidator<RecipeVersionHistoryViewModel> historyValidator,
    IValidator<RecipeVersionComparisonViewModel> comparisonValidator,
    IValidator<ScaleRecipeViewModel> scaleValidator,
    IValidator<ConvertUnitsViewModel> convertUnitsValidator,
    IValidator<ConvertTemperatureViewModel> convertTemperatureValidator,
    IValidator<RecalculateYieldViewModel> recalculateYieldValidator,
    IValidator<NormalizeDisplayViewModel> normalizeDisplayValidator,
    IValidator<RestoreRecipeVersionViewModel> restoreValidator,
    IValidator<DuplicateRecipeViewModel> duplicateValidator,
    IValidator<RecipeLifecycleViewModel> lifecycleValidator,
    IRecipeBusiness business,
    IWorkspaceContext workspace,
    IVocabularyFacade vocabulary,
    IWorkspaceFacade workspaces,
    IMeasurementFacade measurement,
    IIngredientFacade ingredients,
    IIdempotentCommandExecutor idempotency) : IRecipeFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string CreateOperation = "recipes.create";

    private const string UpdateOperation = "recipes.update";

    private const string RestoreOperation = "recipes.restoreVersion";

    private const string DuplicateOperation = "recipes.duplicate";

    private const string CannotCreate = "That recipe cannot be created as described.";

    private const string CannotChange = "That recipe cannot be changed as described.";

    private const string CannotSearch = "That search cannot be run as described.";

    private const string CannotListHistory = "That history cannot be read as described.";

    private const string CannotCompare = "Those versions cannot be compared as described.";

    private const string CannotScale = "That recipe cannot be scaled as described.";

    private const string CannotConvertUnits = "That quantity cannot be converted as described.";

    private const string CannotConvertTemperature = "That temperature cannot be converted as described.";

    private const string CannotRecalculateYield = "That yield cannot be recalculated as described.";

    private const string CannotNormalizeDisplay = "That value cannot be rendered as described.";

    private const string CannotRestore = "That version cannot be restored as described.";

    private const string CannotDuplicate = "That recipe cannot be copied as described.";

    private const string CannotArchive = "That recipe cannot be archived as described.";

    private const string CannotUnarchive = "That recipe cannot be brought back as described.";

    public async Task<OperationResult<RecipeSearchPageServiceModel>> SearchAsync(
        RecipeSearchViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await searchValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            // The cursor's own code when that is what failed, so a paging client knows to start again rather
            // than retrying a cursor that will never be accepted. The property names come from the validator's
            // OverridePropertyName, which is what keeps this matching on the query parameter a caller sent.
            var code = validation.Errors.Any(failure =>
                failure.PropertyName.Equals("cursor", StringComparison.Ordinal))
                ? RecipeErrorCodes.CursorInvalidRequest
                : RecipeErrorCodes.SearchInvalidRequest;

            return OperationResult<RecipeSearchPageServiceModel>.Failure(OperationError.Validation(
                code,
                CannotSearch,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The workspace and the caller's membership come from the resolved context, never from the model —
        // which has no field for either. The workspace is bound into the cursor's scope so that a cursor cannot
        // be replayed across workspaces; the membership is what `mine` filters on.
        if (!RecipeSearchQueryFactory.TryCreate(
            model, workspace.WorkspaceId, workspace.MembershipId, out var criteria, out var error))
        {
            return OperationResult<RecipeSearchPageServiceModel>.Failure(error!);
        }

        return OperationResult<RecipeSearchPageServiceModel>.Success(
            await business.SearchAsync(criteria!, cancellationToken));
    }

    public async Task<IdempotentOutcome<CreatedRecipeServiceModel>> CreateAsync(
        string userId,
        CreateRecipeViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Authorization first, so a caller who may not do this learns that rather than which of their ids is
        // invalid. Checked here and not only at the controller policy because this boundary is also reached
        // by workers and AI plugins, which no MVC policy protects.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused<CreatedRecipeServiceModel>(
                RecipeErrorCodes.RecipeForbidden, "You do not have permission to add recipes to this workspace.");
        }

        var validation = await createValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused<CreatedRecipeServiceModel>(OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                CannotCreate,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // Cross-module reference checks. This is the obligation IRecipeBusiness records: nothing below the
        // facade will do it, and an unverified id becomes a foreign-key violation at save time — a 500 where
        // the honest answer is a 400 naming the field. Facade to facade is also the only way this module is
        // permitted to read another's data.
        var (references, yieldUnitDimension) = await VerifyReferencesAsync(
            model.CuisineId, model.CourseId, model.PrimaryTechniqueId, model.YieldUnitId, CannotCreate, cancellationToken);
        if (references is not null)
        {
            return Refused<CreatedRecipeServiceModel>(references);
        }

        // One value, two uses: it is what gets hashed as the fingerprint and what Business maps the aggregate
        // from. That is the point of canonicalizing here rather than hashing the raw body — "same
        // fingerprint" and "same recipe" are then the same statement, instead of two that can drift.
        var canonical = CanonicalCreateRecipe.From(model);

        if (await VerifyInstructionReferencesAsync(canonical.Instructions, CannotCreate, cancellationToken) is { } instructionError)
        {
            return Refused<CreatedRecipeServiceModel>(instructionError);
        }

        var (ingredientError, ingredientUnitDimensions) = await VerifyIngredientReferencesAsync(
            canonical.IngredientGroups, CannotCreate, cancellationToken);
        if (ingredientError is not null)
        {
            return Refused<CreatedRecipeServiceModel>(ingredientError);
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                CreateOperation,
                idempotencyKey,
                // Two requests that would produce the same recipe replay; two that would not are reported as
                // key reuse rather than silently returning the first one's result.
                Fingerprint: canonical,
                // Accepted, not required. api-contract.md says retryable commands "accept an idempotency key";
                // demanding one would refuse every client that does not send one, and a create is useful
                // without the protection. A caller who wants exactly-once semantics opts in by sending a key.
                KeyRequired: false),
            token => business.CreateAsync(canonical, yieldUnitDimension, ingredientUnitDimensions, token),
            cancellationToken);
    }

    public Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(
        Guid recipeId,
        CancellationToken cancellationToken) =>
        business.GetDetailAsync(recipeId, cancellationToken);

    public async Task<OperationResult<CursorPageServiceModel<RecipeVersionHistoryServiceModel>>> GetVersionHistoryAsync(
        Guid recipeId,
        RecipeVersionHistoryViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await historyValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            // Only the cursor can fail here, so there is no code to choose between: an unreadable cursor is
            // always the cursor's own refusal, telling a paging client to start again rather than to retry
            // something that will never be accepted.
            return OperationResult<CursorPageServiceModel<RecipeVersionHistoryServiceModel>>.Failure(
                OperationError.Validation(
                    RecipeErrorCodes.CursorInvalidRequest,
                    CannotListHistory,
                    validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The workspace comes from the resolved context and the recipe from the route; the model has a field
        // for neither. Both are bound into the cursor's scope, so a cursor cannot be replayed across recipes
        // any more than across workspaces.
        if (!RecipeVersionHistoryQueryFactory.TryCreate(
            model, workspace.WorkspaceId, recipeId, out var criteria, out var error))
        {
            return OperationResult<CursorPageServiceModel<RecipeVersionHistoryServiceModel>>.Failure(error!);
        }

        var result = await business.GetVersionHistoryAsync(criteria!, cancellationToken);

        return result.Succeeded
            ? OperationResult<CursorPageServiceModel<RecipeVersionHistoryServiceModel>>.Success(
                await WithAuthorsAsync(result.Value!, cancellationToken))
            : OperationResult<CursorPageServiceModel<RecipeVersionHistoryServiceModel>>.Failure(result.Error!);
    }

    /// <summary>
    /// Exchanges each entry's membership for the display name of the person behind it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one enrichment this facade performs <em>after</em> Business rather than before it, and the reason
    /// is that the ids are not knowable until the page has been read — unlike a submitted cuisine or yield
    /// unit, which arrive in the request and are verified on the way down. Business cannot do it: naming a
    /// membership means reading Tenancy's rows and Auth's beyond them, and Business calls its own DataLayer
    /// and nothing else. Facade to facade is the only way across.
    /// </para>
    /// <para>
    /// <strong>One lookup per page, not per row.</strong> The distinct memberships of a page of history are
    /// usually one or two people, so this is a single read whatever the page size.
    /// </para>
    /// <para>
    /// <strong>An unresolved membership leaves the name null rather than failing the read.</strong> A version
    /// outlives the membership that wrote it by design — authorship is recorded precisely so it survives
    /// someone leaving — so a history that refused to load because an author had gone would be a history
    /// that punished the creator for a collaborator's departure.
    /// </para>
    /// </remarks>
    private async Task<CursorPageServiceModel<RecipeVersionHistoryServiceModel>> WithAuthorsAsync(
        RecipeVersionHistoryPageResult result,
        CancellationToken cancellationToken)
    {
        if (result.Page.Items.Count == 0)
        {
            return result.Page;
        }

        var names = await workspaces.FindMemberDisplayNamesAsync(
            [.. result.AuthorMembershipByVersionId.Values.Distinct()], cancellationToken);

        return result.Page with
        {
            Items =
            [
                .. result.Page.Items.Select(entry => entry with
                {
                    CreatedByName =
                        result.AuthorMembershipByVersionId.TryGetValue(entry.Id, out var membershipId)
                        && names.TryGetValue(membershipId, out var name)
                            ? name
                            : null,
                }),
            ],
        };
    }

    public async Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid recipeId,
        RecipeVersionComparisonViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await comparisonValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeVersionComparisonServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.ComparisonInvalidRequest,
                CannotCompare,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The recipe comes from the route and the workspace from the resolved context; the model has a field
        // for neither, and the version numbers it does carry are meaningful only within the recipe the route
        // named. Non-null by the validation above.
        return await business.CompareVersionsAsync(
            recipeId, model.From!.Value, model.To!.Value, cancellationToken);
    }

    public async Task<OperationResult<RecipeScalingResultServiceModel>> ScaleAsync(
        Guid recipeId,
        ScaleRecipeViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await scaleValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeScalingResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.ScalingInvalidRequest,
                CannotScale,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // Non-null by the validator's exactly-one-of rule above.
        var request = model.Multiplier is { } multiplier
            ? RecipeScalingRequest.ForMultiplier(Quantity.FromDecimal(multiplier))
            : RecipeScalingRequest.ForTargetYield(Quantity.FromDecimal(model.TargetYieldQuantity!.Value));

        return await business.ScaleAsync(recipeId, model.SourceVersionNumber, request, cancellationToken);
    }

    public async Task<OperationResult<RecipeUnitConversionResultServiceModel>> ConvertUnitsAsync(
        Guid recipeId,
        ConvertUnitsViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await convertUnitsValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeUnitConversionResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.UnitConversionInvalidRequest,
                CannotConvertUnits,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // One round trip for both units — FindUnitsByIdsAsync exists precisely for a calculation seam that
        // already knows which units it needs. Missing from the result means unknown or no longer offered;
        // the two are reported the same way, mirroring VerifyReferencesAsync's own unit check.
        var units = await measurement.FindUnitsByIdsAsync([model.FromUnitId, model.ToUnitId], cancellationToken);
        var byId = units.ToDictionary(unit => unit.Id);

        var errors = new List<(string Field, string Error)>();
        if (!byId.TryGetValue(model.FromUnitId, out var fromUnit))
        {
            errors.Add((nameof(ConvertUnitsViewModel.FromUnitId), "That unit is not available."));
        }

        if (!byId.TryGetValue(model.ToUnitId, out var toUnit))
        {
            errors.Add((nameof(ConvertUnitsViewModel.ToUnitId), "That unit is not available."));
        }

        if (errors.Count > 0)
        {
            return OperationResult<RecipeUnitConversionResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.UnitConversionInvalidRequest, CannotConvertUnits, errors));
        }

        var request = new RecipeUnitConversionRequest(Quantity.FromDecimal(model.Quantity), fromUnit!, toUnit!);

        return await business.ConvertUnitsAsync(recipeId, model.SourceVersionNumber, request, cancellationToken);
    }

    public async Task<OperationResult<RecipeTemperatureConversionResultServiceModel>> ConvertTemperatureAsync(
        Guid recipeId,
        ConvertTemperatureViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await convertTemperatureValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeTemperatureConversionResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.TemperatureConversionInvalidRequest,
                CannotConvertTemperature,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // No cross-module lookup: a temperature scale is an enum this module already knows, not a reference
        // id, so the shape-validated model is handed straight to Business.
        return await business.ConvertTemperatureAsync(recipeId, model.SourceVersionNumber, model, cancellationToken);
    }

    public async Task<OperationResult<RecipeYieldReconciliationResultServiceModel>> RecalculateYieldAsync(
        Guid recipeId,
        RecalculateYieldViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await recalculateYieldValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeYieldReconciliationResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.YieldRecalculationInvalidRequest,
                CannotRecalculateYield,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var request = new RecipeYieldReconciliationRequest(
            model.BatchYield, model.ServingCount, model.ServingSize, model.PanVolume, model.DisplayPrecision);

        return await business.RecalculateYieldAsync(recipeId, model.SourceVersionNumber, request, cancellationToken);
    }

    public async Task<OperationResult<RecipeQuantityDisplayResultServiceModel>> NormalizeDisplayAsync(
        Guid recipeId,
        NormalizeDisplayViewModel model,
        CancellationToken cancellationToken)
    {
        var validation = await normalizeDisplayValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeQuantityDisplayResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.DisplayNormalizationInvalidRequest,
                CannotNormalizeDisplay,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var units = await measurement.FindUnitsByIdsAsync([model.UnitId], cancellationToken);
        var unit = units.FirstOrDefault(candidate => candidate.Id == model.UnitId);

        if (unit is null)
        {
            return OperationResult<RecipeQuantityDisplayResultServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.DisplayNormalizationInvalidRequest,
                CannotNormalizeDisplay,
                [(nameof(NormalizeDisplayViewModel.UnitId), "That unit is not available.")]));
        }

        var request = new RecipeQuantityDisplayRequest(
            Quantity.FromDecimal(model.Value),
            model.UpperValue is { } upper ? Quantity.FromDecimal(upper) : null,
            unit,
            model.Precision,
            model.UseAbbreviation,
            model.Rounding);

        return await business.NormalizeDisplayAsync(recipeId, model.SourceVersionNumber, request, cancellationToken);
    }

    public async Task<IdempotentOutcome<RecipeDetailServiceModel>> UpdateAsync(
        string userId,
        Guid recipeId,
        UpdateRecipeViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Contributor, the same bar as creating one. Editing a recipe someone else started is a
        // collaboration, not an escalation: who made each change is recorded on the version it wrote, and
        // the workspace's own role list is what decides who may contribute at all.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused<RecipeDetailServiceModel>(
                RecipeErrorCodes.RecipeForbidden, "You do not have permission to change recipes in this workspace.");
        }

        var validation = await updateValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused<RecipeDetailServiceModel>(OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                CannotChange,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // Only what the request submitted is checked. Re-verifying a reference the edit never mentioned
        // would refuse an edit because of a cuisine that was retired after the recipe was written — an
        // answer the creator can do nothing about and did not ask for.
        var (references, yieldUnitDimension) = await VerifyReferencesAsync(
            Submitted(model.CuisineId),
            Submitted(model.CourseId),
            Submitted(model.PrimaryTechniqueId),
            Submitted(model.YieldUnitId),
            CannotChange,
            cancellationToken);
        if (references is not null)
        {
            return Refused<RecipeDetailServiceModel>(references);
        }

        var canonical = CanonicalRecipePatch.From(model);

        if (canonical.Instructions.TryGetSubmitted(out var instructions)
            && await VerifyInstructionReferencesAsync(instructions, CannotChange, cancellationToken) is { } instructionError)
        {
            return Refused<RecipeDetailServiceModel>(instructionError);
        }

        var ingredientUnitDimensions = EmptyIngredientUnitDimensions;
        if (canonical.IngredientGroups.TryGetSubmitted(out var ingredientGroups))
        {
            OperationError? ingredientError;
            (ingredientError, ingredientUnitDimensions) = await VerifyIngredientReferencesAsync(
                ingredientGroups, CannotChange, cancellationToken);
            if (ingredientError is not null)
            {
                return Refused<RecipeDetailServiceModel>(ingredientError);
            }
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                UpdateOperation,
                idempotencyKey,
                // The recipe and the expected token are part of the fingerprint, not only the fields: the
                // same edit against a different state of the recipe is a different request, and a caller who
                // re-read before retrying should be told the key was reused rather than handed the earlier
                // answer.
                Fingerprint: canonical.Fingerprint(recipeId),
                KeyRequired: false),
            token => business.UpdateAsync(recipeId, canonical, yieldUnitDimension, ingredientUnitDimensions, token),
            cancellationToken);
    }

    public async Task<OperationResult<RecipeDetailServiceModel>> ApplyProposedChangesAsync(
        Guid recipeId,
        Guid expectedVersionId,
        Guid aiProposalId,
        IReadOnlyList<ProposedRecipeChange> changes,
        CancellationToken cancellationToken)
    {
        // Contributor, the same bar as editing by hand. Accepting a proposal is an edit — the model proposed it,
        // a person made it happen — so it must cost exactly the role that writing it out by hand would.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.RecipeForbidden,
                "You do not have permission to change recipes in this workspace.",
                new Dictionary<string, string[]>()));
        }

        // No ViewModel validator, because there is no ViewModel: the input is not a client request. The shape
        // checks a validator would make — parsable numbers, text that is not blank — happen where the accepted
        // values are translated, and every invariant a recipe has is checked by Business against the merged
        // recipe, which is where they belong for a patch.
        return await business.ApplyProposedChangesAsync(
            recipeId, expectedVersionId, aiProposalId, changes, cancellationToken);
    }

    public async Task<IdempotentOutcome<RecipeDetailServiceModel>> RestoreVersionAsync(
        string userId,
        Guid recipeId,
        int versionNumber,
        RestoreRecipeVersionViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Editor, a step above editing — see the interface remarks for why restoring is a different kind of
        // act from contributing. Checked here and not only at the controller policy because this boundary is
        // also reached by workers and AI plugins, which no MVC policy protects.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return Refused<RecipeDetailServiceModel>(
                RecipeErrorCodes.RecipeForbidden, "You do not have permission to restore versions in this workspace.");
        }

        var validation = await restoreValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused<RecipeDetailServiceModel>(OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                CannotRestore,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // No reference verification, unlike the create and update paths: there is nothing submitted here to
        // verify. Every id this operation writes comes from the recipe's own archive.
        var canonical = CanonicalRestoreRecipeVersion.From(model);

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                RestoreOperation,
                idempotencyKey,
                // Both route values are in the fingerprint, so one key cannot replay across recipes or across
                // versions — see CanonicalRestoreRecipeVersion.Fingerprint.
                Fingerprint: canonical.Fingerprint(recipeId, versionNumber),
                // Accepted, not required, for the reason CreateAsync gives. A restore is well protected
                // without a key — a repeat is either refused as stale or answered as a no-op, and neither
                // writes a second version — so demanding one would refuse honest clients to prevent nothing.
                KeyRequired: false),
            token => business.RestoreVersionAsync(recipeId, versionNumber, canonical, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<CreatedRecipeServiceModel>> DuplicateAsync(
        string userId,
        Guid recipeId,
        DuplicateRecipeViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Contributor, the same bar as creating one from nothing — see the interface remarks for why this is
        // not the Editor bar a restore carries. Checked here and not only at the controller policy because
        // this boundary is also reached by workers and AI plugins, which no MVC policy protects.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused<CreatedRecipeServiceModel>(
                RecipeErrorCodes.RecipeForbidden, "You do not have permission to add recipes to this workspace.");
        }

        var validation = await duplicateValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused<CreatedRecipeServiceModel>(OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                CannotDuplicate,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // No reference verification: nothing submitted here names a cuisine, course, technique or unit. Every
        // such id the copy writes comes from the source recipe's own archive.
        var canonical = CanonicalDuplicateRecipe.From(model);

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                DuplicateOperation,
                idempotencyKey,
                // The source recipe, the version and the title — see CanonicalDuplicateRecipe.Fingerprint.
                Fingerprint: canonical.Fingerprint(recipeId),
                // Accepted, not required, as on the create it resembles: api-contract.md says retryable
                // commands accept a key, and demanding one would refuse every client that does not send one.
                // Worth sending here more than on most routes — without it, a retry after a lost response
                // leaves the creator with two copies to tell apart.
                KeyRequired: false),
            token => business.DuplicateAsync(recipeId, canonical, token),
            cancellationToken);
    }

    public Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(
        string userId,
        Guid recipeId,
        RecipeLifecycleViewModel model,
        CancellationToken cancellationToken) =>
        LifecycleAsync(
            model,
            token => business.ArchiveAsync(recipeId, userId, model.ExpectedConcurrencyToken, token),
            CannotArchive,
            cancellationToken);

    public Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(
        string userId,
        Guid recipeId,
        RecipeLifecycleViewModel model,
        CancellationToken cancellationToken) =>
        LifecycleAsync(
            model,
            token => business.UnarchiveAsync(recipeId, userId, model.ExpectedConcurrencyToken, token),
            CannotUnarchive,
            cancellationToken);

    /// <summary>
    /// The role gate and shape validation both lifecycle commands share, in front of whichever one was asked
    /// for.
    /// </summary>
    /// <remarks>
    /// Shared because the two differ only in which Business method they call and which sentence a refusal
    /// carries — and because the role bar in particular is the kind of thing that must not be able to differ
    /// between them by accident. No idempotency executor: neither command needs one.
    /// </remarks>
    private async Task<OperationResult<RecipeDetailServiceModel>> LifecycleAsync(
        RecipeLifecycleViewModel model,
        Func<CancellationToken, Task<OperationResult<RecipeDetailServiceModel>>> command,
        string message,
        CancellationToken cancellationToken)
    {
        // Editor, as REC-006 requires — see the interface remarks. Checked here and not only at the
        // controller policy because this boundary is also reached by workers and AI plugins, which no MVC
        // policy protects.
        if (workspace.Role < WorkspaceRole.Editor)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
                RecipeErrorCodes.RecipeForbidden,
                "You do not have permission to archive recipes in this workspace.",
                new Dictionary<string, string[]>()));
        }

        var validation = await lifecycleValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                message,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // No reference verification and no canonical form: the body carries one opaque token and no content.
        return await command(cancellationToken);
    }

    /// <summary>The id the request asked for, or <c>null</c> when it did not mention this reference at all.</summary>
    /// <remarks>
    /// A submitted <c>null</c> — a clear — answers <c>null</c> too, and rightly: there is no id to verify
    /// when the caller is removing one.
    /// </remarks>
    private static Guid? Submitted(PatchField<Guid?> field) => field.Or(null);

    /// <summary>
    /// Confirms every reference named here is real and still offered, and resolves the yield unit's
    /// dimension, which Business must store and cannot look up itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dimension is resolved here rather than below, and that is a boundary decision rather than a
    /// convenience. Measurement units belong to another module, whose only permitted entry point is its
    /// facade — and Business may call nothing but its own DataLayer.
    /// </para>
    /// <para>
    /// Field names are taken from <see cref="CreateRecipeViewModel"/> and serve both contracts, because
    /// <see cref="UpdateRecipeViewModel"/> names these four fields identically. If the two ever diverge,
    /// this method needs the names passed in rather than assumed.
    /// </para>
    /// </remarks>
    private async Task<(OperationError? Error, MeasurementDimension? YieldUnitDimension)> VerifyReferencesAsync(
        Guid? cuisineId,
        Guid? courseId,
        Guid? primaryTechniqueId,
        Guid? yieldUnitId,
        string message,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Field, string Error)>();

        await CheckAsync(VocabularyCatalog.Cuisine, cuisineId, nameof(CreateRecipeViewModel.CuisineId), "cuisine");
        await CheckAsync(VocabularyCatalog.Course, courseId, nameof(CreateRecipeViewModel.CourseId), "course");
        await CheckAsync(
            VocabularyCatalog.CookingTechnique, primaryTechniqueId, nameof(CreateRecipeViewModel.PrimaryTechniqueId), "method");

        MeasurementDimension? dimension = null;
        if (yieldUnitId is { } unitId)
        {
            dimension = await measurement.FindUsableUnitDimensionAsync(unitId, cancellationToken);

            if (dimension is null)
            {
                errors.Add((nameof(CreateRecipeViewModel.YieldUnitId), "That unit is not available."));
            }
        }

        return errors.Count == 0
            ? (null, dimension)
            : (OperationError.Validation(RecipeErrorCodes.RecipeInvalidRequest, message, errors), null);

        async Task CheckAsync(VocabularyCatalog catalog, Guid? id, string field, string noun)
        {
            if (id is { } value && !await vocabulary.IsUsableAsync(catalog, value, cancellationToken))
            {
                // Unknown and retired are reported the same way. The caller's remedy is identical either way,
                // and distinguishing them would describe the catalogue's history to no purpose.
                errors.Add((field, $"That {noun} is not available."));
            }
        }
    }

    /// <summary>
    /// Confirms every technique and temperature unit named by a submitted method is real, still offered, and
    /// — for the unit — actually measures temperature.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="VerifyReferencesAsync"/> rather than folded into it: this reads
    /// the canonical, already-shape-validated instructions rather than the raw view model, because canonical
    /// form is what the caller already has in hand at the point instructions need checking, and step
    /// references are per-step rather than one-per-request.
    /// </remarks>
    private async Task<OperationError?> VerifyInstructionReferencesAsync(
        IReadOnlyList<CanonicalInstructionGroup> groups, string message, CancellationToken cancellationToken)
    {
        var errors = new List<(string Field, string Error)>();
        var stepNumber = 0;

        foreach (var step in groups.SelectMany(group => group.Steps))
        {
            stepNumber++;

            if (step.TechniqueId is { } techniqueId
                && !await vocabulary.IsUsableAsync(VocabularyCatalog.CookingTechnique, techniqueId, cancellationToken))
            {
                errors.Add((nameof(RecipeInstructionStepInputViewModel.TechniqueId), $"Step {stepNumber}'s technique is not available."));
            }

            if (step.TemperatureUnitId is { } unitId)
            {
                var dimension = await measurement.FindUsableUnitDimensionAsync(unitId, cancellationToken);

                if (dimension != MeasurementDimension.Temperature)
                {
                    errors.Add((nameof(RecipeInstructionStepInputViewModel.TemperatureUnitId), $"Step {stepNumber}'s temperature unit is not available."));
                }
            }
        }

        return errors.Count == 0
            ? null
            : OperationError.Validation(RecipeErrorCodes.RecipeInvalidRequest, message, errors);
    }

    /// <summary>Shared by every caller with nothing submitted to verify, so none allocates its own empty map.</summary>
    private static readonly IReadOnlyDictionary<Guid, MeasurementDimension> EmptyIngredientUnitDimensions =
        new Dictionary<Guid, MeasurementDimension>();

    /// <summary>
    /// Confirms every unit and every ingredient named by a submitted ingredient list is real, still offered,
    /// and — for the unit — actually measures something an ingredient can be measured in.
    /// </summary>
    /// <returns>
    /// An error, or none — and, when none, the resolved dimension of every distinct unit the list named. That
    /// map is what lets Business store <c>RecipeIngredient.MeasurementUnitDimension</c> without calling
    /// another module's facade itself (backend.md): a unit's dimension can be anything but
    /// <see cref="MeasurementDimension.Temperature"/>, unlike a step's temperature unit, so Business cannot
    /// hardcode the one acceptable value the way <c>ApplyStep</c> does.
    /// </returns>
    /// <remarks>
    /// Deliberately separate from <see cref="VerifyInstructionReferencesAsync"/> for the same reason that one
    /// is separate from <see cref="VerifyReferencesAsync"/>: this reads the canonical, already-shape-validated
    /// ingredient list rather than the raw view model, and its references are per-line rather than
    /// one-per-request.
    /// </remarks>
    private async Task<(OperationError? Error, IReadOnlyDictionary<Guid, MeasurementDimension> UnitDimensions)> VerifyIngredientReferencesAsync(
        IReadOnlyList<CanonicalIngredientGroup> groups, string message, CancellationToken cancellationToken)
    {
        var lines = groups.SelectMany(group => group.Ingredients).ToList();
        if (lines.Count == 0)
        {
            return (null, EmptyIngredientUnitDimensions);
        }

        var errors = new List<(string Field, string Error)>();
        var unitDimensions = new Dictionary<Guid, MeasurementDimension>();
        var lineNumber = 0;

        foreach (var line in lines)
        {
            lineNumber++;

            if (line.MeasurementUnitId is { } unitId && !unitDimensions.ContainsKey(unitId))
            {
                var dimension = await measurement.FindUsableUnitDimensionAsync(unitId, cancellationToken);

                if (dimension is null)
                {
                    errors.Add((nameof(RecipeIngredientInputViewModel.MeasurementUnitId), $"Line {lineNumber}'s unit is not available."));
                }
                else if (dimension == MeasurementDimension.Temperature)
                {
                    errors.Add((nameof(RecipeIngredientInputViewModel.MeasurementUnitId), $"Line {lineNumber} cannot be measured in degrees."));
                }
                else
                {
                    unitDimensions[unitId] = dimension.Value;
                }
            }

            if (line.IngredientId is { } ingredientId && !await ingredients.IsUsableAsync(ingredientId, cancellationToken))
            {
                errors.Add((nameof(RecipeIngredientInputViewModel.IngredientId), $"Line {lineNumber}'s ingredient is not available."));
            }
        }

        return errors.Count == 0
            ? (null, unitDimensions)
            : (OperationError.Validation(RecipeErrorCodes.RecipeInvalidRequest, message, errors), EmptyIngredientUnitDimensions);
    }

    /// <summary>
    /// A refusal that never reached the idempotency executor, and so was never recorded.
    /// </summary>
    /// <remarks>
    /// Deliberate: only committed outcomes are replayable. A rejected request leaves the key unused, so a
    /// caller that fixes the payload and retries with the same key is making a fresh attempt, not replaying.
    /// </remarks>
    private static IdempotentOutcome<T> Refused<T>(string code, string message) =>
        Refused<T>(new OperationError(code, message, new Dictionary<string, string[]>()));

    private static IdempotentOutcome<T> Refused<T>(OperationError error) =>
        new(OperationResult<T>.Failure(error), Replayed: false);
}
