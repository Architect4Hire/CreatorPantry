using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Content.Business;

public interface IPromptRecordBusiness
{
    /// <summary>
    /// Saves one prompt to the workspace's library, with the lineage it was produced from.
    /// </summary>
    /// <remarks>
    /// Takes no actor id: nothing here is audited, because the row <em>is</em> the record — it is immutable and
    /// carries its own author and timestamp, and a prompt body is the one thing an audit summary must never
    /// hold (ai.md). Authorship comes from <see cref="IWorkspaceContext.MembershipId"/>, never from input.
    /// </remarks>
    Task<OperationResult<SavedPromptRecordServiceModel>> SaveAsync(
        SavePromptRecordViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of the resolved workspace's prompt library, newest first.
    /// </summary>
    /// <remarks>
    /// No <c>OperationResult</c>, because there is no expected failure left by the time criteria exist: the
    /// filters are strings matched literally, the cursor was resolved against the scope by
    /// <see cref="PromptSearchQueryFactory"/>, and an empty library and filters nothing matches are both an
    /// empty page rather than a refusal.
    /// </remarks>
    Task<PromptSearchPageServiceModel> SearchAsync(
        PromptSearchCriteria criteria, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one prompt of the resolved workspace in full.
    /// </summary>
    /// <remarks>
    /// One expected failure, and only one: no prompt in this workspace has that id. There is no separate
    /// "belongs to another workspace" branch to write, because the query filter means this layer never sees a
    /// neighbour's row — which is what makes the two answers identical rather than merely matched.
    /// </remarks>
    Task<OperationResult<PromptDetailServiceModel>> GetDetailAsync(
        Guid promptRecordId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPromptRecordBusiness"/>
internal sealed class PromptRecordBusiness(
    IPromptRecordDataLayer dataLayer,
    IContentChannelCatalog channels,
    IRecipeFacade recipes,
    IAiProposalLookupFacade proposals,
    IWorkspaceContext workspace,
    IClock clock) : IPromptRecordBusiness
{
    public async Task<OperationResult<SavedPromptRecordServiceModel>> SaveAsync(
        SavePromptRecordViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        // The backstop for any caller that reaches Business without the facade's validator — a worker, a plugin,
        // DAM-001. The facade has already refused the same input; the rules live in one place so they cannot
        // disagree.
        var failures = PromptRecordInputChecks.Save(model, channels).ToList();
        if (failures.Count > 0)
        {
            return OperationResult<SavedPromptRecordServiceModel>.Failure(OperationError.Validation(
                ContentErrorCodes.PromptInvalid, CannotSave, failures));
        }

        if (await UnresolvedPinAsync(model, cancellationToken) is { } unresolved)
        {
            return Unprocessable(unresolved);
        }

        var record = Build(model);

        if (!await dataLayer.SaveAsync(record, cancellationToken))
        {
            // The insert collided and nothing was written. Ask the pins again rather than assume which it was: a
            // recipe deleted between the check and the insert can never be saved against, so answering "retry"
            // would send the caller round a loop that cannot end differently, while a deadlock victim or a
            // timeout genuinely can succeed next time. Same reasoning as
            // WorkspaceWeeklyThemeDataLayer.LostToAnotherWriterAsync, asked from here because the facades it
            // needs live at this layer.
            return await UnresolvedPinAsync(model, cancellationToken) is { } vanished
                ? Unprocessable(vanished)
                : OperationResult<SavedPromptRecordServiceModel>.Failure(new OperationError(
                    ContentErrorCodes.PromptConflict,
                    "The prompt could not be saved just then. Try saving it again.",
                    new Dictionary<string, string[]>()));
        }

        return OperationResult<SavedPromptRecordServiceModel>.Success(ToServiceModel(record));
    }

    public async Task<PromptSearchPageServiceModel> SearchAsync(
        PromptSearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        var (rows, hasMore, total) = await dataLayer.SearchAsync(criteria, cancellationToken);

        // The cursor is minted here, not in the repository: it is bound to the resource and filters it was
        // issued for, and a repository is handed a predicate rather than a route. PageBuilder is the shared
        // helper so that no module can drift on how a page ends.
        var page = PageBuilder.Build(rows, hasMore, criteria.Scope, ToSummary);

        return new PromptSearchPageServiceModel(page.Items, page.NextCursor, total);
    }

    public async Task<OperationResult<PromptDetailServiceModel>> GetDetailAsync(
        Guid promptRecordId, CancellationToken cancellationToken)
    {
        var record = await dataLayer.GetDetailAsync(promptRecordId, cancellationToken);

        // One refusal for "no such prompt" and for "that prompt is another workspace's", in one sentence,
        // because by the time the answer is null those were never two conditions here. Nothing names a field:
        // the id came from the route.
        return record is null
            ? OperationResult<PromptDetailServiceModel>.Failure(new OperationError(
                ContentErrorCodes.PromptNotFound,
                "That prompt could not be found.",
                new Dictionary<string, string[]>()))
            : OperationResult<PromptDetailServiceModel>.Success(ToDetail(record));
    }

    /// <summary>
    /// Maps the row to the wire shape, dropping the two columns that never leave the server.
    /// </summary>
    /// <remarks>
    /// <c>WorkspaceId</c> and <c>CreatedByMembershipId</c> are read and discarded, which is the accepted cost of
    /// a single-row entity read — see <c>IPromptRecordRepository.FindAsync</c>. <c>GeneratedImageId</c> and
    /// <c>DamAssetId</c> are dropped too, because nothing can write them yet and a field that is always null
    /// would document a capability this server does not have.
    /// </remarks>
    private static PromptDetailServiceModel ToDetail(PromptRecord record) => new(
        record.Id,
        record.ChannelKey,
        record.ImageKind,
        record.Text,
        record.GeneratedText,
        record.Label,
        record.Source,
        record.AiProposalId,
        record.RecipeId,
        record.RecipeVersionId,
        record.PromptTemplateId,
        record.PromptTemplateVersion,
        record.PromptTemplateBodyChecksum,
        record.CreatedAt);

    /// <summary>
    /// Maps a projected row to the wire shape.
    /// </summary>
    /// <remarks>
    /// Nothing is dropped here, unlike the recipe search's mapping — the row was projected without the
    /// membership id, the template triple and the draft in the first place, because a list page has no business
    /// reading them out of the database to discard them afterwards.
    /// </remarks>
    private static PromptSummaryServiceModel ToSummary(PromptSummaryRecord row) =>
        new(row.Id,
            row.ChannelKey,
            row.ImageKind,
            row.TextPreview,
            row.TextLength,
            row.Label,
            row.Source,
            row.RecipeId,
            row.RecipeVersionId,
            row.CreatedAt);

    /// <summary>
    /// The field naming the first pin this workspace cannot resolve, or null when every pin resolves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every pin is resolved through the facade of the module that owns it — never by reading its tables from
    /// here — and each answer is scoped to the resolved workspace, so another workspace's recipe, version or
    /// proposal is indistinguishable from one that does not exist. Both are refused.
    /// </para>
    /// <para>
    /// <strong>This is not belt-and-braces on top of the foreign keys.</strong> It is what turns a storage
    /// exception into a field error a creator can act on, and for the version pin it is the only check that
    /// runs before an immutable row exists. The keys remain the authority — a wrong pin is unrepresentable, not
    /// merely refused.
    /// </para>
    /// <para>
    /// A recipe pinned without a version is resolved as a recipe; with one, the snapshot read resolves both at
    /// once and refuses a version belonging to some other recipe. That read carries more than an existence
    /// check needs, which on a write this rare is cheaper than widening another module's facade for it.
    /// </para>
    /// </remarks>
    private async Task<string?> UnresolvedPinAsync(
        SavePromptRecordViewModel model, CancellationToken cancellationToken)
    {
        if (model.RecipeId is { } recipeId)
        {
            if (model.RecipeVersionId is { } versionId)
            {
                var snapshot = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);
                if (!snapshot.Succeeded)
                {
                    return nameof(model.RecipeVersionId);
                }
            }
            else
            {
                var detail = await recipes.GetDetailAsync(recipeId, cancellationToken);
                if (!detail.Succeeded)
                {
                    return nameof(model.RecipeId);
                }
            }
        }

        if (model.AiProposalId is { } proposalId
            && !await proposals.ExistsAsync(proposalId, cancellationToken))
        {
            return nameof(model.AiProposalId);
        }

        return null;
    }

    /// <summary>
    /// The row, from the request plus the two things the request may not carry.
    /// </summary>
    /// <remarks>
    /// <c>WorkspaceId</c> is left unset: stamping it is <c>WorkspaceOwnershipInterceptor</c>'s job, from the
    /// resolved context, and a value assigned here would be the only place a client-shaped id could enter.
    /// <c>GeneratedImageId</c> and <c>DamAssetId</c> are left unset because nothing can resolve them yet — see
    /// <see cref="SavePromptRecordViewModel"/>, which is why the request cannot name them either.
    /// </remarks>
    private PromptRecord Build(SavePromptRecordViewModel model) => new()
    {
        Id = Guid.NewGuid(),
        ChannelKey = PromptRecordInputChecks.Normalize(model.ChannelKey)!,
        ImageKind = model.ImageKind!.Value,
        Text = PromptRecordInputChecks.Normalize(model.Text)!,
        GeneratedText = PromptRecordInputChecks.Normalize(model.GeneratedText),
        Label = PromptRecordInputChecks.Normalize(model.Label),
        Source = model.Source!.Value,
        AiProposalId = model.AiProposalId,
        RecipeId = model.RecipeId,
        RecipeVersionId = model.RecipeVersionId,
        PromptTemplateId = PromptRecordInputChecks.Normalize(model.PromptTemplateId),
        PromptTemplateVersion = PromptRecordInputChecks.Normalize(model.PromptTemplateVersion),
        PromptTemplateBodyChecksum = PromptRecordInputChecks.Normalize(model.PromptTemplateBodyChecksum),
        CreatedByMembershipId = workspace.MembershipId,
        CreatedAt = clock.UtcNow,
    };

    private static SavedPromptRecordServiceModel ToServiceModel(PromptRecord record) => new(
        record.Id,
        record.ChannelKey,
        record.ImageKind,
        record.Text,
        record.GeneratedText,
        record.Label,
        record.Source,
        record.AiProposalId,
        record.RecipeId,
        record.RecipeVersionId,
        record.PromptTemplateId,
        record.PromptTemplateVersion,
        record.PromptTemplateBodyChecksum,
        record.CreatedAt);

    private const string CannotSave = "The prompt could not be saved.";

    /// <summary>
    /// One message for every unresolved pin, naming only the field. "No such recipe in this workspace" and "that
    /// recipe belongs to someone else" are the same sentence on purpose.
    /// </summary>
    private static OperationResult<SavedPromptRecordServiceModel> Unprocessable(string field) =>
        OperationResult<SavedPromptRecordServiceModel>.Failure(OperationError.Validation(
            ContentErrorCodes.PromptLineageUnprocessable,
            CannotSave,
            [(field, "This workspace has nothing with that id.")]));
}
