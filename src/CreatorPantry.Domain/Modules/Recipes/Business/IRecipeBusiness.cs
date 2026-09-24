using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Recipes.Business;

/// <summary>
/// Domain rules for recipes: the invariants, the mapping from creator input to the aggregate, and the
/// decisions about what a version records. Calls the DataLayer and nothing else.
/// </summary>
public interface IRecipeBusiness
{
    /// <summary>
    /// Applies creation invariants, maps the creator's input onto a new aggregate, and writes it with its
    /// first version.
    /// </summary>
    /// <param name="input">
    /// A validated request reduced to its meaning by <see cref="CanonicalCreateRecipe.From"/>. Taking the
    /// canonical form rather than the view model keeps HTTP shapes out of Business, and means the value
    /// hashed as the idempotency fingerprint is the very value mapped here — so "same fingerprint" and "same
    /// recipe" cannot drift apart. Format rules are not re-checked; what is checked is what a validator
    /// structurally could not know.
    /// </param>
    /// <param name="yieldUnitDimension">
    /// The dimension of <c>input.YieldUnitId</c>, or <c>null</c> when no unit was named.
    /// </param>
    /// <remarks>
    /// The dimension is a parameter rather than something looked up here, and that is a boundary decision
    /// rather than a convenience. Measurement units belong to another module, whose only permitted entry
    /// point is its facade — and Business may call nothing but its own DataLayer. So the Facade resolves it
    /// and passes the fact down. The same applies to whether the cuisine, course and technique ids exist and
    /// are active: <strong>the Facade must verify those before calling this method</strong>, because nothing
    /// below will, and an unverified id becomes a foreign-key violation at save time — a 500 where the honest
    /// answer is a 400 naming the field.
    /// </remarks>
    Task<OperationResult<CreatedRecipeServiceModel>> CreateAsync(
        CanonicalCreateRecipe input,
        MeasurementDimension? yieldUnitDimension,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one recipe as a client sees it.
    /// </summary>
    /// <returns>
    /// The recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>.
    /// </returns>
    /// <remarks>
    /// The not-found decision is made here rather than above, because whether a resource exists for this
    /// caller is a resource-level authorization question and those stay in Business. It answers the same way
    /// for a recipe that was never created and for one belonging to another workspace — not as a convenience,
    /// but because the layer beneath cannot tell them apart and this layer must not be able to either
    /// (tenancy.md).
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a creator's partial edit and captures the version that records it.
    /// </summary>
    /// <param name="recipeId">The recipe to edit, from the route.</param>
    /// <param name="patch">
    /// A validated request reduced to its meaning by <see cref="CanonicalRecipePatch.From"/>. Each field
    /// still carries whether it was submitted, because that — not its value — is what decides whether it is
    /// being changed, cleared, or left alone.
    /// </param>
    /// <param name="submittedYieldUnitDimension">
    /// The dimension of the yield unit <em>if the request submitted one</em>, and <c>null</c> otherwise —
    /// including when the request submitted <c>null</c> to clear the unit. When the unit was not submitted,
    /// the recipe's stored dimension stands, because the unit is not changing.
    /// </param>
    /// <returns>
    /// The edited recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.RecipeInvalidRequest"/> or
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Invariants are checked against the merged recipe, not against the request.</strong> A patch
    /// names only part of a recipe, so "does the total time still make sense" and "does this yield unit
    /// still have a quantity beside it" cannot be answered from the body — a request that clears the yield
    /// quantity is only wrong because of a unit it never mentioned. This is the one real difference from
    /// <see cref="CreateAsync"/>, where a complete request let the edge answer some of it.
    /// </para>
    /// <para>
    /// <strong>An edit that changes nothing writes nothing.</strong> Not an optimisation: a version that
    /// records no change is noise in a history a creator reads, and stamping <c>UpdatedAt</c> for it would
    /// invalidate every concurrency token their collaborators hold for no reason.
    /// </para>
    /// <para>
    /// As on <see cref="CreateAsync"/>, <strong>the caller must have verified the submitted reference ids
    /// first</strong>: nothing below here will, and an unverified id becomes a foreign-key violation at save
    /// time — a 500 where the honest answer is a 400 naming the field.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> UpdateAsync(
        Guid recipeId,
        CanonicalRecipePatch patch,
        MeasurementDimension? submittedYieldUnitDimension,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of the workspace's recipes, as the client reads it: summaries, the cursor that resumes
    /// them, and the total when it was asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No failure case, and so no <c>OperationResult</c>. Every way this request can be refused — an unparseable
    /// filter, a cursor from another scope — has already been settled above, by the validator and the query
    /// factory, before a criteria existed to hand down here. A workspace with no recipes and filters that match
    /// nothing are both an empty page, not an error, and a search must not be able to say "not found" about a
    /// workspace the caller is demonstrably a member of.
    /// </para>
    /// <para>
    /// No role gate, for the same reason <see cref="GetDetailAsync"/> gives: <c>Viewer</c> is the lowest role, so
    /// a resolved workspace context already is the authorization.
    /// </para>
    /// </remarks>
    Task<RecipeSearchPageServiceModel> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a recipe's history, newest first, as the client reads it: version metadata and the
    /// cursor that resumes it.
    /// </summary>
    /// <returns>
    /// The page, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> — the single answer for a
    /// recipe that does not exist and one that belongs to another workspace.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A failure case, unlike <see cref="SearchAsync"/>, and the difference is which question is being asked.
    /// A search asks about a workspace the caller demonstrably belongs to, so an empty answer is an answer.
    /// This asks about one named recipe, and whether that recipe exists is precisely what must not be
    /// disclosed.
    /// </para>
    /// <para>
    /// No role gate, for the reason <see cref="GetDetailAsync"/> gives: <c>Viewer</c> is the lowest role, so a
    /// resolved workspace context already is the authorization. Someone who may read a recipe may read how it
    /// came to say what it says.
    /// </para>
    /// <para>
    /// <strong>Read-only, necessarily.</strong> These rows are immutable and this seam has no write path — a
    /// correction to the history is a new version, written by an edit or a restore, never a change here.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeVersionHistoryPageResult>> GetVersionHistoryAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Compares two of one recipe's versions and returns what differs between them.
    /// </summary>
    /// <param name="recipeId">The recipe whose versions to compare, resolved from the route.</param>
    /// <param name="fromVersionNumber">The version reported as the <c>from</c> side. Need not be the lower number.</param>
    /// <param name="toVersionNumber">The version reported as the <c>to</c> side.</param>
    /// <returns>
    /// The comparison, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> when the recipe is
    /// not visible here, or <see cref="RecipeErrorCodes.VersionNotFound"/> when one of the numbers names no
    /// version of it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Nothing is generated and nothing is written.</strong> The answer is
    /// <see cref="RecipeComparer"/>'s arithmetic over two documents this server read for itself, which is the
    /// same requirement REC-008 and AIREC-GR-002 state from either side. Comparing two versions leaves no
    /// record that it happened.
    /// </para>
    /// <para>
    /// <strong>The two numbers may be equal</strong>, and the honest answer is a comparison with nothing in
    /// it. The single row the query returns is read as both sides rather than reported as a missing version.
    /// </para>
    /// <para>
    /// <strong>An unreadable stored document raises rather than returns.</strong>
    /// <see cref="RecipeSnapshotDocument.MinimumReadableSchemaVersion"/> guarantees that documents written by
    /// older builds stay readable, so the only way here is a document written by a <em>newer</em> build than
    /// the one serving the request — a rolled-back deployment reading forward. That is a server fault and a
    /// 500 is the truthful status; giving it an error code would invite a client to handle a condition it
    /// cannot do anything about.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid recipeId,
        int fromVersionNumber,
        int toVersionNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Puts a recipe back to what one of its versions said, and captures the new version that records having
    /// done so.
    /// </summary>
    /// <param name="recipeId">The recipe to restore, from the route.</param>
    /// <param name="versionNumber">Which version's content to put back, from the route.</param>
    /// <param name="request">
    /// The state the restore was composed against, and why the creator did it. Carries no content: a restore
    /// takes all of it from the archive.
    /// </param>
    /// <returns>
    /// The restored recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/>,
    /// <see cref="RecipeErrorCodes.VersionNotFound"/> or <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>History is appended to, never rewritten.</strong> The version being restored is read and left
    /// exactly where it is — it does not become current, is not renumbered, and is not deleted. What the
    /// creator gets is a <em>new</em> current version whose content was copied from it, whose parent is the
    /// version it replaced, and whose <c>RestoredFromVersionId</c> names where the content came from. Those
    /// are two different ancestors and the requirement needs both.
    /// </para>
    /// <para>
    /// <strong>An unknown version number is answered before the concurrency token is checked.</strong> Both
    /// are refusals, but only one is worth re-reading for: version 99 will not appear on a second look, so
    /// telling that caller to reload would send them round a loop that cannot end. Nothing is disclosed by
    /// the ordering — see <see cref="RecipeErrorCodes.VersionNotFound"/>, which is returned only to a caller
    /// already shown that this recipe exists.
    /// </para>
    /// <para>
    /// <strong>A restore that would change nothing writes nothing</strong>, for the reason
    /// <see cref="UpdateAsync"/> gives, and with a second benefit here: it makes a creator who submits the
    /// same restore twice — or a client with no idempotency key that retries after re-reading — receive the
    /// recipe rather than a duplicate version.
    /// </para>
    /// <para>
    /// <strong>The archived vocabulary ids are not re-verified.</strong> A cuisine, course, technique or unit
    /// that has since been retired is still restored, exactly as <see cref="UpdateAsync"/> declines to
    /// re-check a reference an edit never mentioned: the creator did not name these ids, cannot do anything
    /// about a catalogue decision, and refusing their own history would be an answer with no remedy. Nothing
    /// deletes a reference row, so the foreign keys hold. Tags are the exception, because their vocabulary is
    /// the workspace's own — see <c>RecipeSnapshotReconciler</c>.
    /// </para>
    /// <para>
    /// <strong>An unreadable stored document raises rather than returns</strong>, for the reason
    /// <see cref="CompareVersionsAsync"/> gives: the only way there is a document written by a newer build
    /// than the one serving the request, which is a server fault and truthfully a 500.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> RestoreVersionAsync(
        Guid recipeId,
        int versionNumber,
        CanonicalRestoreRecipeVersion request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Copies one version of a recipe into a new, independent recipe with its own first version.
    /// </summary>
    /// <param name="recipeId">The recipe to copy from, resolved from the route.</param>
    /// <param name="request">The copy's title, and which version to copy.</param>
    /// <returns>
    /// The new recipe, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> when the source is
    /// not visible here, or <see cref="RecipeErrorCodes.VersionNotFound"/> when the number names no version
    /// of it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The copy is canonical source material, not a derivative.</strong> Nothing links the two
    /// recipes' content: editing either does nothing to the other, and the copy has its own version history
    /// beginning at 1. <c>Recipe.DuplicatedFromVersionId</c> records where the content came from, which is
    /// provenance rather than dependence — content.md's derivative rules govern blog drafts and social copy
    /// generated <em>from</em> a recipe, and a recipe is not one of those.
    /// </para>
    /// <para>
    /// <strong>The copy is always a Draft</strong>, whatever the source said. A copy of a finished recipe is
    /// not itself finished — it exists to be taken somewhere else — and duplicating an archived recipe must
    /// not produce an archived copy that the creator then has to go and find.
    /// </para>
    /// <para>
    /// <strong>Every identity is minted fresh</strong>, down to each ingredient line and step. Sharing a
    /// child id between two recipes is not merely untidy: the ids are primary keys, and a diff matches
    /// content by them.
    /// </para>
    /// <para>
    /// <strong>The archived content is not re-validated</strong>, for the reason <see cref="UpdateAsync"/>
    /// declines to re-check a reference an edit never mentioned. A recipe written before a limit tightened,
    /// or naming a cuisine since retired, is still the creator's recipe; refusing to copy it would be an
    /// answer with no remedy. Nothing submitted this content, so there is nothing here a client could have
    /// got wrong.
    /// </para>
    /// <para>
    /// <strong>Tags are copied by resolving the archive's ids back to names</strong> and handing them to the
    /// create, which owns tag attachment for a new recipe. An id whose vocabulary row is gone resolves to
    /// nothing and is simply not applied.
    /// </para>
    /// </remarks>
    Task<OperationResult<CreatedRecipeServiceModel>> DuplicateAsync(
        Guid recipeId,
        CanonicalDuplicateRecipe request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Shelves a recipe: moves it to <see cref="RecipeStatus.Archived"/>, records the move, and leaves
    /// everything it contains exactly where it is.
    /// </summary>
    /// <param name="recipeId">The recipe to archive, from the route.</param>
    /// <param name="actorUserId">The authenticated caller, for the audit entry. Never from a request field.</param>
    /// <param name="expectedConcurrencyToken">The state the command was composed against.</param>
    /// <returns>
    /// The recipe as it now stands, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Not a delete, and not a soft delete either.</strong> Every version, tag, media link,
    /// ingredient line and step survives untouched, and the recipe stays readable by id — what changes is
    /// that it drops out of the default library listing and stops accepting content changes. Reading it,
    /// listing its history, comparing its versions and copying it all keep working.
    /// </para>
    /// <para>
    /// <strong>Archiving an archived recipe is a success that does nothing.</strong> No audit entry, no
    /// stamped <c>UpdatedAt</c>, no invalidated token: the creator asked for a state the recipe is already
    /// in, and the honest answer is the recipe. That also makes the command safe to retry without an
    /// idempotency key, which is why it has none.
    /// </para>
    /// <para>
    /// <strong>No version is written.</strong> A version records what a recipe said, and this changes
    /// nothing it says — see <see cref="IRecipeDataLayer.TrySetStatusAsync"/>, which also explains why that
    /// keeps a later version restore from archiving a recipe without an audit trail.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Brings a recipe back from the archive, as a <see cref="RecipePolicy.UnarchivedStatus"/>.
    /// </summary>
    /// <inheritdoc cref="ArchiveAsync" path="/param"/>
    /// <returns>
    /// The recipe as it now stands, or a failure carrying <see cref="RecipeErrorCodes.RecipeNotFound"/> or
    /// <see cref="RecipeErrorCodes.RecipeConflict"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="ArchiveAsync"/> in every respect, including that unarchiving a recipe which
    /// is not archived succeeds and does nothing.
    /// </para>
    /// <para>
    /// <strong>It returns to Draft, not to whatever it was before.</strong>
    /// <see cref="RecipePolicy.UnarchivedStatus"/> records why.
    /// </para>
    /// </remarks>
    Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken);
}

internal sealed class RecipeBusiness(
    IRecipeDataLayer dataLayer,
    IWorkspaceContext workspace,
    IClock clock) : IRecipeBusiness
{
    public async Task<OperationResult<CreatedRecipeServiceModel>> CreateAsync(
        CanonicalCreateRecipe input,
        MeasurementDimension? yieldUnitDimension,
        CancellationToken cancellationToken)
    {
        if (Validate(input, yieldUnitDimension) is { } error)
        {
            return OperationResult<CreatedRecipeServiceModel>.Failure(error);
        }

        // One read of the clock for the whole operation, so the recipe, its version and any tag born with it
        // all agree on when this happened.
        var now = clock.UtcNow;

        var recipe = new Recipe
        {
            Id = Guid.NewGuid(),

            // WorkspaceId is deliberately not set. The ownership interceptor stamps it from the resolved
            // context; assigning it here is the defect tenancy.md names.
            Title = input.Title,
            Description = input.Description,
            Headnote = input.Headnote,
            Notes = input.Notes,
            StorageNotes = input.StorageNotes,
            AttributionText = input.AttributionText,
            SourceUrl = input.SourceUrl,

            CuisineId = input.CuisineId,
            CourseId = input.CourseId,
            PrimaryTechniqueId = input.PrimaryTechniqueId,

            PrepTimeMinutes = input.PrepTimeMinutes,
            CookTimeMinutes = input.CookTimeMinutes,
            RestTimeMinutes = input.RestTimeMinutes,
            TotalTimeMinutes = input.TotalTimeMinutes,

            YieldText = input.YieldText,
            YieldQuantity = input.YieldQuantity,
            YieldUnitId = input.YieldUnitId,
            // Derived, never accepted from the request: a client able to send this could contradict the unit
            // it names, and the composite foreign key would then be pinning a lie.
            YieldUnitDimension = input.YieldUnitId is null ? null : yieldUnitDimension,

            Status = input.Status,

            // The authenticated creator, from the resolved membership. Never from the request body.
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedByMembershipId = workspace.MembershipId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var index = 0; index < input.Instructions.Count; index++)
        {
            recipe.InstructionGroups.Add(BuildInstructionGroup(recipe, input.Instructions[index], index));
        }

        var version = new RecipeVersionFacts(
            RecipeVersionSource.CreatorEdit,

            // The version inherits the recipe's editorial state rather than inventing one: a recipe created
            // as Ready has a first version that is ready, and one created as a draft does not.
            input.Status == RecipeStatus.Ready ? RecipeVersionReadiness.Ready : RecipeVersionReadiness.Draft,

            // No reason. RecipeVersion.Reason is documented as optional — "a routine save has no reason" —
            // and "this is the first version" is already carried by the version number. Inventing a sentence
            // here would put user-visible English in the domain that nothing can localize.
            Reason: null);

        var created = await dataLayer.CreateAsync(recipe, version, input.Tags, cancellationToken);

        // Mapping the persistence result to the application's own output shape is Business.s job (backend.md);
        // the layers above never see what the DataLayer returned.
        return OperationResult<CreatedRecipeServiceModel>.Success(new CreatedRecipeServiceModel(
            created.RecipeId,
            recipe.Title,
            recipe.Status,
            created.VersionId,
            created.VersionNumber,
            recipe.CreatedAt));
    }

    public async Task<RecipeSearchPageServiceModel> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var (rows, hasMore, total) = await dataLayer.SearchAsync(criteria, cancellationToken);

        // The cursor is minted here, not in the repository: it is bound to the resource and filters it was
        // issued for, and a repository is handed a predicate rather than a route. PageBuilder is the shared
        // helper so that no module can drift on how a page ends.
        var page = PageBuilder.Build(rows, hasMore, criteria.Scope, ToSummary);

        return new RecipeSearchPageServiceModel(page.Items, page.NextCursor, total);
    }

    /// <summary>
    /// Maps a repository row to the wire shape, dropping what must not leave the server.
    /// </summary>
    /// <remarks>
    /// The membership ids and the sort key the row carries are both absent from the result, for different
    /// reasons: the membership columns never leave the server (tenancy.md), and the sort key exists only so the
    /// row can say what it was ordered by, which is a fact about the query rather than about the recipe.
    /// </remarks>
    private static RecipeSummaryServiceModel ToSummary(RecipeSummaryRecord row) =>
        new(row.Id,
            row.Title,
            row.Description,
            row.Status,
            row.CuisineId,
            row.CourseId,
            row.CreatedAt,
            row.UpdatedAt,
            row.LatestVersionNumber,
            row.LatestVersionReadiness,
            row.HasUnmatchedIngredients);

    public async Task<OperationResult<RecipeVersionHistoryPageResult>> GetVersionHistoryAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken)
    {
        var page = await dataLayer.ListVersionsAsync(criteria, cancellationToken);

        if (page is not { } history)
        {
            // The recipe is not visible. Answered identically to one that was never created, because the layer
            // beneath cannot tell them apart and this one must not be able to either (tenancy.md).
            return NotFound<RecipeVersionHistoryPageResult>();
        }

        // The cursor is minted here rather than in the repository, for the reason PageBuilder exists: it is
        // bound to the recipe and workspace it was issued for, which a repository is never told.
        //
        // The authorship travels beside the page rather than inside it: naming a membership needs two other
        // modules, and this layer may call neither. See RecipeVersionHistoryPageResult.
        return OperationResult<RecipeVersionHistoryPageResult>.Success(new RecipeVersionHistoryPageResult(
            PageBuilder.Build(history.Rows, history.HasMore, criteria.Scope, ToHistoryEntry),
            history.Rows.ToDictionary(row => row.Id, row => row.CreatedByMembershipId)));
    }

    /// <summary>
    /// Maps a history row to the wire shape, dropping the membership id that must not leave the server.
    /// </summary>
    /// <remarks>
    /// The same rule the search mapping follows, with a more visible consequence: a history entry cannot name
    /// who wrote the version. See <see cref="RecipeVersionHistoryServiceModel"/> — the id is read for the
    /// future, not published today.
    /// </remarks>
    public async Task<OperationResult<RecipeVersionComparisonServiceModel>> CompareVersionsAsync(
        Guid recipeId,
        int fromVersionNumber,
        int toVersionNumber,
        CancellationToken cancellationToken)
    {
        var rows = await dataLayer.FindVersionSnapshotsAsync(
            recipeId, fromVersionNumber, toVersionNumber, cancellationToken);

        if (rows is null)
        {
            // The recipe is not visible. Answered identically to one that was never created (tenancy.md).
            return NotFound<RecipeVersionComparisonServiceModel>();
        }

        var from = rows.SingleOrDefault(row => row.VersionNumber == fromVersionNumber);
        var to = rows.SingleOrDefault(row => row.VersionNumber == toVersionNumber);

        // Both sides are named, so a creator who mistyped one number is told which one. Reporting only the
        // first would send them round the loop twice when they mistyped both.
        var missing = new List<(string Field, string Error)>();
        if (from is null)
        {
            missing.Add(("from", $"This recipe has no version {fromVersionNumber}."));
        }

        if (to is null && toVersionNumber != fromVersionNumber)
        {
            missing.Add(("to", $"This recipe has no version {toVersionNumber}."));
        }

        if (missing.Count > 0)
        {
            return OperationResult<RecipeVersionComparisonServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound, "Those versions could not be compared.", missing));
        }

        // Equal numbers read the one row as both sides rather than as a missing version: comparing a version
        // with itself is a legitimate question whose answer is "nothing changed".
        to ??= from;

        return OperationResult<RecipeVersionComparisonServiceModel>.Success(new RecipeVersionComparisonServiceModel
        {
            From = ToComparisonSide(from!),
            To = ToComparisonSide(to!),

            // Deserialized here rather than in the repository: reading stored text as a document is
            // translation, and deciding what an unreadable one means is a domain decision rather than a
            // persistence one.
            Comparison = RecipeComparer.Compare(
                RecipeSnapshotSerializer.Deserialize(from!.Document),
                RecipeSnapshotSerializer.Deserialize(to!.Document)),
        });
    }

    private static RecipeVersionComparisonSideServiceModel ToComparisonSide(RecipeVersionSnapshotRecord row) =>
        new()
        {
            VersionId = row.Id,
            VersionNumber = row.VersionNumber,
            Source = row.Source,
            Readiness = row.Readiness,
            CreatedAt = row.CreatedAt,
        };

    private static RecipeVersionHistoryServiceModel ToHistoryEntry(RecipeVersionHistoryRecord row) =>
        new()
        {
            Id = row.Id,
            VersionNumber = row.VersionNumber,
            Source = row.Source,
            Readiness = row.Readiness,
            Reason = row.Reason,
            CreatedAt = row.CreatedAt,
            // Left for the Facade, which is the only layer that may ask another module who this membership
            // belongs to. See RecipeVersionHistoryPageResult.
            CreatedByName = null,

            ParentVersionId = row.ParentVersionId,
            RestoredFromVersionId = row.RestoredFromVersionId,
            AiProposalId = row.AiProposalId,
        };

    public async Task<OperationResult<RecipeDetailServiceModel>> GetDetailAsync(
        Guid recipeId,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetDetailAsync(recipeId, cancellationToken);

        return loaded is null
            ? NotFound<RecipeDetailServiceModel>()
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
    }

    public async Task<OperationResult<RecipeDetailServiceModel>> UpdateAsync(
        Guid recipeId,
        CanonicalRecipePatch patch,
        MeasurementDimension? submittedYieldUnitDimension,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return NotFound<RecipeDetailServiceModel>();
        }

        var recipe = loaded.Recipe.Recipe;

        // Checked before anything is merged, so a refused edit is answered from the state it was composed
        // against rather than from a half-applied one. The database repeats this check when the save runs;
        // this one is not redundant, because it is the only one that can produce a readable answer, and it
        // catches the far commoner case — a token that went stale minutes ago, not microseconds.
        if (!RecipeConcurrencyToken.Matches(patch.ExpectedConcurrencyToken, recipe.RowVersion))
        {
            return Conflict();
        }

        // After the token check, deliberately. A caller holding a stale token has not seen the recipe as it
        // now stands — possibly including that someone archived it — so "re-read this" is the more useful of
        // the two answers and the one that leads them to the other.
        if (!RecipePolicy.AcceptsContentChanges(recipe.Status))
        {
            return ArchivedConflict();
        }

        // The yield unit and its dimension move together or not at all. When the unit was not submitted the
        // stored dimension stands, because the unit is not changing; when it was submitted as null the
        // dimension goes with it, because a dimension describes a unit that is no longer there.
        var yieldUnitId = patch.YieldUnitId.Or(recipe.YieldUnitId);
        var yieldUnitDimension = patch.YieldUnitId.IsSubmitted
            ? patch.YieldUnitId.Value is null ? null : submittedYieldUnitDimension
            : recipe.YieldUnitDimension;

        if (Validate(patch, recipe, yieldUnitId, yieldUnitDimension) is { } error)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(error);
        }

        // Collected rather than applied, so that "did anything actually change" is answered before the
        // aggregate is touched. A submitted field holding the value it already holds is not a change, and
        // treating it as one would write a version recording nothing.
        var changes = new List<Action>();

        Change<string?>(patch.Title, recipe.Title, value => recipe.Title = value!);
        Change(patch.Description, recipe.Description, value => recipe.Description = value);
        Change(patch.Headnote, recipe.Headnote, value => recipe.Headnote = value);
        Change(patch.Notes, recipe.Notes, value => recipe.Notes = value);
        Change(patch.StorageNotes, recipe.StorageNotes, value => recipe.StorageNotes = value);
        Change(patch.AttributionText, recipe.AttributionText, value => recipe.AttributionText = value);
        Change(patch.SourceUrl, recipe.SourceUrl, value => recipe.SourceUrl = value);
        Change(patch.CuisineId, recipe.CuisineId, value => recipe.CuisineId = value);
        Change(patch.CourseId, recipe.CourseId, value => recipe.CourseId = value);
        Change(patch.PrimaryTechniqueId, recipe.PrimaryTechniqueId, value => recipe.PrimaryTechniqueId = value);
        Change(patch.PrepTimeMinutes, recipe.PrepTimeMinutes, value => recipe.PrepTimeMinutes = value);
        Change(patch.CookTimeMinutes, recipe.CookTimeMinutes, value => recipe.CookTimeMinutes = value);
        Change(patch.RestTimeMinutes, recipe.RestTimeMinutes, value => recipe.RestTimeMinutes = value);
        Change(patch.TotalTimeMinutes, recipe.TotalTimeMinutes, value => recipe.TotalTimeMinutes = value);
        Change(patch.YieldText, recipe.YieldText, value => recipe.YieldText = value);
        Change(patch.YieldQuantity, recipe.YieldQuantity, value => recipe.YieldQuantity = value);
        Change(patch.YieldUnitId, recipe.YieldUnitId, value =>
        {
            recipe.YieldUnitId = value;

            // Derived, never accepted from the request: a client able to send this could contradict the unit
            // it names, and the composite foreign key would then be pinning a lie.
            recipe.YieldUnitDimension = yieldUnitDimension;
        });

        // Handled outside the generic helper because the request's type and the recipe's differ: a status
        // may be submitted as null, and the validation above has already refused that.
        if (patch.Status.IsSubmitted && patch.Status.Value is { } requestedStatus && requestedStatus != recipe.Status)
        {
            changes.Add(() => recipe.Status = requestedStatus);
        }

        var tags = patch.Tags.IsSubmitted ? patch.Tags.Value : null;
        var tagsChanged = tags is not null && !SameTags(loaded.Tags, tags);

        // Reconciled eagerly rather than collected into `changes` like the scalar fields: there is no single
        // "current value" to compare a submission against, only a graph to reconcile against, and the
        // reconciliation already returns whether it touched anything. A true no-op resubmission mutates
        // nothing here, so running it before the no-op check below is safe.
        var instructionsChanged = patch.Instructions.TryGetSubmitted(out var submittedInstructions)
            && ReconcileInstructions(recipe, submittedInstructions);

        if (changes.Count == 0 && !tagsChanged && !instructionsChanged)
        {
            // Nothing to record. Answering with the recipe as it stands is the honest reply — the creator
            // asked for a state it is already in — and writing a version for it would put a change with no
            // content into a history they read, while invalidating every token their collaborators hold.
            return OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
        }

        foreach (var change in changes)
        {
            change();
        }

        // One read of the clock for the whole edit, so the recipe and the version recording it agree on when
        // it happened. The actor is the resolved membership, never a request field.
        recipe.UpdatedAt = clock.UtcNow;
        recipe.UpdatedByMembershipId = workspace.MembershipId;

        var facts = new RecipeVersionFacts(
            RecipeVersionSource.CreatorEdit,

            // The version inherits the recipe's editorial state, exactly as version 1 does: an edit that
            // leaves it Ready captures a ready version, and one that does not, does not.
            recipe.Status == RecipeStatus.Ready ? RecipeVersionReadiness.Ready : RecipeVersionReadiness.Draft,
            patch.Reason);

        // Tags are handed down only when they are actually changing. Submitting the set a recipe already has
        // is not a request to rewrite its links.
        var outcome = await dataLayer.UpdateAsync(loaded, facts, tagsChanged ? tags : null, cancellationToken);

        return outcome.Version is null
            ? Conflict()
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(
                // A `with` expression, not a fresh construction: the write changed the aggregate, its
                // version and its tags, and everything else the read resolved — the duplication lineage
                // today — has to survive unchanged. A constructor call here would drop it silently, and
                // would drop the next such member too.
                loaded with
                {
                    Recipe = new CompleteRecipe(recipe, outcome.Version),
                    Tags = outcome.Tags,
                }));

        void Change<T>(PatchField<T> field, T current, Action<T> apply)
        {
            if (field.IsSubmitted && !EqualityComparer<T>.Default.Equals(field.Value, current))
            {
                changes.Add(() => apply(field.Value));
            }
        }
    }

    public async Task<OperationResult<RecipeDetailServiceModel>> RestoreVersionAsync(
        Guid recipeId,
        int versionNumber,
        CanonicalRestoreRecipeVersion request,
        CancellationToken cancellationToken)
    {
        var unit = await dataLayer.GetForRestoreAsync(recipeId, versionNumber, cancellationToken);
        if (unit is null)
        {
            return NotFound<RecipeDetailServiceModel>();
        }

        var loaded = unit.Recipe;
        var recipe = loaded.Recipe.Recipe;

        // Before the token check, deliberately — see the interface remarks. A version this recipe does not
        // have is a mistake in the request, and no amount of re-reading will make it right.
        if (unit.Snapshot is null)
        {
            return OperationResult<RecipeDetailServiceModel>.Failure(OperationError.Validation(
                RecipeErrorCodes.VersionNotFound,
                "That version could not be restored.",
                [(VersionNumberField, $"This recipe has no version {versionNumber}.")]));
        }

        // Checked before anything is reconciled, so a refused restore is answered from the state it was
        // composed against rather than from a half-applied one. The database repeats this when the save runs;
        // this one is not redundant, because it is the only one that can produce a readable answer, and it
        // catches the far commoner case — a token that went stale minutes ago, not microseconds.
        if (!RecipeConcurrencyToken.Matches(request.ExpectedConcurrencyToken, recipe.RowVersion))
        {
            return Conflict();
        }

        // A restore rewrites the recipe's content, so an archived recipe refuses it for the same reason it
        // refuses an edit — REC-006's freeze covers every content write, not only the obvious one. The
        // creator's remedy is to bring the recipe back first, which the error code says.
        if (!RecipePolicy.AcceptsContentChanges(recipe.Status))
        {
            return ArchivedConflict();
        }

        // Deserialized here rather than in the repository, for the reason CompareVersionsAsync gives: reading
        // stored text as a document is translation, and what an unreadable one means is a domain decision.
        var document = RecipeSnapshotSerializer.Deserialize(unit.Snapshot.Document);

        // Read before reconciling and unconditionally when the archive names any tag, because the reconciler
        // must be told which of those tags still exist — a link to a vocabulary row that is gone is an insert
        // the Restrict foreign key refuses. Going through the DataLayer, not a repository: Business calls one
        // layer.
        var tagIds = document.Tags.Select(tag => tag.WorkspaceTagId).ToList();
        IReadOnlyList<WorkspaceTag> vocabulary = tagIds.Count == 0
            ? []
            : await dataLayer.FindWorkspaceTagsAsync(tagIds, cancellationToken);

        // Reconciled eagerly rather than compared first, exactly as an edit's instructions are: there is no
        // single "current value" to test a document against, only a graph to reconcile with, and the
        // reconciliation already reports whether it touched anything. A restore onto content that already
        // matches mutates nothing, which is what makes asking afterwards safe.
        if (!RecipeSnapshotReconciler.Apply(recipe, document, vocabulary))
        {
            // Nothing to record. The creator asked for a state the recipe is already in, and writing a
            // version for it would put a change with no content into a history they read while invalidating
            // every token their collaborators hold.
            return OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
        }

        // One read of the clock for the whole restore, so the recipe and the version recording it agree on
        // when it happened. The actor is the resolved membership: a restore is attributed to whoever performed
        // it, never to the author of the version whose content came back.
        recipe.UpdatedAt = clock.UtcNow;
        recipe.UpdatedByMembershipId = workspace.MembershipId;

        var facts = new RecipeVersionFacts(
            RecipeVersionSource.Restore,

            // Derived from the status the restore just put back, by the same rule an edit follows. Restoring a
            // version that was Ready produces a ready version, because the recipe now says what that version
            // said — including its status.
            recipe.Status == RecipeStatus.Ready ? RecipeVersionReadiness.Ready : RecipeVersionReadiness.Draft,
            request.Reason,

            // The version the content came from. The one the restore replaces is the parent, and the
            // DataLayer derives that from the aggregate it already holds.
            unit.Snapshot.Id);

        var outcome = await dataLayer.RestoreAsync(loaded, facts, vocabulary, cancellationToken);

        return outcome.Version is null
            ? Conflict()
            : OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(
                // A `with` expression, not a fresh construction: the write changed the aggregate, its
                // version and its tags, and everything else the read resolved — the duplication lineage
                // today — has to survive unchanged. A constructor call here would drop it silently, and
                // would drop the next such member too.
                loaded with
                {
                    Recipe = new CompleteRecipe(recipe, outcome.Version),
                    Tags = outcome.Tags,
                }));
    }

    public async Task<OperationResult<CreatedRecipeServiceModel>> DuplicateAsync(
        Guid recipeId,
        CanonicalDuplicateRecipe request,
        CancellationToken cancellationToken)
    {
        var (visible, source) = await dataLayer.FindDuplicateSourceAsync(
            recipeId, request.SourceVersionNumber, cancellationToken);

        if (!visible)
        {
            return NotFound<CreatedRecipeServiceModel>();
        }

        if (source is null)
        {
            // Two shapes of the same code, because only one of them is the caller's mistake. A number they
            // sent names the field they sent it in; a recipe with no archived version at all — unreachable
            // through this API, since every recipe it writes has version 1 — is nothing they can correct, so
            // naming a field would tell them to fix a request that was right.
            return OperationResult<CreatedRecipeServiceModel>.Failure(
                request.SourceVersionNumber is { } number
                    ? OperationError.Validation(
                        RecipeErrorCodes.VersionNotFound,
                        CannotDuplicate,
                        [(SourceVersionNumberField, $"This recipe has no version {number}.")])
                    : new OperationError(
                        RecipeErrorCodes.VersionNotFound,
                        "That recipe has no version to copy.",
                        new Dictionary<string, string[]>()));
        }

        // Deserialized here rather than in the repository, for the reason CompareVersionsAsync gives: reading
        // stored text as a document is translation, and deciding what an unreadable one means is a domain
        // decision. An unreadable document raises, which is a 500 and the truthful status — the only way here
        // is a document written by a newer build than the one serving the request.
        var document = RecipeSnapshotSerializer.Deserialize(source.Document);

        // One read of the clock for the whole operation, so the recipe, its version and any tag born with it
        // all agree on when this happened.
        var now = clock.UtcNow;

        // Fresh identities throughout, and the archived content underneath. WorkspaceId is left unset on
        // every entity: the ownership interceptor stamps it, and Business assigning it is the defect
        // tenancy.md names.
        var copy = RecipeSnapshotDuplicator.Duplicate(document, Guid.NewGuid());

        // What the copy does not inherit, in one place. Everything else came from the archive.
        copy.Title = request.Title;
        copy.Status = RecipeStatus.Draft;
        copy.DuplicatedFromVersionId = source.Id;

        // The authenticated creator making the copy — never the author of the recipe it came from.
        copy.CreatedByMembershipId = workspace.MembershipId;
        copy.UpdatedByMembershipId = workspace.MembershipId;
        copy.CreatedAt = now;
        copy.UpdatedAt = now;

        // Resolved back to names so the create's own tag path can reuse the existing vocabulary rows. Ids
        // whose rows are gone resolve to nothing and are not applied — see RecipeSnapshotDuplicator for why
        // the links are not built from the document directly.
        var tagIds = document.Tags.Select(tag => tag.WorkspaceTagId).ToList();
        IReadOnlyList<WorkspaceTag> vocabulary = tagIds.Count == 0
            ? []
            : await dataLayer.FindWorkspaceTagsAsync(tagIds, cancellationToken);

        var facts = new RecipeVersionFacts(
            RecipeVersionSource.Duplicate,

            // Draft, not derived from the status like a create's or an edit's: the status above is forced, so
            // a conditional here would be a branch that can never take its other path.
            RecipeVersionReadiness.Draft,

            // No reason. A copy's first version is explained by its source and its lineage, and inventing a
            // sentence would put user-visible English in the domain that nothing can localize — the argument
            // CreateAsync makes for version 1.
            Reason: null);

        var created = await dataLayer.CreateAsync(
            copy,
            facts,
            [.. vocabulary.Select(tag => new RecipeTagName(tag.Name, tag.NormalizedName))],
            cancellationToken);

        return OperationResult<CreatedRecipeServiceModel>.Success(new CreatedRecipeServiceModel(
            created.RecipeId,
            copy.Title,
            copy.Status,
            created.VersionId,
            created.VersionNumber,
            copy.CreatedAt));
    }

    private const string CannotDuplicate = "That recipe cannot be copied as described.";

    public Task<OperationResult<RecipeDetailServiceModel>> ArchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken) =>
        SetStatusAsync(
            recipeId,
            // Anything that is not already shelved is shelved. Expressed as "which recipes this moves"
            // rather than "which state this leaves", because the two commands are not mirror images: see
            // UnarchiveAsync.
            current => current != RecipeStatus.Archived,
            RecipeStatus.Archived,
            RecipeAuditActions.Archived,
            "Archived the recipe.",
            actorUserId,
            expectedConcurrencyToken,
            cancellationToken);

    public Task<OperationResult<RecipeDetailServiceModel>> UnarchiveAsync(
        Guid recipeId,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken) =>
        SetStatusAsync(
            recipeId,
            // Only an archived recipe moves. "Already in the target state" would be the wrong test here and
            // actively harmful: the target is Draft, so it would leave a Ready recipe alone — and quietly
            // demote every other one this command was never meant to touch.
            current => current == RecipeStatus.Archived,
            RecipePolicy.UnarchivedStatus,
            RecipeAuditActions.Unarchived,
            "Brought the recipe back from the archive.",
            actorUserId,
            expectedConcurrencyToken,
            cancellationToken);

    /// <summary>
    /// The one implementation behind both lifecycle commands: load, check, move, record, save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two public methods over one private body rather than one method taking the target state, because the
    /// target is not a caller's choice — the route decides it, and a parameter would invite a future caller
    /// to move a recipe somewhere neither command means. What genuinely differs between the two is three
    /// values, and they are passed.
    /// </para>
    /// <para>
    /// <strong>No <c>AcceptsContentChanges</c> check here</strong>, and that is not an oversight: this
    /// changes the state itself rather than the content, so gating it on the state would make an archived
    /// recipe impossible to unarchive.
    /// </para>
    /// </remarks>
    /// <param name="moves">
    /// Whether a recipe in this state is one the command acts on. Supplied rather than derived from
    /// <paramref name="status"/>, because "already in the target state" is the right test for archiving and
    /// the wrong one for its opposite — unarchiving targets Draft, and a Ready recipe is not one it should
    /// touch.
    /// </param>
    private async Task<OperationResult<RecipeDetailServiceModel>> SetStatusAsync(
        Guid recipeId,
        Func<RecipeStatus, bool> moves,
        RecipeStatus status,
        string auditAction,
        string auditSummary,
        string actorUserId,
        string? expectedConcurrencyToken,
        CancellationToken cancellationToken)
    {
        var loaded = await dataLayer.GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return NotFound<RecipeDetailServiceModel>();
        }

        var recipe = loaded.Recipe.Recipe;

        // Before anything is touched, so a refused command is answered from the state it was composed
        // against. Checked even though the state may already be the one asked for: a creator quoting a stale
        // token has not seen what the recipe looks like now, and telling them "already archived" would hide
        // a collaborator's edit from them.
        if (!RecipeConcurrencyToken.Matches(expectedConcurrencyToken, recipe.RowVersion))
        {
            return Conflict();
        }

        if (!moves(recipe.Status))
        {
            // Nothing for this command to do. The honest answer is the recipe, and writing an audit entry
            // for a transition that did not happen would put a lie in the one log that has to be
            // trustworthy.
            return OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded));
        }

        var from = recipe.Status;

        // One read of the clock, and the actor from the resolved membership — never a request field. The
        // audit entry's own timestamp comes from the same clock inside the writer.
        recipe.Status = status;
        recipe.UpdatedAt = clock.UtcNow;
        recipe.UpdatedByMembershipId = workspace.MembershipId;

        var committed = await dataLayer.TrySetStatusAsync(
            loaded,
            new AuditEntry(
                actorUserId,
                auditAction,
                RecipeAuditActions.ResourceType,
                recipeId.ToString("D"),
                CorrelationId(),
                auditSummary,

                // State names, not content. AuditLog requires these to stay safe to display, and "which
                // editorial state" is exactly the kind of pointer they are for — no title, no creator text.
                BeforeReference: from.ToString(),
                AfterReference: status.ToString()),
            cancellationToken);

        return committed
            ? OperationResult<RecipeDetailServiceModel>.Success(RecipeDetailMapper.ToDetail(loaded))
            : Conflict();
    }

    /// <summary>
    /// The identifier tying an audit entry to the request that produced it.
    /// </summary>
    /// <remarks>
    /// Taken from the ambient <see cref="System.Diagnostics.Activity"/>, which is the correlation the
    /// gateway already starts and ServiceDefaults already propagates through every hop — so an audit row can
    /// be joined to the traces and logs of the request that wrote it without a fourth parameter threaded
    /// through four layers to say the same thing. A new id when there is no activity, so an entry written
    /// outside a traced request is still correlatable with itself rather than carrying <c>Guid.Empty</c>
    /// alongside every other such entry.
    /// </remarks>
    private static Guid CorrelationId()
    {
        // A W3C trace id is sixteen bytes, the same width as a Guid, so the two are the same value in two
        // spellings rather than a hash of one into the other — an operator can paste the audit row's
        // correlation id into a trace search and find the request.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default
            ? Guid.ParseExact(id.ToHexString(), "N")
            : Guid.NewGuid();
    }

    /// <summary>
    /// The body field a duplicate's source version arrives in, named in a refusal so a creator is told which
    /// part of their request was wrong.
    /// </summary>
    /// <remarks>
    /// The JSON name rather than <c>nameof</c>, matching how the validator's refusals reach the wire.
    /// <c>RecipeDuplicateEndpointTests</c> asserts the two agree.
    /// </remarks>
    private const string SourceVersionNumberField = "sourceVersionNumber";

    /// <summary>
    /// The route segment a restore's version number arrives on, named in a refusal so a creator is told which
    /// part of the URL was wrong.
    /// </summary>
    /// <remarks>
    /// A literal rather than <c>nameof</c> over a controller parameter, because the domain may not reference
    /// the API project. <c>RecipeRestoreEndpointTests</c> asserts the two agree.
    /// </remarks>
    private const string VersionNumberField = "versionNumber";

    /// <summary>
    /// Whether the submitted set names exactly the tags the recipe already carries.
    /// </summary>
    /// <remarks>
    /// Compared by normalized name, because that is what tag identity is: re-submitting "Weeknight" for a
    /// recipe tagged "weeknight" is not a change, and treating it as one would write a version whose only
    /// difference is capitalisation the vocabulary does not store.
    /// </remarks>
    private static bool SameTags(IReadOnlyList<WorkspaceTag> current, IReadOnlyList<RecipeTagName> submitted)
    {
        var held = current.Select(tag => tag.NormalizedName).ToHashSet(StringComparer.Ordinal);

        return held.Count == submitted.Count
            && submitted.All(tag => held.Contains(tag.NormalizedName));
    }

    /// <remarks>
    /// <see cref="RecipeInstructionGroup.WorkspaceId"/> is set here explicitly, from <paramref name="recipe"/>
    /// — not left for <c>WorkspaceOwnershipInterceptor</c> to stamp the way a scalar field would be. That
    /// interceptor runs at <c>SavingChanges</c>, after EF's own relationship fixup has already tried to
    /// propagate a value onto this new entity for adding it under an already-loaded parent (the update path,
    /// where <paramref name="recipe"/> is a real tracked row with a real <c>WorkspaceId</c>) — and
    /// <c>WorkspaceId</c> is part of this entity's own alternate key (<c>(WorkspaceId, RecipeId, Id)</c>, the
    /// principal key its steps' foreign key points at), which EF refuses to set via fixup on a still-forming
    /// entity. Setting it up front avoids the conflict; on a create, where <paramref name="recipe"/> has not
    /// been stamped yet either, this simply carries the same not-yet-set value the interceptor would apply
    /// moments later.
    /// </remarks>
    private static RecipeInstructionGroup BuildInstructionGroup(Recipe recipe, CanonicalInstructionGroup source, int sortOrder)
    {
        var group = new RecipeInstructionGroup
        {
            Id = Guid.NewGuid(),
            WorkspaceId = recipe.WorkspaceId,
            RecipeId = recipe.Id,
            Title = source.Title,
            SortOrder = sortOrder,
        };

        for (var index = 0; index < source.Steps.Count; index++)
        {
            group.Steps.Add(BuildInstructionStep(recipe, group.Id, source.Steps[index], index));
        }

        return group;
    }

    /// <inheritdoc cref="BuildInstructionGroup" path="//remarks"/>
    private static RecipeInstructionStep BuildInstructionStep(Recipe recipe, Guid groupId, CanonicalInstructionStep source, int sortOrder)
    {
        var step = new RecipeInstructionStep
        {
            Id = Guid.NewGuid(),
            WorkspaceId = recipe.WorkspaceId,
            RecipeId = recipe.Id,
            RecipeInstructionGroupId = groupId,
        };
        ApplyStep(step, source, sortOrder);

        return step;
    }

    /// <summary>
    /// Makes <paramref name="recipe"/>'s method match <paramref name="submitted"/> exactly: a group named by
    /// id is updated and reordered in place, one submitted without an id is staged as new, and one the
    /// recipe currently has but the submission does not name is removed.
    /// </summary>
    /// <returns>Whether anything about the method actually changed.</returns>
    /// <remarks>
    /// An id the submission names that does not belong to this recipe is not distinguished from no id at
    /// all: it is silently treated as a new group. Refusing it would first have to decide whether the id is
    /// simply unknown or names a row in a different workspace, and answering that at all is the disclosure
    /// tenancy.md forbids. Treating both alike costs nothing — the result is identical either way.
    /// </remarks>
    private static bool ReconcileInstructions(Recipe recipe, IReadOnlyList<CanonicalInstructionGroup> submitted)
    {
        var changed = false;
        var existing = recipe.InstructionGroups.ToDictionary(group => group.Id);
        var kept = new HashSet<Guid>();

        for (var index = 0; index < submitted.Count; index++)
        {
            var source = submitted[index];
            RecipeInstructionGroup group;

            if (source.Id is { } groupId && existing.TryGetValue(groupId, out var found))
            {
                group = found;
                kept.Add(groupId);

                if (group.Title != source.Title)
                {
                    group.Title = source.Title;
                    changed = true;
                }

                if (group.SortOrder != index)
                {
                    group.SortOrder = index;
                    changed = true;
                }
            }
            else
            {
                group = new RecipeInstructionGroup
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = recipe.WorkspaceId, // see BuildInstructionGroup's remarks
                    RecipeId = recipe.Id,
                    Title = source.Title,
                    SortOrder = index,
                };
                recipe.InstructionGroups.Add(group);
                changed = true;
            }

            if (ReconcileSteps(recipe, group, source.Steps))
            {
                changed = true;
            }
        }

        foreach (var orphan in existing.Values.Where(group => !kept.Contains(group.Id)).ToList())
        {
            recipe.InstructionGroups.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <inheritdoc cref="ReconcileInstructions"/>
    private static bool ReconcileSteps(Recipe recipe, RecipeInstructionGroup group, IReadOnlyList<CanonicalInstructionStep> submitted)
    {
        var changed = false;
        var existing = group.Steps.ToDictionary(step => step.Id);
        var kept = new HashSet<Guid>();

        for (var index = 0; index < submitted.Count; index++)
        {
            var source = submitted[index];

            if (source.Id is { } stepId && existing.TryGetValue(stepId, out var found))
            {
                kept.Add(stepId);
                changed |= ApplyStep(found, source, index);
            }
            else
            {
                group.Steps.Add(BuildInstructionStep(recipe, group.Id, source, index));
                changed = true;
            }
        }

        foreach (var orphan in existing.Values.Where(step => !kept.Contains(step.Id)).ToList())
        {
            group.Steps.Remove(orphan);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies <paramref name="source"/> and <paramref name="sortOrder"/> onto <paramref name="step"/> in
    /// place, deriving <see cref="RecipeInstructionStep.TemperatureUnitDimension"/> rather than trusting it
    /// from the request — exactly as <see cref="Recipe.YieldUnitDimension"/> is derived on the recipe
    /// itself, and safe to hardcode here: the facade's reference check already refused any submitted
    /// <see cref="CanonicalInstructionStep.TemperatureUnitId"/> whose unit is not
    /// <see cref="MeasurementDimension.Temperature"/>, unlike a yield unit, where more than one dimension is
    /// acceptable and Business genuinely needs the resolved value.
    /// </summary>
    /// <returns>Whether anything about the step actually changed.</returns>
    private static bool ApplyStep(RecipeInstructionStep step, CanonicalInstructionStep source, int sortOrder)
    {
        var dimension = source.TemperatureUnitId is null ? (MeasurementDimension?)null : MeasurementDimension.Temperature;

        var changed = step.SortOrder != sortOrder
            || step.Text != source.Text
            || step.TechniqueId != source.TechniqueId
            || step.DurationMinutes != source.DurationMinutes
            || step.TemperatureValue != source.TemperatureValue
            || step.TemperatureUnitId != source.TemperatureUnitId
            || step.TemperatureUnitDimension != dimension
            || step.Note != source.Note;

        if (!changed)
        {
            return false;
        }

        step.SortOrder = sortOrder;
        step.Text = source.Text;
        step.TechniqueId = source.TechniqueId;
        step.DurationMinutes = source.DurationMinutes;
        step.TemperatureValue = source.TemperatureValue;
        step.TemperatureUnitId = source.TemperatureUnitId;
        step.TemperatureUnitDimension = dimension;
        step.Note = source.Note;

        return true;
    }

    /// <summary>
    /// The one answer for a recipe that does not exist and for one belonging to another workspace.
    /// </summary>
    /// <remarks>
    /// Generic over what the caller was reading, so that every read of a recipe — the recipe itself, its
    /// history, and whatever follows — refuses in the same words under the same code. Two spellings of "that
    /// recipe could not be found" would eventually become two ways to tell those two cases apart.
    /// </remarks>
    private static OperationResult<T> NotFound<T>() =>
        OperationResult<T>.Failure(new OperationError(
            RecipeErrorCodes.RecipeNotFound,
            "That recipe could not be found.",
            new Dictionary<string, string[]>()));

    private static OperationResult<RecipeDetailServiceModel> Conflict() =>
        OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
            RecipeErrorCodes.RecipeConflict,
            "This recipe has changed since you opened it. Reload it and make your edit again.",
            new Dictionary<string, string[]>()));

    /// <summary>
    /// The refusal an archived recipe gives every write that would change its content.
    /// </summary>
    /// <remarks>
    /// Its own message as well as its own code, because the two conflicts want opposite things from the
    /// reader: <see cref="Conflict"/> says reload and retry, and this says the retry will not work until the
    /// recipe is brought back. See <see cref="RecipeErrorCodes.RecipeArchivedConflict"/>.
    /// </remarks>
    private static OperationResult<RecipeDetailServiceModel> ArchivedConflict() =>
        OperationResult<RecipeDetailServiceModel>.Failure(new OperationError(
            RecipeErrorCodes.RecipeArchivedConflict,
            "This recipe is archived. Bring it back from the archive before changing it.",
            new Dictionary<string, string[]>()));

    /// <summary>
    /// The invariants an edit must satisfy, judged against the recipe it would produce.
    /// </summary>
    /// <remarks>
    /// Every check reads a merged value rather than a submitted one, because a patch names only part of the
    /// recipe: clearing the yield quantity is wrong only because of a unit the request never mentioned, and
    /// a total time that was coherent an hour ago stops being so when the cook time grows.
    /// </remarks>
    private static OperationError? Validate(
        CanonicalRecipePatch patch,
        Recipe recipe,
        Guid? yieldUnitId,
        MeasurementDimension? yieldUnitDimension)
    {
        var errors = new List<(string Field, string Error)>();

        // Defence, not duplication: the validator already refuses both of these, and Business is the last
        // thing standing between them and the aggregate if some other caller — a worker, an AI plugin, a
        // future facade overload — ever builds a patch without it.
        if (patch.Title.IsSubmitted && string.IsNullOrEmpty(patch.Title.Value))
        {
            errors.Add((nameof(UpdateRecipeViewModel.Title), "A recipe must keep a title."));
        }

        // Left unchecked, a cleared status would be silently read as Draft further down, and an archived or
        // finished recipe would quietly reappear in the working set as an ordinary-looking creator edit.
        if (patch.Status.IsSubmitted && patch.Status.Value is null)
        {
            errors.Add((nameof(UpdateRecipeViewModel.Status), "A recipe must keep an editorial state."));
        }

        // A total below the longest single phase is incoherent under any amount of overlap. Deliberately not
        // "total equals the sum": prep overlaps cooking and resting is unattended, so a total legitimately
        // runs less than the sum, and a creator who writes "about 2 hours, mostly waiting" means it.
        var longestPhase = new[]
            {
                patch.PrepTimeMinutes.Or(recipe.PrepTimeMinutes),
                patch.CookTimeMinutes.Or(recipe.CookTimeMinutes),
                patch.RestTimeMinutes.Or(recipe.RestTimeMinutes),
            }
            .Where(minutes => minutes.HasValue)
            .Select(minutes => minutes!.Value)
            .DefaultIfEmpty(0)
            .Max();

        if (patch.TotalTimeMinutes.Or(recipe.TotalTimeMinutes) is { } total && total < longestPhase)
        {
            errors.Add((
                nameof(UpdateRecipeViewModel.TotalTimeMinutes),
                $"A total time of {total} minutes is less than the longest single step, which takes {longestPhase}."));
        }

        // Mirrors CK_Recipes_YieldUnit_RequiresQuantity, and unlike on a create it cannot be asked at the
        // edge: either half of the pair may be the half that is not being submitted.
        if (yieldUnitId is not null && patch.YieldQuantity.Or(recipe.YieldQuantity) is null)
        {
            errors.Add((nameof(UpdateRecipeViewModel.YieldQuantity), "Give the yield a number as well as a unit."));
        }

        if (yieldUnitId is not null && yieldUnitDimension == MeasurementDimension.Temperature)
        {
            errors.Add((nameof(UpdateRecipeViewModel.YieldUnitId), "A yield cannot be measured in degrees."));
        }

        return errors.Count == 0
            ? null
            : OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                "That recipe cannot be changed as described.",
                errors);
    }

    /// <summary>
    /// The invariants a shape validator structurally cannot check.
    /// </summary>
    private static OperationError? Validate(CanonicalCreateRecipe input, MeasurementDimension? yieldUnitDimension)
    {
        var errors = new List<(string Field, string Error)>();

        // A total below the longest single phase is incoherent under any amount of overlap. Deliberately not
        // "total equals the sum": prep overlaps cooking and resting is unattended, so a total legitimately
        // runs less than the sum, and a creator who writes "about 2 hours, mostly waiting" means it.
        var longestPhase = new[] { input.PrepTimeMinutes, input.CookTimeMinutes, input.RestTimeMinutes }
            .Where(minutes => minutes.HasValue)
            .Select(minutes => minutes!.Value)
            .DefaultIfEmpty(0)
            .Max();

        if (input.TotalTimeMinutes is { } total && total < longestPhase)
        {
            errors.Add((
                nameof(CreateRecipeViewModel.TotalTimeMinutes),
                $"A total time of {total} minutes is less than the longest single step, which takes {longestPhase}."));
        }

        if (input.YieldUnitId is not null && yieldUnitDimension == MeasurementDimension.Temperature)
        {
            errors.Add((nameof(CreateRecipeViewModel.YieldUnitId), "A yield cannot be measured in degrees."));
        }

        return errors.Count == 0
            ? null
            : OperationError.Validation(
                RecipeErrorCodes.RecipeInvalidRequest,
                "That recipe cannot be created as described.",
                errors);
    }
}
