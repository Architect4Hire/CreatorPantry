using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;
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
}

internal sealed class RecipeFacade(
    IValidator<CreateRecipeViewModel> createValidator,
    IValidator<UpdateRecipeViewModel> updateValidator,
    IRecipeBusiness business,
    IWorkspaceContext workspace,
    IVocabularyFacade vocabulary,
    IMeasurementFacade measurement,
    IIdempotentCommandExecutor idempotency) : IRecipeFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string CreateOperation = "recipes.create";

    private const string UpdateOperation = "recipes.update";

    private const string CannotCreate = "That recipe cannot be created as described.";

    private const string CannotChange = "That recipe cannot be changed as described.";

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
            token => business.CreateAsync(canonical, yieldUnitDimension, token),
            cancellationToken);
    }

    public Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(
        Guid recipeId,
        CancellationToken cancellationToken) =>
        business.GetDetailAsync(recipeId, cancellationToken);

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
            token => business.UpdateAsync(recipeId, canonical, yieldUnitDimension, token),
            cancellationToken);
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
