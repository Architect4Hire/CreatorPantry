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
/// The application boundary for the pictures attached to a recorded test (RCPUB-005).
/// </summary>
/// <remarks>
/// The asset is resolved here, through the Media module's lookup facade, inside the caller's workspace and
/// before anything is written — exactly as <see cref="IRecipeAssetLinkFacade"/> does, and for the same reason.
/// </remarks>
public interface IRecipeTestAttachmentFacade
{
    /// <summary>The attachments of one test of the workspace resolved for this scope. Any member may read.</summary>
    Task<OperationResult<IReadOnlyList<TestAttachmentServiceModel>>> ListAsync(
        Guid recipeId, Guid testRunId, CancellationToken cancellationToken);

    /// <summary>Attaches one asset to one test.</summary>
    /// <param name="idempotencyKey">
    /// The caller's <c>Idempotency-Key</c>, when one was sent. A replay of the same key and the same request
    /// returns the original attachment rather than a duplicate refusal.
    /// </param>
    Task<IdempotentOutcome<TestAttachmentServiceModel>> AttachAsync(
        string userId,
        Guid recipeId,
        Guid testRunId,
        AttachTestImageViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Removes one attachment from one test.</summary>
    /// <inheritdoc cref="AttachAsync" path="/param[@name='idempotencyKey']"/>
    Task<IdempotentOutcome<TestAttachmentServiceModel>> DetachAsync(
        string userId,
        Guid recipeId,
        Guid testRunId,
        Guid attachmentId,
        string? idempotencyKey,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IRecipeTestAttachmentFacade"/>
internal sealed class RecipeTestAttachmentFacade(
    IValidator<AttachTestImageViewModel> attachValidator,
    ITestAttachmentBusiness business,
    IMediaAssetLookupFacade mediaAssets,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IRecipeTestAttachmentFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string AttachOperation = "recipes.testAttachments.attach";

    private const string DetachOperation = "recipes.testAttachments.detach";

    private const string Forbidden = "You do not have permission to change tests in this workspace.";

    public Task<OperationResult<IReadOnlyList<TestAttachmentServiceModel>>> ListAsync(
        Guid recipeId, Guid testRunId, CancellationToken cancellationToken) =>

        // No role gate beyond a resolved workspace: every member may read a test, and its pictures are part
        // of reading it.
        business.ListAsync(recipeId, testRunId, cancellationToken);

    public async Task<IdempotentOutcome<TestAttachmentServiceModel>> AttachAsync(
        string userId,
        Guid recipeId,
        Guid testRunId,
        AttachTestImageViewModel model,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        // Contributor, the bar recording a test carries. Checked here and not only at the controller policy
        // because this boundary is also reached by workers and AI plugins, which no MVC policy protects.
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused(RecipeErrorCodes.RecipeForbidden, Forbidden);
        }

        var validation = await attachValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                RecipeErrorCodes.TestAttachmentInvalidRequest,
                "That picture cannot be attached as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        var canonical = CanonicalTestAttachment.From(model);

        var target = await mediaAssets.ResolveLinkTargetAsync(
            canonical.MediaAssetId, canonical.VersionNumber, cancellationToken) switch
        {
            MediaAssetLinkTarget.Linkable => RecipeAssetLinkTargetState.Linkable,
            MediaAssetLinkTarget.VersionNotFound => RecipeAssetLinkTargetState.VersionNotFound,

            // Anything this build does not recognise is treated as not there.
            _ => RecipeAssetLinkTargetState.AssetNotFound,
        };

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                AttachOperation,
                idempotencyKey,
                Fingerprint: canonical.Fingerprint(recipeId, testRunId),

                // Accepted, not required. A repeat without one is refused as a duplicate rather than
                // attaching the picture twice, so a key buys the original answer, not safety.
                KeyRequired: false),
            token => business.AttachAsync(recipeId, testRunId, canonical, target, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<TestAttachmentServiceModel>> DetachAsync(
        string userId,
        Guid recipeId,
        Guid testRunId,
        Guid attachmentId,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (workspace.Role < WorkspaceRole.Contributor)
        {
            return Refused(RecipeErrorCodes.RecipeForbidden, Forbidden);
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId,
                workspace.WorkspaceId,
                DetachOperation,
                idempotencyKey,
                Fingerprint: new { RecipeId = recipeId, TestRunId = testRunId, AttachmentId = attachmentId },
                KeyRequired: false),
            token => business.DetachAsync(recipeId, testRunId, attachmentId, token),
            cancellationToken);
    }

    private static IdempotentOutcome<TestAttachmentServiceModel> Refused(string code, string message) =>
        Refused(new OperationError(code, message, new Dictionary<string, string[]>()));

    private static IdempotentOutcome<TestAttachmentServiceModel> Refused(OperationError error) =>
        new(OperationResult<TestAttachmentServiceModel>.Failure(error), Replayed: false);
}
