using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// Composes complete persistence operations for recipes and owns their transaction boundaries. Makes no
/// product decisions: what a valid title is, what a version's reason should say, and whether a creator may
/// do this at all are all settled before anything here is called.
/// </summary>
public interface IRecipeDataLayer
{
    /// <summary>
    /// Persists a new recipe, every one of its children, and version 1 with its snapshot — atomically.
    /// </summary>
    /// <param name="recipe">
    /// A complete aggregate built by Business, with <c>WorkspaceId</c> left unset on every entity: the
    /// ownership interceptor stamps it from the resolved context.
    /// </param>
    /// <param name="version">The editorial facts about version 1.</param>
    /// <param name="tags">
    /// The tags to apply, already normalized by Business. Existing vocabulary entries are reused and new ones
    /// are created — inside this same transaction, so a failed create cannot leave orphan tags behind in the
    /// workspace's vocabulary.
    /// </param>
    /// <remarks>
    /// Either all of it commits or none of it does. A failure part-way cannot leave a recipe without its
    /// first version, which would be a recipe whose history began after its content did.
    /// </remarks>
    Task<CreatedRecipe> CreateAsync(
        Recipe recipe,
        RecipeVersionFacts version,
        IReadOnlyCollection<RecipeTagName> tags,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one recipe whole — every child, its current version's metadata, and the vocabulary rows naming
    /// its tags.
    /// </summary>
    /// <returns>
    /// <c>null</c> when no such recipe is visible in the resolved workspace. Unknown and
    /// belonging-to-another-workspace are one answer, because the query filter cannot tell them apart and
    /// nothing above here should be able to either.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Three logical reads — the recipe graph, its current version, and the tag vocabulary — and deliberately
    /// no transaction around them. Two of those are composed here; the version read sits inside
    /// <see cref="IRecipeRepository.GetCompleteAsync"/>, which documents that it opens no boundary of its own.
    /// </para>
    /// <para>
    /// No transaction, because mutual consistency would buy nothing any caller can use. The tag read only
    /// names tags the first read already found, and the worst a concurrent edit can do there is drop a tag
    /// whose link had just been removed anyway. The version read can skew — a version written between the two
    /// statements would pair newer metadata with slightly older content — but the concurrency token comes from
    /// the recipe row, so an edit composed against this state is refused as stale rather than silently
    /// applied; the skew corrects itself on the next read and cannot cost a creator their work. An explicit
    /// transaction would also have to be wrapped in the retrying execution strategy's ceremony, which is real
    /// cost for no guarantee.
    /// </para>
    /// </remarks>
    Task<TaggedRecipe?> GetDetailAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the same unit as <see cref="GetDetailAsync"/>, with the aggregate <strong>tracked</strong>, for
    /// a caller that is about to change it.
    /// </summary>
    /// <returns><c>null</c> when no such recipe is visible, exactly as on the read path.</returns>
    /// <remarks>
    /// Opens no transaction, for the reasons <see cref="GetDetailAsync"/> gives — and here it costs nothing
    /// at all, because <see cref="UpdateAsync"/> quotes the token this read returned. An edit composed
    /// against state that has since moved on is refused, which is a stronger guarantee than holding a read
    /// lock across a creator's thinking time could ever be.
    /// </remarks>
    Task<TaggedRecipe?> GetForUpdateAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>
    /// Persists an edited recipe together with the one new version that records it — atomically, and only if
    /// the recipe is still in the state the edit was composed against.
    /// </summary>
    /// <param name="loaded">
    /// The unit <see cref="GetForUpdateAsync"/> returned, with its aggregate already changed by Business. The
    /// recipe's <c>RowVersion</c> still holds the value that read returned, which is what the update quotes.
    /// </param>
    /// <param name="version">The editorial facts about the version this edit writes.</param>
    /// <param name="tags">
    /// The complete set of tags the recipe should end up with, or <c>null</c> to leave its tags exactly as
    /// they are. A submitted set replaces: links it does not name are removed, and names the workspace does
    /// not yet know become new vocabulary rows in this same transaction.
    /// </param>
    /// <returns>
    /// The version that was written, or a conflicted outcome when the recipe had already moved on — in which
    /// case nothing was written at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The version number is allocated by the concurrency check, not by the read.</strong> Numbering
    /// from the version this caller read looks like a race — two editors could both see version 3 and both
    /// try to write 4. They cannot both succeed: every real change also stamps the recipe row, so each save
    /// issues an update quoting the token it read, and only one of those updates matches a row. The loser is
    /// a conflict, not a duplicate version number. This is also why a no-op must write nothing at all rather
    /// than write a version and leave the recipe row alone.
    /// </para>
    /// <para>
    /// Historical versions are never touched: this stages an insert and nothing else. <c>RecipeVersion</c> is
    /// <see cref="IImmutableRecord"/>, so even a mistake here would be refused at <c>SaveChanges</c>.
    /// </para>
    /// </remarks>
    Task<RecipeUpdateOutcome> UpdateAsync(
        TaggedRecipe loaded,
        RecipeVersionFacts version,
        IReadOnlyCollection<RecipeTagName>? tags,
        CancellationToken cancellationToken);
}

internal sealed class RecipeDataLayer(
    CreatorPantryDbContext context,
    IRecipeRepository recipes,
    IRecipeVersionRepository versions,
    IWorkspaceTagRepository workspaceTags) : IRecipeDataLayer
{
    public async Task<CreatedRecipe> CreateAsync(
        Recipe recipe,
        RecipeVersionFacts version,
        IReadOnlyCollection<RecipeTagName> tags,
        CancellationToken cancellationToken)
    {
        // Before the snapshot is captured, so version 1 records the tags the recipe was created with rather
        // than an untagged version of it. Attach rather than replace: a recipe that does not exist yet has
        // nothing to remove, and the two operations differ only for one that does.
        await AttachTagsAsync(recipe, tags, recipe.CreatedAt, cancellationToken);

        var firstVersion = BuildVersion(
            new CompleteRecipe(recipe, null), version, recipe.CreatedByMembershipId, recipe.CreatedAt);

        recipes.Add(recipe);
        versions.Add(firstVersion);

        // The transaction boundary, and the whole of it. One SaveChangesAsync over every pending entity on
        // one DbContext is already one transaction — the rule IOutboxWriter states and WorkspaceRepository
        // relies on for the same shape of write. An explicit BeginTransactionAsync would buy nothing here and
        // would cost something real: EnrichSqlServerDbContext enables a retrying execution strategy, under
        // which EF refuses a user-initiated transaction unless it is wrapped in
        // CreateExecutionStrategy().ExecuteAsync — the ceremony IdempotencyDataLayer needs because it spans
        // several statements. This spans one.
        await context.SaveChangesAsync(cancellationToken);

        return new CreatedRecipe(recipe.Id, firstVersion.Id, firstVersion.VersionNumber);
    }

    public async Task<TaggedRecipe?> GetDetailAsync(Guid recipeId, CancellationToken cancellationToken) =>
        await WithTagsAsync(await recipes.GetCompleteAsync(recipeId, cancellationToken), cancellationToken);

    public async Task<TaggedRecipe?> GetForUpdateAsync(Guid recipeId, CancellationToken cancellationToken) =>
        await WithTagsAsync(await recipes.GetForUpdateAsync(recipeId, cancellationToken), cancellationToken);

    public async Task<RecipeUpdateOutcome> UpdateAsync(
        TaggedRecipe loaded,
        RecipeVersionFacts version,
        IReadOnlyCollection<RecipeTagName>? tags,
        CancellationToken cancellationToken)
    {
        var recipe = loaded.Recipe.Recipe;

        // Before the snapshot, so the version records the tags the recipe ends up with rather than the ones
        // it had a moment ago. A tag born here shares the edit's instant.
        var vocabulary = tags is null
            ? loaded.Tags
            : await ReplaceTagsAsync(recipe, tags, recipe.UpdatedAt, cancellationToken);

        // Captured before the save, because a successful save replaces it with the token the server just
        // generated — and this is the value the losing writer needs to recognise that it lost.
        var readWith = recipe.RowVersion;

        var next = BuildVersion(loaded.Recipe, version, recipe.UpdatedByMembershipId, recipe.UpdatedAt);
        versions.Add(next);

        try
        {
            // One save, one transaction — the create's argument applies unchanged. What is added here is the
            // WHERE clause EF puts on the recipe's UPDATE, quoting the RowVersion this aggregate was read
            // with: nothing commits unless the recipe is still in the state the edit was composed against.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Two guards on one race, and either may fire first.
            //
            // The intended one is the row version: the recipe's UPDATE matches no row and EF raises
            // DbUpdateConcurrencyException. But EF sends the version INSERT before that UPDATE, so when the
            // other writer has already committed their version, the unique index on
            // (WorkspaceId, RecipeId, VersionNumber) is what refuses this batch — an ordinary
            // DbUpdateException, describing the same event.
            //
            // Rather than read SQL Server error numbers to tell those apart, ask the question that actually
            // matters: has the row this edit was composed against moved on? If it has, this is a conflict
            // whichever guard caught it. If it has not, something else is wrong and must not be reported as
            // a collaborator's edit.
            if (exception is DbUpdateConcurrencyException
                || await HasMovedOnAsync(recipe.Id, readWith, cancellationToken))
            {
                // Nothing was written — and nothing may be left staged either. The aggregate is still
                // Modified and the new version still Added, so the next SaveChanges on this scope would
                // commit an edit the caller was told had been refused. Today that save happens only when an
                // idempotency key was sent, and its executor clears the tracker itself; with no key there is
                // no transaction and no cleanup at all. Clearing here makes "nothing was written" a property
                // of this method rather than of whoever happens to call it next.
                context.ChangeTracker.Clear();

                // Expected, not exceptional: two creators editing one recipe is the ordinary case. Translated
                // here because this is the layer that knows the save failed and why (backend.md), and because
                // an exception crossing into Business would make an ordinary editorial outcome look like a
                // fault.
                return RecipeUpdateOutcome.Conflict();
            }

            throw;
        }

        return RecipeUpdateOutcome.Applied(next, vocabulary);
    }

    /// <summary>
    /// Whether the recipe is no longer in the state <paramref name="readWith"/> names — because someone else
    /// saved, or because the recipe is gone.
    /// </summary>
    /// <remarks>
    /// Asked only after a save has already failed, to decide what kind of failure it was. A missing recipe
    /// counts as moved on: the edit cannot be applied and the caller's remedy is to go and look, which is
    /// what a conflict tells them to do.
    /// </remarks>
    private async Task<bool> HasMovedOnAsync(Guid recipeId, byte[] readWith, CancellationToken cancellationToken)
    {
        var current = await recipes.FindRowVersionAsync(recipeId, cancellationToken);

        return current is null || !current.AsSpan().SequenceEqual(readWith);
    }

    private async Task<TaggedRecipe?> WithTagsAsync(CompleteRecipe? loaded, CancellationToken cancellationToken)
    {
        if (loaded is null)
        {
            // Before the second read, not after: an invisible recipe must cost one query, and a tag lookup for
            // a recipe nobody may see would be work done on behalf of a caller who gets nothing.
            return null;
        }

        var tagIds = loaded.Recipe.Tags.Select(link => link.WorkspaceTagId).ToList();

        return new TaggedRecipe(loaded, await workspaceTags.FindByIdsAsync(tagIds, cancellationToken));
    }

    /// <summary>
    /// Makes the recipe's tag links name exactly <paramref name="tags"/>: attaches what is missing, then
    /// drops every link the set does not name.
    /// </summary>
    /// <remarks>
    /// Replacing rather than merging is what gives an edit a way to remove a tag at all — and dropping the
    /// last link to one leaves the vocabulary row in place, deliberately. <see cref="WorkspaceTag"/> is a
    /// root of its own, a creator's tag list is theirs to curate, and deleting it here would retire a tag
    /// from the whole workspace because one recipe stopped using it.
    /// </remarks>
    private async Task<IReadOnlyList<WorkspaceTag>> ReplaceTagsAsync(
        Recipe recipe,
        IReadOnlyCollection<RecipeTagName> tags,
        DateTimeOffset bornAt,
        CancellationToken cancellationToken)
    {
        var wanted = await AttachTagsAsync(recipe, tags, bornAt, cancellationToken);
        var wantedIds = wanted.Select(tag => tag.Id).ToHashSet();

        foreach (var link in recipe.Tags.Where(link => !wantedIds.Contains(link.WorkspaceTagId)).ToList())
        {
            recipe.Tags.Remove(link);
        }

        return wanted;
    }

    /// <summary>
    /// Links the recipe to each of <paramref name="tags"/> — reusing existing vocabulary rows and staging
    /// new ones — without disturbing any tag it already carries.
    /// </summary>
    /// <param name="bornAt">
    /// The instant a tag created here records as its own. Copied from the write that is happening rather than
    /// read from the clock again, so a tag born with a recipe shares its instant instead of disagreeing by
    /// microseconds.
    /// </param>
    /// <returns>The vocabulary rows named by <paramref name="tags"/>.</returns>
    /// <remarks>
    /// New tags are staged rather than saved, so they commit in the same batch as the recipe. Saving them
    /// first would mean a write that later failed still enlarged the creator's vocabulary with tags no recipe
    /// uses.
    /// </remarks>
    private async Task<IReadOnlyList<WorkspaceTag>> AttachTagsAsync(
        Recipe recipe,
        IReadOnlyCollection<RecipeTagName> tags,
        DateTimeOffset bornAt,
        CancellationToken cancellationToken)
    {
        var existing = tags.Count == 0
            ? new Dictionary<string, WorkspaceTag>(StringComparer.Ordinal)
            : (await workspaceTags.FindByNormalizedNamesAsync(
                    [.. tags.Select(tag => tag.NormalizedName)], cancellationToken))
                .ToDictionary(tag => tag.NormalizedName, StringComparer.Ordinal);

        var wanted = new List<WorkspaceTag>(tags.Count);

        foreach (var tag in tags)
        {
            if (!existing.TryGetValue(tag.NormalizedName, out var vocabulary))
            {
                vocabulary = new WorkspaceTag
                {
                    Id = Guid.NewGuid(),
                    Name = tag.Name,
                    NormalizedName = tag.NormalizedName,
                    CreatedAt = bornAt,
                };

                workspaceTags.Add(vocabulary);
                existing[tag.NormalizedName] = vocabulary;
            }

            wanted.Add(vocabulary);
        }

        var linked = recipe.Tags.Select(link => link.WorkspaceTagId).ToHashSet();

        foreach (var tag in wanted.Where(tag => !linked.Contains(tag.Id)))
        {
            recipe.Tags.Add(new RecipeTag { RecipeId = recipe.Id, WorkspaceTagId = tag.Id });
        }

        return wanted;
    }

    /// <summary>
    /// Builds the version that records the aggregate's current content.
    /// </summary>
    /// <param name="writtenBy">
    /// The membership that performed the write being recorded — the creator on a create, the editor on an
    /// edit. Passed in rather than read off the recipe so each caller names the write it is capturing.
    /// </param>
    /// <param name="writtenAt">The instant of that write, copied from the recipe's own stamp.</param>
    /// <remarks>
    /// <para>
    /// One builder for the first version and for every later one, because the difference between them is
    /// entirely in values derived from the aggregate itself. Version 1 has no current version to follow, so
    /// it numbers from <see cref="RecipePolicy.FirstVersionNumber"/> and has no parent; an edit numbers one
    /// past what it read and names that version as its parent.
    /// </para>
    /// <para>
    /// The recipe must already hold the content being captured. On a create it was constructed whole by
    /// Business a moment ago; on an edit it was read whole and changed in place. Capturing here rather than
    /// in Business is deliberate: it is a faithful copy rather than a judgement, and it has to happen inside
    /// the unit that commits, or a version could describe content that never landed.
    /// </para>
    /// </remarks>
    private static RecipeVersion BuildVersion(
        CompleteRecipe loaded,
        RecipeVersionFacts facts,
        Guid writtenBy,
        DateTimeOffset writtenAt)
    {
        var recipe = loaded.Recipe;
        var current = loaded.CurrentVersion;
        var document = RecipeSnapshotMapper.Capture(loaded);

        var version = new RecipeVersion
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe.Id,
            VersionNumber = current is null ? RecipePolicy.FirstVersionNumber : current.VersionNumber + 1,
            ParentVersionId = current?.Id,
            Source = facts.Source,
            Readiness = facts.Readiness,
            Reason = facts.Reason,

            // Copied from the recipe rather than read from a clock again, so the recipe and the version that
            // records it cannot disagree about when it happened or who did it.
            CreatedByMembershipId = writtenBy,
            CreatedAt = writtenAt,

            // The state this version was derived from: the token the edit was composed against, which is
            // knowable now — unlike the token the save is about to generate, which would need a second save
            // and an explicit transaction to quote. Absent on a create, because the row does not exist yet
            // and version 1 superseded nothing, so there is no prior state to cite.
            BasedOnRecipeRowVersion = recipe.RowVersion.Length == 0 ? null : recipe.RowVersion,

            SnapshotSchemaVersion = document.SchemaVersion,
        };

        version.Snapshot = new RecipeVersionSnapshot
        {
            RecipeVersionId = version.Id,
            Document = RecipeSnapshotSerializer.Serialize(document),
        };

        return version;
    }
}
