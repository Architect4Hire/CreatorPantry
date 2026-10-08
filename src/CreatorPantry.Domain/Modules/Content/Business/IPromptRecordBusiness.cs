using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Facade;
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

    /// <summary>
    /// Names the prompts that produced one DAM asset, newest first, for a caller in another module.
    /// </summary>
    /// <remarks>
    /// Not an <c>OperationResult</c>, deliberately: an asset with no prompts and an asset this workspace cannot
    /// see are both an empty list, so there is no failure to report and nothing a caller could learn from one.
    /// A DAM detail read that could not name its prompts should still show the asset.
    /// </remarks>
    Task<IReadOnlyList<AssetPromptServiceModel>> ListForAssetAsync(
        Guid damAssetId, CancellationToken cancellationToken);


    /// <summary>
    /// Reads one prompt of the resolved workspace as a plain-text download: the text, and the name to offer it
    /// under.
    /// </summary>
    /// <remarks>
    /// The same single expected failure <see cref="GetDetailAsync"/> has, answered with the same
    /// <c>OperationError</c> — the same object from the same method, not a second one written to match, so the
    /// code, the sentence and the empty field set cannot drift between the two routes.
    /// </remarks>
    Task<OperationResult<PromptTextDownloadServiceModel>> GetTextDownloadAsync(
        Guid promptRecordId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one prompt of the resolved workspace as a JSON export document: the record and the name to offer
    /// it under.
    /// </summary>
    /// <remarks>
    /// The same single expected failure the other two reads have, answered from the same method. It reads the
    /// whole row, because the document publishes every column the detail route does — so it reuses
    /// <c>GetDetailAsync</c> and the <c>ToDetail</c> mapping rather than bringing a query or a second opinion
    /// about what a prompt may publish.
    /// </remarks>
    Task<OperationResult<PromptRecordExportServiceModel>> GetRecordDownloadAsync(
        Guid promptRecordId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPromptRecordBusiness"/>
internal sealed class PromptRecordBusiness(
    IPromptRecordDataLayer dataLayer,
    IContentChannelCatalog channels,
    IRecipeFacade recipes,
    IAiProposalLookupFacade proposals,
    IGeneratedImageLookupFacade generatedImages,
    IMediaAssetLookupFacade mediaAssets,
    IWorkspaceContext workspace,
    IClock clock) : IPromptRecordBusiness
{
    public async Task<IReadOnlyList<AssetPromptServiceModel>> ListForAssetAsync(
        Guid damAssetId, CancellationToken cancellationToken) =>
        [.. (await dataLayer.ListForAssetAsync(damAssetId, cancellationToken))
            .Select(row => new AssetPromptServiceModel(
                row.Id, row.Label, row.ImageKind, row.Source, row.CreatedAt))];

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

        var pins = await ResolvePinsAsync(model, cancellationToken);

        if (pins.Refusal is { } unresolved)
        {
            return Unprocessable(unresolved.Field, unresolved.Message);
        }

        var record = Build(model, pins.Lineage);

        if (!await dataLayer.SaveAsync(record, cancellationToken))
        {
            // The insert collided and nothing was written. Ask the pins again rather than assume which it was: a
            // recipe deleted between the check and the insert can never be saved against, so answering "retry"
            // would send the caller round a loop that cannot end differently, while a deadlock victim or a
            // timeout genuinely can succeed next time. Same reasoning as
            // WorkspaceWeeklyThemeDataLayer.LostToAnotherWriterAsync, asked from here because the facades it
            // needs live at this layer.
            return (await ResolvePinsAsync(model, cancellationToken)).Refusal is { } vanished
                ? Unprocessable(vanished.Field, vanished.Message)
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
            ? OperationResult<PromptDetailServiceModel>.Failure(NoSuchPrompt())
            : OperationResult<PromptDetailServiceModel>.Success(ToDetail(record));
    }

    public async Task<OperationResult<PromptTextDownloadServiceModel>> GetTextDownloadAsync(
        Guid promptRecordId, CancellationToken cancellationToken)
    {
        var record = await dataLayer.GetTextDownloadAsync(promptRecordId, cancellationToken);

        // The same one refusal, from the same method as the detail read's.
        if (record is null)
        {
            return OperationResult<PromptTextDownloadServiceModel>.Failure(NoSuchPrompt());
        }

        // The text is passed through exactly as stored — not re-trimmed, not re-wrapped, no trailing newline
        // added. It was normalized on the way in, and a download that tidied it further would be handing the
        // creator back something other than the prompt they used.
        return OperationResult<PromptTextDownloadServiceModel>.Success(new PromptTextDownloadServiceModel(
            PromptDownloadFileName.For(
                record.Label, record.CreatedAt, PromptDownloadFileName.TextExtension),
            record.Text));
    }

    public async Task<OperationResult<PromptRecordExportServiceModel>> GetRecordDownloadAsync(
        Guid promptRecordId, CancellationToken cancellationToken)
    {
        // The whole row, through the detail read: the document publishes every column that read does, so a
        // projection of its own would be the same query written twice.
        var record = await dataLayer.GetDetailAsync(promptRecordId, cancellationToken);

        if (record is null)
        {
            return OperationResult<PromptRecordExportServiceModel>.Failure(NoSuchPrompt());
        }

        // ToDetail is what drops WorkspaceId and CreatedByMembershipId, so the writer is never handed them.
        return OperationResult<PromptRecordExportServiceModel>.Success(new PromptRecordExportServiceModel(
            PromptDownloadFileName.For(
                record.Label, record.CreatedAt, PromptDownloadFileName.JsonExtension),
            PromptRecordExportWriter.Write(ToDetail(record))));
    }

    /// <summary>
    /// No prompt in this workspace has that id — which is also the answer for a neighbour's prompt.
    /// </summary>
    /// <remarks>
    /// One method rather than one sentence written twice: every read of a prompt by id refuses in exactly these
    /// words with exactly this empty field set, and two copies would be two things to keep in step. Nothing
    /// names a field, because the id came from the route.
    /// </remarks>
    private static OperationError NoSuchPrompt() => new(
        ContentErrorCodes.PromptNotFound,
        "That prompt could not be found.",
        new Dictionary<string, string[]>());

    /// <summary>
    /// Maps the row to the wire shape, dropping the two columns that never leave the server.
    /// </summary>
    /// <remarks>
    /// <c>WorkspaceId</c> and <c>CreatedByMembershipId</c> are read and discarded, which is the accepted cost of
    /// a single-row entity read — see <c>IPromptRecordRepository.FindAsync</c>. <c>GeneratedImageId</c> and
    /// <c>DamAssetId</c> are dropped too. <c>DamAssetId</c> because nothing can write it until 12.9;
    /// <c>GeneratedImageId</c> — writable since 12.6 — because an id is only useful to a client that can
    /// fetch the image, and the authorized retrieval that resolves one arrives with 12.8. Published then,
    /// which the export's own rule makes an additive change rather than a new schema version.
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
    private async Task<PromptRecordPins> ResolvePinsAsync(
        SavePromptRecordViewModel model, CancellationToken cancellationToken)
    {
        if (model.RecipeId is { } recipeId)
        {
            if (model.RecipeVersionId is { } versionId)
            {
                var snapshot = await recipes.GetSnapshotAsync(recipeId, versionId, cancellationToken);
                if (!snapshot.Succeeded)
                {
                    return PromptRecordPins.Missing(nameof(model.RecipeVersionId));
                }
            }
            else
            {
                var detail = await recipes.GetDetailAsync(recipeId, cancellationToken);
                if (!detail.Succeeded)
                {
                    return PromptRecordPins.Missing(nameof(model.RecipeId));
                }
            }
        }

        // Resolved through the Media module's facade inside the resolved workspace, which is what 12.3's note
        // asked for and what 12.3a refused to accept an id without. The composite foreign key is still the
        // authority; this is what turns a storage exception into a field error a creator can act on.
        if (model.GeneratedImageId is { } imageId
            && !await generatedImages.ExistsAsync(imageId, cancellationToken))
        {
            return PromptRecordPins.Missing(nameof(model.GeneratedImageId));
        }

        // The same shape for the DAM asset (12.9a), and for the same reason: this row is immutable, so an
        // id it cannot resolve is one nobody could ever correct. A soft-deleted asset answers false.
        if (model.DamAssetId is { } damAssetId
            && !await mediaAssets.ExistsAsync(damAssetId, cancellationToken))
        {
            return PromptRecordPins.Missing(nameof(model.DamAssetId));
        }

        if (model.AiProposalId is not { } proposalId)
        {
            // A template is a fact about a generation, and the only record of a generation is its proposal
            // (12.10l). Without one there is nothing to check the claim against, and this row is immutable:
            // a hand-typed prompt saved as "image.prompt 1.0.0" would say so for good. So the triple is
            // accepted only alongside the proposal it is then derived from, and refused on its own.
            foreach (var (field, value) in new (string, string?)[]
            {
                (nameof(model.PromptTemplateId), model.PromptTemplateId),
                (nameof(model.PromptTemplateVersion), model.PromptTemplateVersion),
                (nameof(model.PromptTemplateBodyChecksum), model.PromptTemplateBodyChecksum),
            })
            {
                if (PromptRecordInputChecks.Normalize(value) is not null)
                {
                    return PromptRecordPins.Refused(
                        field, "A prompt template can only be recorded with the AI proposal it came from.");
                }
            }

            return PromptRecordPins.Resolved(null);
        }

        var lineage = await proposals.FindLineageAsync(proposalId, cancellationToken);

        if (lineage is null)
        {
            return PromptRecordPins.Missing(nameof(model.AiProposalId));
        }

        // 12.3a's owed decision, first half: the proposal has to be about the recipe this record pins. Before
        // this, any proposal the workspace held satisfied any pin, so a prompt could claim to have come from a
        // generation about an entirely different recipe — permanently, because the row is immutable.
        if (model.RecipeId is { } pinned && lineage.RecipeId is { } subject && pinned != subject)
        {
            return PromptRecordPins.Refused(
                nameof(model.AiProposalId),
                "That proposal was about a different recipe, so it is not where this prompt came from.");
        }

        // The version too, when both state one. A proposal pinned to version 3 is not the provenance of a
        // prompt about version 7, and the recipe-level check above cannot see the difference.
        if (model.RecipeVersionId is { } pinnedVersion
            && lineage.RecipeVersionId is { } subjectVersion
            && pinnedVersion != subjectVersion)
        {
            return PromptRecordPins.Refused(
                nameof(model.AiProposalId),
                "That proposal was about a different version of this recipe.");
        }

        // And which capability produced it. A prompt declares its own source, so a generation of some other
        // kind is not its provenance however real it is — before this, a recipe-concepts proposal could be
        // recorded as the origin of a saved image prompt.
        if (ExpectedTask(model.Source!.Value) is { } expected && lineage.TaskType != expected)
        {
            return PromptRecordPins.Refused(
                nameof(model.AiProposalId),
                "That proposal came from a different kind of generation than this prompt says it did.");
        }

        // Second half: the template triple is the proposal's own, so a request that contradicts it is refused
        // rather than stored. Equally, a request that agrees costs nothing — see Build, which takes the
        // server's values either way, so what is stored is never the client's claim about the server's data.
        if (Disagrees(model.PromptTemplateId, lineage.PromptTemplateId) is { } idMismatch)
        {
            return PromptRecordPins.Refused(nameof(model.PromptTemplateId), idMismatch);
        }

        if (Disagrees(model.PromptTemplateVersion, lineage.PromptTemplateVersion) is { } versionMismatch)
        {
            return PromptRecordPins.Refused(nameof(model.PromptTemplateVersion), versionMismatch);
        }

        if (Disagrees(model.PromptTemplateBodyChecksum, lineage.PromptTemplateBodyChecksum) is { } sumMismatch)
        {
            return PromptRecordPins.Refused(nameof(model.PromptTemplateBodyChecksum), sumMismatch);
        }

        return PromptRecordPins.Resolved(lineage);
    }

    /// <summary>
    /// The complaint when a request's template value is not the proposal's, or null when it agrees.
    /// </summary>
    /// <remarks>
    /// A client that sends nothing is not contradicting anything — the shape rules already require all three
    /// for a generated prompt, and <see cref="PromptRecordInputChecks"/> is where that is enforced. This is
    /// only about a value that disagrees.
    /// </remarks>
    /// <summary>
    /// Which AI task a prompt of this source must have come from, or null when nothing constrains it.
    /// </summary>
    /// <remarks>
    /// <c>Manual</c> never reaches here — a hand-written prompt names no proposal at all.
    /// <c>ReferenceImageAnalysis</c> learned its own task type when IMG-004 shipped: this switch is the
    /// one line the comment here was waiting for, so a prompt saved as a reading of an image can only cite
    /// a proposal that actually read one.
    /// </remarks>
    private static AiTaskType? ExpectedTask(PromptRecordSource source) => source switch
    {
        PromptRecordSource.PhotographyConcept => AiTaskType.PhotographyConcept,
        PromptRecordSource.ImagePromptComposition => AiTaskType.ImagePrompt,
        PromptRecordSource.ReferenceImageAnalysis => AiTaskType.ReferenceImageAnalysis,
        _ => null,
    };

    private static string? Disagrees(string? supplied, string recorded) =>
        PromptRecordInputChecks.Normalize(supplied) is { } value
        && !string.Equals(value, recorded, StringComparison.Ordinal)
            ? "That is not what the proposal records, so it is not the template that wrote this draft."
            : null;

    /// <summary>
    /// The row, from the request plus the two things the request may not carry.
    /// </summary>
    /// <remarks>
    /// <c>WorkspaceId</c> is left unset: stamping it is <c>WorkspaceOwnershipInterceptor</c>'s job, from the
    /// resolved context, and a value assigned here would be the only place a client-shaped id could enter.
    /// <c>GeneratedImageId</c> is copied from the request, which since 12.6 may carry one: <c>ResolvePinsAsync</c>
    /// has already resolved it through the Media facade inside this workspace, and the composite foreign key
    /// refuses a value that got past it. <c>DamAssetId</c> stays unset, because nothing can resolve one until
    /// 12.9 — see <see cref="SavePromptRecordViewModel"/>, which is why the request cannot name it either.
    /// <para>
    /// <strong>The template triple is the proposal's, not the request's</strong> (12.3a's owed decision). The
    /// server already holds it on the proposal that wrote the draft, so it is stamped from there; a request
    /// that supplied a <em>different</em> value never reaches here, having been refused by
    /// <c>ResolvePinsAsync</c>. The request's own fields are still required by the shape rules, because a
    /// client claiming a provenance it cannot name is a client that has lost track of what it is saving — but
    /// what is stored is the server's record either way, and the row is immutable, so this is the one chance
    /// to get it right.
    /// </para>
    /// </remarks>
    private PromptRecord Build(
        SavePromptRecordViewModel model, AiProposalLineageServiceModel? lineage) => new()
    {
        Id = Guid.NewGuid(),
        ChannelKey = PromptRecordInputChecks.Normalize(model.ChannelKey)!,
        ImageKind = model.ImageKind!.Value,
        Text = PromptRecordInputChecks.Normalize(model.Text)!,
        GeneratedText = PromptRecordInputChecks.Normalize(model.GeneratedText),
        Label = PromptRecordInputChecks.Normalize(model.Label),
        Source = model.Source!.Value,
        AiProposalId = model.AiProposalId,

        // Accepted from the request since 12.6, resolved above, and settable only here: the row is immutable,
        // so this is the one moment a prompt can be tied to the image it produced.
        GeneratedImageId = model.GeneratedImageId,
        DamAssetId = model.DamAssetId,
        RecipeId = model.RecipeId,
        RecipeVersionId = model.RecipeVersionId,
        // From the proposal and from nowhere else (12.10l). The request's own copies were checked against
        // these when there is a proposal and refused when there is not, so nothing a client typed is ever
        // what gets stored here.
        PromptTemplateId = lineage?.PromptTemplateId,
        PromptTemplateVersion = lineage?.PromptTemplateVersion,
        PromptTemplateBodyChecksum = lineage?.PromptTemplateBodyChecksum,
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

    private static OperationResult<SavedPromptRecordServiceModel> Unprocessable(
        string field, string message) =>
        OperationResult<SavedPromptRecordServiceModel>.Failure(OperationError.Validation(
            ContentErrorCodes.PromptLineageUnprocessable,
            CannotSave,
            [(field, message)]));

    /// <summary>
    /// What the pins resolved to: a refusal naming one field, or the proposal's own lineage to stamp.
    /// </summary>
    /// <remarks>
    /// Both are null for a prompt that pins nothing, which is the common case and not a failure.
    /// </remarks>
    private sealed record PromptRecordPins(
        (string Field, string Message)? Refusal,
        AiProposalLineageServiceModel? Lineage)
    {
        /// <summary>
        /// Nothing in this workspace has that id.
        /// </summary>
        /// <remarks>
        /// One sentence for a recipe, version or proposal that does not exist and for one belonging to a
        /// neighbour — the same wording on purpose, so a save cannot be used to ask what another workspace
        /// owns (tenancy.md).
        /// </remarks>
        public static PromptRecordPins Missing(string field) =>
            new((field, "This workspace has nothing with that id."), null);

        /// <summary>
        /// The id resolves, but what it names disagrees with what the request claims about it.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Missing"/> in wording but not in code or status: both are the same
        /// <c>422</c> with the same code, and this one discloses only facts about the caller's own workspace —
        /// that their proposal was about a different recipe, or records a different template.
        /// </remarks>
        public static PromptRecordPins Refused(string field, string message) =>
            new((field, message), null);

        public static PromptRecordPins Resolved(AiProposalLineageServiceModel? lineage) => new(null, lineage);
    }
}
