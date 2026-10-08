using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Business;

/// <summary>
/// Domain rules for attaching a library asset to a recorded test, and detaching it (RCPUB-005).
/// </summary>
/// <remarks>
/// <para>
/// <strong>An attachment is not part of what the test recorded, and not part of the recipe.</strong> No recipe
/// version is written — a version's snapshot does not hold a test's pictures — and the test's own row is not
/// touched, so its concurrency token stays valid. That is the difference from a recipe's own asset links, which
/// are an edit of the recipe (<see cref="IRecipeAssetLinkBusiness"/>).
/// </para>
/// <para>
/// <strong>Nothing here touches the asset.</strong> Attaching writes a row naming it; detaching removes that
/// row. The asset, its versions and its usage history are the library's.
/// </para>
/// </remarks>
public interface ITestAttachmentBusiness
{
    /// <summary>The test's attachments in order.</summary>
    /// <returns>The list, or a failure carrying <see cref="RecipeErrorCodes.TestRunNotFound"/>.</returns>
    Task<OperationResult<IReadOnlyList<TestAttachmentServiceModel>>> ListAsync(
        Guid recipeId, Guid testRunId, CancellationToken cancellationToken);

    /// <summary>Attaches one asset to the test, at the end of its attachments.</summary>
    /// <param name="target">What the asset the request names resolved to, in the caller's workspace.</param>
    /// <returns>
    /// The attachment, or a failure carrying <see cref="RecipeErrorCodes.TestRunNotFound"/>,
    /// <see cref="RecipeErrorCodes.RecipeArchivedConflict"/>,
    /// <see cref="RecipeErrorCodes.TestAttachmentTargetUnprocessable"/>,
    /// <see cref="RecipeErrorCodes.TestAttachmentDuplicateConflict"/> or
    /// <see cref="RecipeErrorCodes.TestAttachmentConflict"/>.
    /// </returns>
    Task<OperationResult<TestAttachmentServiceModel>> AttachAsync(
        Guid recipeId,
        Guid testRunId,
        CanonicalTestAttachment request,
        RecipeAssetLinkTargetState target,
        CancellationToken cancellationToken);

    /// <summary>Removes one attachment from the test. The asset it named is not touched.</summary>
    /// <returns>
    /// The attachment that was removed, or a failure carrying <see cref="RecipeErrorCodes.TestRunNotFound"/>,
    /// <see cref="RecipeErrorCodes.RecipeArchivedConflict"/> or
    /// <see cref="RecipeErrorCodes.TestAttachmentNotFound"/>.
    /// </returns>
    Task<OperationResult<TestAttachmentServiceModel>> DetachAsync(
        Guid recipeId, Guid testRunId, Guid attachmentId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ITestAttachmentBusiness"/>
internal sealed class TestAttachmentBusiness(ITestAttachmentDataLayer dataLayer) : ITestAttachmentBusiness
{
    private const string CannotAttach = "That picture cannot be attached as described.";

    public async Task<OperationResult<IReadOnlyList<TestAttachmentServiceModel>>> ListAsync(
        Guid recipeId, Guid testRunId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindRunAsync(recipeId, testRunId, cancellationToken) is null)
        {
            return RunNotFound<IReadOnlyList<TestAttachmentServiceModel>>();
        }

        var attachments = await dataLayer.ListAsync(testRunId, cancellationToken);

        return OperationResult<IReadOnlyList<TestAttachmentServiceModel>>.Success([.. attachments.Select(Map)]);
    }

    public async Task<OperationResult<TestAttachmentServiceModel>> AttachAsync(
        Guid recipeId,
        Guid testRunId,
        CanonicalTestAttachment request,
        RecipeAssetLinkTargetState target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await Refusal(recipeId, testRunId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        // One sentence for an asset that is unknown, a neighbour's or removed, so an attach request cannot be
        // used to ask what another workspace owns (tenancy.md).
        if (target == RecipeAssetLinkTargetState.AssetNotFound)
        {
            return Unprocessable(
                nameof(AttachTestImageViewModel.MediaAssetId), "That picture is not in this workspace's library.");
        }

        if (target == RecipeAssetLinkTargetState.VersionNotFound)
        {
            return Unprocessable(
                nameof(AttachTestImageViewModel.VersionNumber), "That picture has no version with that number.");
        }

        // The key behind TestIssueId pins the workspace, not the test, so "an issue of this test" is decided
        // here. An issue of another test — or of another workspace — gets the same answer as an id that names
        // nothing.
        if (request.TestIssueId is { } issueId
            && !await dataLayer.IssueBelongsToRunAsync(testRunId, issueId, cancellationToken))
        {
            return Unprocessable(
                nameof(AttachTestImageViewModel.TestIssueId), "That is not an issue of this test.");
        }

        var existing = await dataLayer.ListAsync(testRunId, cancellationToken);
        if (existing.Any(attachment =>
            attachment.MediaAssetId == request.MediaAssetId && attachment.TestIssueId == request.TestIssueId))
        {
            // The same picture against the same thing twice is a double submission. The same picture against
            // the test and against one of its issues is two different statements, and is allowed.
            return Failure(
                RecipeErrorCodes.TestAttachmentDuplicateConflict,
                "That picture is already attached to this test there.");
        }

        var attachment = new TestAttachmentLink
        {
            Id = Guid.NewGuid(),
            RecipeTestRunId = testRunId,
            TestIssueId = request.TestIssueId,
            MediaAssetId = request.MediaAssetId,
            MediaAssetVersionNumber = request.VersionNumber,
            Caption = request.Caption,
        };

        return await dataLayer.AttachAsync(attachment, cancellationToken) == TestAttachmentWriteOutcome.Committed
            ? OperationResult<TestAttachmentServiceModel>.Success(Map(attachment))
            : Failure(
                RecipeErrorCodes.TestAttachmentConflict,
                "Another picture was attached to this test at the same moment. Nothing was saved — try again.");
    }

    public async Task<OperationResult<TestAttachmentServiceModel>> DetachAsync(
        Guid recipeId, Guid testRunId, Guid attachmentId, CancellationToken cancellationToken)
    {
        if (await Refusal(recipeId, testRunId, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        // Looked for among this test's own attachments only. One belonging to another test, or to another
        // workspace, is not among them and answers exactly as an id that names nothing does.
        var attachment = await dataLayer.FindAsync(testRunId, attachmentId, cancellationToken);
        if (attachment is null)
        {
            return Failure(
                RecipeErrorCodes.TestAttachmentNotFound, "That picture is not attached to this test.");
        }

        // Mapped before the delete, so the answer describes the row that was there.
        var removed = Map(attachment);

        await dataLayer.DetachAsync(attachment, cancellationToken);

        return OperationResult<TestAttachmentServiceModel>.Success(removed);
    }

    /// <summary>The two refusals both commands share: no such test here, or a recipe that takes no changes.</summary>
    private async Task<OperationResult<TestAttachmentServiceModel>?> Refusal(
        Guid recipeId, Guid testRunId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindRunAsync(recipeId, testRunId, cancellationToken) is not { } run)
        {
            return RunNotFound<TestAttachmentServiceModel>();
        }

        // The same rule a test's own edit obeys, in the same words: an archived recipe is on the shelf, and
        // nothing about it or its tests changes until it is brought back.
        return RecipePolicy.AcceptsContentChanges(run.RecipeStatus)
            ? null
            : Failure(
                RecipeErrorCodes.RecipeArchivedConflict,
                "This recipe is archived. Bring it back from the archive before changing its tests.");
    }

    private static TestAttachmentServiceModel Map(TestAttachmentLink attachment) =>
        new(
            attachment.Id,
            attachment.MediaAssetId,
            attachment.MediaAssetVersionNumber,
            attachment.TestIssueId,
            attachment.SortOrder,
            attachment.Caption);

    /// <summary>
    /// The one answer for a test that does not exist, one on another recipe, and one in another workspace —
    /// the code and the sentence a test's own read gives.
    /// </summary>
    private static OperationResult<T> RunNotFound<T>() =>
        OperationResult<T>.Failure(new OperationError(
            RecipeErrorCodes.TestRunNotFound, "That test could not be found.", new Dictionary<string, string[]>()));

    private static OperationResult<TestAttachmentServiceModel> Failure(string code, string message) =>
        OperationResult<TestAttachmentServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));

    private static OperationResult<TestAttachmentServiceModel> Unprocessable(string field, string message) =>
        OperationResult<TestAttachmentServiceModel>.Failure(OperationError.Validation(
            RecipeErrorCodes.TestAttachmentTargetUnprocessable, CannotAttach, [(field, message)]));
}
