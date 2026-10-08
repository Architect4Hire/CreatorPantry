using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Media.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Recipes.Facade;

/// <summary>
/// The application boundary for linking a library asset to a recipe and unlinking it (RCPUB-005).
/// </summary>
/// <remarks>
/// <para>
/// Its own facade rather than two more methods on <see cref="IRecipeFacade"/>, which already carries every
/// other thing a recipe can be asked to do. It is the same recipe and the same save: both commands end in the
/// edit's own write and return the recipe exactly as an edit does.
/// </para>
/// <para>
/// <strong>The asset is resolved here, through the Media module's lookup facade</strong>, inside the caller's
/// workspace and before anything is written — facade to facade, which is the only way one module asks another
/// anything (backend.md). The foreign key behind a link remains the authority; this is what turns a storage
/// exception into a field a creator can act on.
/// </para>
/// </remarks>
public interface IRecipeAssetLinkFacade
{
    /// <summary>Links one asset to one recipe of the workspace resolved for this scope.</summary>
    /// <param name="idempotencyKey">
    /// The caller's <c>Idempotency-Key</c>, when one was sent. A replay of the same key and the same link
    /// returns the original outcome rather than a conflict with itself.
    /// </param>
    Task<IdempotentOutcome<RecipeDetailServiceModel>> LinkAsync(
        string userId,
        Guid recipeId,
        LinkRecipeAssetViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Removes one link from one recipe of the workspace resolved for this scope.</summary>
    /// <inheritdoc cref="LinkAsync" path="/param[@name='idempotencyKey']"/>
    Task<IdempotentOutcome<RecipeDetailServiceModel>> UnlinkAsync(
        string userId,
        Guid recipeId,
        Guid linkId,
        UnlinkRecipeAssetViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecipeAssetLinkFacade"/>
internal sealed class RecipeAssetLinkFacade(
    IValidator<LinkRecipeAssetViewModel> linkValidator,
    IValidator<UnlinkRecipeAssetViewModel> unlinkValidator,
    IRecipeAssetLinkBusiness business,
    IMediaAssetLookupFacade mediaAssets,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IRecipeAssetLinkFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string LinkOperation = "recipes.assetLinks.link";

    private const string UnlinkOperation = "recipes.assetLinks.unlink";

    private const string CannotLink = "That picture cannot be linked as described.";

    private const string CannotUnlink = "That picture cannot be unlinked as described.";

    public async Task<IdempotentOutcome<RecipeDetailServiceModel>> LinkAsync(
        string userId,
        Guid recipeId,
        LinkRecipeAssetViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Contributor, the same bar as editing the recipe, because this is an edit of it. Checked here and not
        // only at the controller policy because this boundary is also reached by workers and AI plugins, which
        // no MVC policy protects.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused(
                RecipeErrorCodes.RecipeForbidden, "You do not have permission to change recipes in this workspace.");
        }

        var validation = await linkValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                RecipeErrorCodes.AssetLinkInvalidRequest,
                CannotLink,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var canonical = CanonicalRecipeAssetLink.From(model);

        // Resolved before the command runs and handed down as a value. Outside the idempotent executor on
        // purpose: it is a read, and a replay returns the recorded answer without needing it again.
        var target = await mediaAssets.ResolveLinkTargetAsync(
            canonical.MediaAssetId, canonical.VersionNumber, cancellationToken) switch
        {
            MediaAssetLinkTarget.Linkable => RecipeAssetLinkTargetState.Linkable,
            MediaAssetLinkTarget.VersionNotFound => RecipeAssetLinkTargetState.VersionNotFound,

            // Anything this build does not recognise is treated as not there. Guessing the other way would
            // write a link on the strength of an answer nobody understood.
            _ => RecipeAssetLinkTargetState.AssetNotFound,
        };

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                LinkOperation,
                idempotencyKey,
                Fingerprint: canonical.Fingerprint(recipeId),

                // Accepted, not required, as on the edit route. A repeat without one is refused as stale —
                // the first call moved the recipe's token — so nothing is linked twice either way.
                KeyRequired: false),
            token => business.LinkAsync(recipeId, canonical, target, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<RecipeDetailServiceModel>> UnlinkAsync(
        string userId,
        Guid recipeId,
        Guid linkId,
        UnlinkRecipeAssetViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused(
                RecipeErrorCodes.RecipeForbidden, "You do not have permission to change recipes in this workspace.");
        }

        var validation = await unlinkValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                RecipeErrorCodes.AssetLinkInvalidRequest,
                CannotUnlink,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // Non-null by validation.
        var token = model.ExpectedConcurrencyToken!;

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                UnlinkOperation,
                idempotencyKey,

                // Both route values and the token, so one key cannot replay across recipes, across links, or
                // against a recipe the caller has since re-read.
                Fingerprint: new { RecipeId = recipeId, LinkId = linkId, ExpectedConcurrencyToken = token },
                KeyRequired: false),
            cancellation => business.UnlinkAsync(recipeId, linkId, token, cancellation),
            cancellationToken);
    }

    private static IdempotentOutcome<RecipeDetailServiceModel> Refused(string code, string message) =>
        Refused(new OperationError(code, message, new Dictionary<string, string[]>()));

    private static IdempotentOutcome<RecipeDetailServiceModel> Refused(OperationError error) =>
        new(OperationResult<RecipeDetailServiceModel>.Failure(error), Replayed: false);
}
