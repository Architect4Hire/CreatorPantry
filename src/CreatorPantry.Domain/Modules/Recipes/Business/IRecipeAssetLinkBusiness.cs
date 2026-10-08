using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Business;

/// <summary>
/// What the facade learned about the asset a link names, in this module's own terms.
/// </summary>
/// <remarks>
/// Resolved by the facade through the Media module's lookup facade and handed down as a value, the way a
/// resolved unit dimension is: Business decides what a link means, and never reaches into another module to
/// find out whether its target exists (backend.md).
/// </remarks>
public enum RecipeAssetLinkTargetState
{
    /// <summary>The asset is in this workspace's library, and the pinned version, if any, is one of its own.</summary>
    Linkable = 0,

    /// <summary>No live asset with that id in this workspace: unknown, a neighbour's, or removed.</summary>
    AssetNotFound = 1,

    /// <summary>The asset is there and has no version with that number.</summary>
    VersionNotFound = 2,
}

/// <summary>
/// Domain rules for putting a library asset to use on a recipe, and taking it off again (RCPUB-005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A link is part of the recipe, so changing one is an edit.</strong> Every version's snapshot records
/// the recipe's links, and restore and duplicate rebuild them from it. Both commands therefore go through the
/// edit's own save — <see cref="IRecipeDataLayer.UpdateAsync"/> — and write one new version: the recipe's
/// concurrency token is required and refreshed, a change to an approved recipe reopens it, and history and
/// the live recipe cannot come to disagree about which pictures it has.
/// </para>
/// <para>
/// <strong>Nothing here touches the asset.</strong> Linking writes a row naming it; unlinking removes that
/// row. The asset, its versions and its usage history are another module's, reached by none of this.
/// </para>
/// </remarks>
public interface IRecipeAssetLinkBusiness
{
    /// <summary>Links one asset to the recipe in one role, at the end of its links.</summary>
    /// <param name="target">What the asset the request names resolved to, in the caller's workspace.</param>
    /// <returns>
    /// The recipe as it now stands, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>, <see cref="RecipeErrorCodes.RecipeArchivedConflict"/>,
    /// <see cref="RecipeErrorCodes.AssetLinkTargetUnprocessable"/>,
    /// <see cref="RecipeErrorCodes.AssetLinkHeroConflict"/> or
    /// <see cref="RecipeErrorCodes.AssetLinkDuplicateConflict"/>.
    /// </returns>
    Task<OperationResult<RecipeDetailServiceModel>> LinkAsync(
        Guid recipeId,
        CanonicalRecipeAssetLink request,
        RecipeAssetLinkTargetState target,
        CancellationToken cancellationToken);

    /// <summary>Removes one link from the recipe. The asset it named is not touched.</summary>
    /// <returns>
    /// The recipe as it now stands, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>, <see cref="RecipeErrorCodes.RecipeArchivedConflict"/> or
    /// <see cref="RecipeErrorCodes.AssetLinkNotFound"/>.
    /// </returns>
    Task<OperationResult<RecipeDetailServiceModel>> UnlinkAsync(
        Guid recipeId,
        Guid linkId,
        string expectedConcurrencyToken,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecipeAssetLinkBusiness"/>
internal sealed class RecipeAssetLinkBusiness(
    IRecipeDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IRecipeAssetLinkBusiness
{
    private const string CannotLink = "That picture cannot be linked as described.";

    public async Task<OperationResult<RecipeDetailServiceModel>> LinkAsync(
        Guid recipeId,
        CanonicalRecipeAssetLink request,
        RecipeAssetLinkTargetState target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var loaded = await dataLayer.GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return NotFound();
        }

        var recipe = loaded.Recipe.Recipe;

        if (Refusal(recipe, request.ExpectedConcurrencyToken) is { } refusal)
        {
            return refusal;
        }

        // The asset before anything about the recipe's own links: a link to something that is not there is
        // wrong whatever else is true of it. One sentence for unknown, a neighbour's and removed, so a link
        // request cannot be used to ask what another workspace owns (tenancy.md).
        if (target == RecipeAssetLinkTargetState.AssetNotFound)
        {
            return Unprocessable(
                nameof(LinkRecipeAssetViewModel.MediaAssetId), "That picture is not in this workspace's library.");
        }

        if (target == RecipeAssetLinkTargetState.VersionNotFound)
        {
            return Unprocessable(
                nameof(LinkRecipeAssetViewModel.VersionNumber), "That picture has no version with that number.");
        }

        // The key behind InstructionStepId pins the workspace, not the recipe, so "a step of this recipe" is
        // decided here against the aggregate that was loaded. A step of another recipe — or of another
        // workspace — is simply not in it, and gets the same answer as an id that names nothing.
        if (request.Role == RecipeAssetRole.Step
            && !recipe.InstructionGroups.SelectMany(group => group.Steps).Any(step => step.Id == request.InstructionStepId))
        {
            return Unprocessable(
                nameof(LinkRecipeAssetViewModel.InstructionStepId), "That step is not part of this recipe.");
        }

        if (request.Role == RecipeAssetRole.Hero && recipe.AssetLinks.Any(link => link.Role == RecipeAssetRole.Hero))
        {
            // Refused, not replaced: replacing would unlink the lead image the creator chose as a side effect
            // of linking another. The filtered unique index says the same thing, as an exception nobody reads.
            return Failure(
                RecipeErrorCodes.AssetLinkHeroConflict,
                "This recipe already has a lead picture. Unlink it before choosing another.");
        }

        if (recipe.AssetLinks.Any(link =>
            link.MediaAssetId == request.MediaAssetId
            && link.Role == request.Role
            && link.InstructionStepId == request.InstructionStepId))
        {
            // The same picture doing the same job twice is a double submission, not a second choice. The same
            // picture in two roles is fine and is not this.
            return Failure(
                RecipeErrorCodes.AssetLinkDuplicateConflict,
                "That picture is already linked to this recipe in that role.");
        }

        recipe.AssetLinks.Add(new RecipeAssetLink
        {
            Id = Guid.NewGuid(),

            // Set from the aggregate rather than left to the save: the link's composite keys read this column,
            // and the recipe's own workspace is the only one a link of it can have.
            WorkspaceId = recipe.WorkspaceId,
            RecipeId = recipe.Id,
            MediaAssetId = request.MediaAssetId,
            MediaAssetVersionNumber = request.VersionNumber,
            InstructionStepId = request.Role == RecipeAssetRole.Step ? request.InstructionStepId : null,
            Role = request.Role,
            Caption = request.Caption,

            // At the end. Positions are unique within the recipe and may have gaps after an unlink, so this is
            // one past the largest in use rather than a count.
            SortOrder = recipe.AssetLinks.Count == 0 ? 0 : recipe.AssetLinks.Max(link => link.SortOrder) + 1,
        });

        return await CommitAsync(loaded, cancellationToken);
    }

    public async Task<OperationResult<RecipeDetailServiceModel>> UnlinkAsync(
        Guid recipeId,
        Guid linkId,
        string expectedConcurrencyToken,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return NotFound();
        }

        var recipe = loaded.Recipe.Recipe;

        if (Refusal(recipe, expectedConcurrencyToken) is { } refusal)
        {
            return refusal;
        }

        // Looked for in this recipe's own links only. A link of another recipe, or of another workspace, is
        // not among them and answers exactly as an id that names nothing does.
        var link = recipe.AssetLinks.FirstOrDefault(candidate => candidate.Id == linkId);
        if (link is null)
        {
            return Failure(RecipeErrorCodes.AssetLinkNotFound, "That picture is not linked to this recipe.");
        }

        // The row and nothing else. The asset it named, that asset's versions and its usage history belong to
        // the library and are not reachable from here; the positions of the links that remain are left as they
        // are, because a gap is harmless and renumbering would be a second change nobody asked for.
        recipe.AssetLinks.Remove(link);

        return await CommitAsync(loaded, cancellationToken);
    }

    /// <summary>The two refusals every change to a recipe's links shares with every other edit of it.</summary>
    private static OperationResult<RecipeDetailServiceModel>? Refusal(Recipe recipe, string expectedConcurrencyToken)
    {
        // Checked before anything is changed, so a refused command is answered from the state it was composed
        // against. The save repeats the check; this one is what can produce a readable answer.
        if (!RecipeConcurrencyToken.Matches(expectedConcurrencyToken, recipe.RowVersion))
        {
            return Failure(
                RecipeErrorCodes.RecipeConflict,
                "This recipe has changed since you opened it. Reload it and make your edit again.");
        }

        // After the token, for the reason the edit path gives: a stale caller may not have seen that the
        // recipe was archived, and "re-read this" is the answer that leads them to it.
        return RecipePolicy.AcceptsContentChanges(recipe.Status)
            ? null
            : Failure(
                RecipeErrorCodes.RecipeArchivedConflict,
                "This recipe is archived. Bring it back from the archive before changing it.");
    }

    /// <summary>Stamps the edit and saves it with the one version that records it.</summary>
    private async Task<OperationResult<RecipeDetailServiceModel>> CommitAsync(
        TaggedRecipe loaded, CancellationToken cancellationToken)
    {
        var recipe = loaded.Recipe.Recipe;

        // One read of the clock, so the recipe and the version recording the change agree on when it
        // happened. The actor is the resolved membership, never a request field.
        recipe.UpdatedAt = clock.UtcNow;
        recipe.UpdatedByMembershipId = workspace.MembershipId;

        // Changing which pictures an approved recipe carries changes what was approved, so it reopens — the
        // same rule, staged the same way, as any other edit to one (RecipeStatusTransitions.EditReopens).
        var reopen = RecipeStatusTransitions.EditReopens(recipe.Status)
            ? RecipeBusiness.StageReopen(recipe)
            : null;

        var outcome = await dataLayer.UpdateAsync(
            loaded,
            new RecipeVersionFacts(RecipeVersionSource.CreatorEdit, RecipeVersionReadiness.Draft, Reason: null),
            tags: null,
            reopen,
            cancellationToken);

        return outcome.Version is null
            ? Failure(
                RecipeErrorCodes.RecipeConflict,
                "This recipe has changed since you opened it. Reload it and make your edit again.")
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(
                loaded with
                {
                    Recipe = new CompleteRecipe(recipe, outcome.Version),
                    Tags = outcome.Tags,
                }));
    }

    private static OperationResult<RecipeDetailServiceModel> NotFound() =>
        Failure(RecipeErrorCodes.RecipeNotFound, "That recipe could not be found.");

    private static OperationResult<RecipeDetailServiceModel> Failure(string code, string message) =>
        OperationResult<RecipeDetailServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));

    private static OperationResult<RecipeDetailServiceModel> Unprocessable(string field, string message) =>
        OperationResult<RecipeDetailServiceModel>.Failure(OperationError.Validation(
            RecipeErrorCodes.AssetLinkTargetUnprocessable, CannotLink, [(field, message)]));
}
