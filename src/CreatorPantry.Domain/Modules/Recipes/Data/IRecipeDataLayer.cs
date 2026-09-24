using CreatorPantry.Domain.Managers.Audit;
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

    /// <summary>
    /// Reads one page of recipe summaries, and the total matching the same filters when the criteria asks for it.
    /// </summary>
    /// <returns>
    /// The page's rows, whether another page follows, and the total — <c>null</c> when it was not asked for,
    /// which is different from zero and must stay so.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Two reads, and deliberately no transaction around them, for the reasons
    /// <see cref="GetDetailAsync"/> sets out. Here the case is even clearer: the count is documented as able to
    /// disagree with the rows under a concurrent write, so a boundary that made them consistent would be paying
    /// the retrying execution strategy's ceremony to guarantee something the contract explicitly does not
    /// promise.
    /// </para>
    /// <para>
    /// Whether to count is read off the criteria rather than decided here. That is the caller's request, not a
    /// product judgement, so it is no more a decision this layer is making than the page size is.
    /// </para>
    /// </remarks>
    Task<(IReadOnlyList<RecipeSummaryRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of a recipe's history, newest first — version metadata only, never a snapshot.
    /// </summary>
    /// <returns>
    /// The page's rows and whether another follows, or <c>null</c> when no such recipe is visible in the
    /// resolved workspace.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Two reads, and the first is the reason there are two.</strong> Every recipe has at least one
    /// version, so an empty history would say "this recipe is not here" as plainly as a 404 would — and it
    /// would say it about another workspace's recipe too. Probing existence first is what lets both answer
    /// alike. The probe reads one column of one row; it is not a second load of the aggregate.
    /// </para>
    /// <para>
    /// <strong><c>null</c> rather than an empty page</strong>, because those are different facts and only the
    /// caller can decide what to do with each. A recipe with no visible versions is not representable today,
    /// but if it ever were — a platform maintenance path, an erasure — it is an empty history, not a missing
    /// recipe.
    /// </para>
    /// <para>
    /// No transaction, for the reasons <see cref="GetDetailAsync"/> sets out, and here the case is simpler
    /// still: history rows are immutable, so the page cannot change under the probe. The only skew available
    /// is a version written between the two statements, which appears at the top of the list where it belongs.
    /// </para>
    /// </remarks>
    Task<(IReadOnlyList<RecipeVersionHistoryRecord> Rows, bool HasMore)?> ListVersionsAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the archived content of two of one recipe's versions, for a comparison.
    /// </summary>
    /// <returns>
    /// The rows found — none, one, or two — or <c>null</c> when the recipe itself is not visible here.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong><c>null</c> rather than an empty list</strong>, for the reason <see cref="ListVersionsAsync"/>
    /// gives: "you may not see this recipe" and "this recipe has no version 9" are different facts with
    /// different answers, and only the caller can decide what to say about each. Collapsing them here would
    /// make a creator who mistyped a version number be told their recipe does not exist.
    /// </para>
    /// <para>
    /// <strong>The visibility probe runs first and separately</strong>, exactly as on the history route. The
    /// snapshot query would return nothing for an invisible recipe too — the query filter sees to that — but
    /// nothing is also what a readable recipe with no matching versions returns, and the two must not become
    /// the same answer by accident.
    /// </para>
    /// <para>
    /// <strong>No transaction.</strong> Versions and their snapshots are immutable, so neither statement can
    /// see the other's rows change; the only skew available is a version written between them, which this
    /// query was not asked about.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<RecipeVersionSnapshotRecord>?> FindVersionSnapshotsAsync(
        Guid recipeId,
        int firstVersionNumber,
        int secondVersionNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads what a restore operates on: the recipe tracked and whole, and the archived content of the one
    /// version being restored.
    /// </summary>
    /// <returns>
    /// The unit, or <c>null</c> when no such recipe is visible. See <see cref="RecipeRestoreUnit"/> for why a
    /// missing recipe and a missing version are two different answers rather than one.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Two reads, and no existence probe.</strong> <see cref="ListVersionsAsync"/> and
    /// <see cref="FindVersionSnapshotsAsync"/> both run one first, because their own queries cannot tell an
    /// invisible recipe from a readable one with nothing to return. This has the aggregate in hand, so the
    /// distinction is free.
    /// </para>
    /// <para>
    /// <strong>No transaction</strong>, for the reasons <see cref="GetForUpdateAsync"/> gives, and the
    /// ordering makes it moot: the snapshot is an immutable row, so it cannot change under the aggregate, and
    /// the aggregate's own token is what the save quotes. A version written between the two statements is not
    /// this one.
    /// </para>
    /// </remarks>
    Task<RecipeRestoreUnit?> GetForRestoreAsync(
        Guid recipeId,
        int versionNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the workspace's tag vocabulary rows for the ids given — those that exist here.
    /// </summary>
    /// <returns>
    /// Only the ids that exist in the resolved workspace, in no particular order. Fewer than asked for is a
    /// normal answer.
    /// </returns>
    /// <remarks>
    /// For a restore, which finds its tags in an archive as ids rather than as the names an edit submits, and
    /// so cannot go through the create-or-reuse path that <see cref="UpdateAsync"/> uses. Reading them first
    /// is what lets an id whose vocabulary row is gone be dropped instead of sent to a <c>Restrict</c>
    /// foreign key to fail — see <c>RecipeSnapshotReconciler</c>, which is handed this answer.
    /// </remarks>
    Task<IReadOnlyList<WorkspaceTag>> FindWorkspaceTagsAsync(
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a restored recipe together with the one new version that records it — atomically, and only if
    /// the recipe is still in the state the restore was composed against.
    /// </summary>
    /// <param name="loaded">
    /// The unit <see cref="GetForRestoreAsync"/> returned, with its aggregate already reconciled to the
    /// archived content by Business.
    /// </param>
    /// <param name="version">
    /// The editorial facts about the version this restore writes, including which version it was restored
    /// from.
    /// </param>
    /// <param name="tags">
    /// The vocabulary rows the recipe carries after the restore, for naming its tags in the answer. The links
    /// themselves were reconciled by Business; nothing here changes them.
    /// </param>
    /// <returns>
    /// The version that was written, or a conflicted outcome when the recipe had already moved on — in which
    /// case nothing was written at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Shares its whole implementation with <see cref="UpdateAsync"/>, and everything that method's remarks
    /// say about numbering, conflicts and immutable history is true here word for word. A separate method
    /// rather than a flag on that one, because the two differ in what the caller has already done: an edit
    /// hands down tag <em>names</em> for this layer to create or reuse, a restore hands down rows it has
    /// already resolved.
    /// </para>
    /// <para>
    /// <strong>The restored version's own row is not touched, and cannot be.</strong> This stages one insert.
    /// <c>RecipeVersion</c> is <see cref="IImmutableRecord"/>, so nothing on any code path can mark an old
    /// version current, undelete one, or renumber one — the save refuses it.
    /// </para>
    /// </remarks>
    Task<RecipeUpdateOutcome> RestoreAsync(
        TaggedRecipe loaded,
        RecipeVersionFacts version,
        IReadOnlyList<WorkspaceTag> tags,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the archived content a duplicate will copy: one named version of a recipe, or its current one.
    /// </summary>
    /// <param name="recipeId">The recipe to copy from, resolved from the route.</param>
    /// <param name="versionNumber">
    /// Which version, or <c>null</c> for the recipe's most recent — which is its content as it currently
    /// stands.
    /// </param>
    /// <returns>
    /// Whether the recipe is visible in the resolved workspace, and the version to copy when one was found.
    /// The two are separate answers because they are separate facts: an invisible recipe must be answered
    /// like one that never existed (tenancy.md), while a readable recipe with no version 9 must be told
    /// which number was wrong.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The visibility probe runs first and separately</strong>, exactly as on the history and
    /// comparison reads. The snapshot query returns nothing for an invisible recipe too — the query filter
    /// sees to that — but nothing is also what a readable recipe with a mistyped version number returns, and
    /// the two must not become one answer by accident.
    /// </para>
    /// <para>
    /// <strong>No transaction.</strong> Versions and their snapshots are immutable, so neither statement can
    /// see the other's rows change, and the copy this feeds is written by <see cref="CreateAsync"/> in a
    /// boundary of its own — there is nothing here for a shared one to protect.
    /// </para>
    /// <para>
    /// <strong>The aggregate is never loaded.</strong> A duplicate copies an archived document, so reading
    /// the live recipe would fetch eight queries' worth of rows that nothing then reads — and would invite
    /// the copy to be built from tracked entities belonging to another recipe.
    /// </para>
    /// </remarks>
    Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindDuplicateSourceAsync(
        Guid recipeId,
        int? versionNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Moves a recipe between editorial states and records the move — atomically, and only if the recipe is
    /// still in the state the command was composed against.
    /// </summary>
    /// <param name="loaded">
    /// The unit <see cref="GetForUpdateAsync"/> returned, with <c>Status</c> already set to its new value by
    /// Business. The recipe's <c>RowVersion</c> still holds the value that read returned.
    /// </param>
    /// <param name="audit">
    /// The entry describing the transition. Staged on this same context, so the audit row and the status
    /// change commit together or neither does — which is the whole reason an audited command cannot write
    /// its two halves in two saves.
    /// </param>
    /// <returns>
    /// <c>false</c> when the recipe had already moved on, in which case nothing was written and nothing is
    /// left staged.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>No version is written, deliberately.</strong> A <c>RecipeVersion</c> is a snapshot of what a
    /// recipe <em>said</em>, and archiving changes nothing it says — its ingredients, steps, equipment, media
    /// links and tags are all exactly where they were, which is what "archive is not a delete" means in
    /// practice. The lifecycle move is recorded in the audit log, which is where REC-006 puts it.
    /// </para>
    /// <para>
    /// That omission also closes a hole worth naming: because no version is ever written while a recipe is
    /// archived, and archiving itself writes none, <strong>no snapshot can carry
    /// <c>RecipeStatus.Archived</c></strong>. A later version restore therefore cannot archive a recipe
    /// behind the audit log's back, even though it does restore the status a snapshot holds.
    /// </para>
    /// <para>
    /// <strong>A no-op never reaches here.</strong> Business answers a command that asks for the state the
    /// recipe is already in without calling this, so there is no audit entry claiming a transition that did
    /// not happen, and no stamped <c>UpdatedAt</c> invalidating tokens for nothing.
    /// </para>
    /// <para>
    /// Conflicts are detected the same way <see cref="UpdateAsync"/> detects them, minus the second guard:
    /// nothing inserts a version here, so the unique index on <c>(WorkspaceId, RecipeId, VersionNumber)</c>
    /// cannot fire first and the row version is the only race in play.
    /// </para>
    /// </remarks>
    Task<bool> TrySetStatusAsync(
        TaggedRecipe loaded,
        AuditEntry audit,
        CancellationToken cancellationToken);
}

internal sealed class RecipeDataLayer(
    CreatorPantryDbContext context,
    IRecipeRepository recipes,
    IRecipeVersionRepository versions,
    IWorkspaceTagRepository workspaceTags,
    IRecipeSearchRepository search,
    IAuditWriter auditWriter) : IRecipeDataLayer
{
    public async Task<(IReadOnlyList<RecipeSummaryRecord> Rows, bool HasMore, int? Total)> SearchAsync(
        RecipeSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await search.SearchAsync(criteria, cancellationToken);

        // The page first, so a caller that asked for a total still gets their rows from the cheaper statement if
        // the count is what fails. Skipped entirely when unwanted: an unasked-for count is a query nobody reads.
        var total = criteria.IncludeTotal
            ? await search.CountAsync(criteria, cancellationToken)
            : (int?)null;

        return (rows, hasMore, total);
    }

    public async Task<(IReadOnlyList<RecipeVersionHistoryRecord> Rows, bool HasMore)?> ListVersionsAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken)
    {
        // First, and on every page rather than only the first. A cursor proves where a previous page ended,
        // not that the recipe is still visible — and a caller removed from a workspace between two pages must
        // be told the same thing they would be told about any recipe they may not see.
        if (!await recipes.ExistsAsync(criteria.RecipeId, cancellationToken))
        {
            return null;
        }

        return await versions.ListHistoryAsync(criteria, cancellationToken);
    }

    public async Task<IReadOnlyList<RecipeVersionSnapshotRecord>?> FindVersionSnapshotsAsync(
        Guid recipeId,
        int firstVersionNumber,
        int secondVersionNumber,
        CancellationToken cancellationToken)
    {
        // First, for the reason ListVersionsAsync gives: whether the recipe is visible is a separate question
        // from whether these version numbers exist, and a caller who may not see the recipe must be told that
        // rather than which of its versions are missing.
        if (!await recipes.ExistsAsync(recipeId, cancellationToken))
        {
            return null;
        }

        return await versions.FindSnapshotsAsync(recipeId, firstVersionNumber, secondVersionNumber, cancellationToken);
    }

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

    public async Task<RecipeRestoreUnit?> GetForRestoreAsync(
        Guid recipeId,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        // The aggregate first, and tracked: it is both what the restore will change and the answer to whether
        // this recipe is visible at all. A snapshot read for a recipe nobody may see would be work done on
        // behalf of a caller who gets nothing.
        var loaded = await GetForUpdateAsync(recipeId, cancellationToken);
        if (loaded is null)
        {
            return null;
        }

        // Scoped by the recipe, which is what makes the version number mean anything — a number is unique
        // only within its recipe, and another recipe's version 3 matches nothing here rather than being
        // refused by a check somebody has to remember to write.
        return new RecipeRestoreUnit(
            loaded, await versions.FindSnapshotAsync(recipeId, versionNumber, cancellationToken));
    }

    public Task<IReadOnlyList<WorkspaceTag>> FindWorkspaceTagsAsync(
        IReadOnlyCollection<Guid> tagIds,
        CancellationToken cancellationToken) =>
        workspaceTags.FindByIdsAsync(tagIds, cancellationToken);

    public async Task<(bool RecipeVisible, RecipeVersionSnapshotRecord? Source)> FindDuplicateSourceAsync(
        Guid recipeId,
        int? versionNumber,
        CancellationToken cancellationToken)
    {
        // First, for the reason ListVersionsAsync gives: whether the recipe is visible is a separate question
        // from whether this version number exists, and a caller who may not see the recipe must be told that
        // rather than which of its versions are missing.
        if (!await recipes.ExistsAsync(recipeId, cancellationToken))
        {
            return (false, null);
        }

        var source = versionNumber is { } number
            ? await versions.FindSnapshotAsync(recipeId, number, cancellationToken)
            : await versions.FindCurrentSnapshotAsync(recipeId, cancellationToken);

        return (true, source);
    }

    public async Task<bool> TrySetStatusAsync(
        TaggedRecipe loaded,
        AuditEntry audit,
        CancellationToken cancellationToken)
    {
        var recipe = loaded.Recipe.Recipe;

        // Captured before the save, because a successful save replaces it with the token the server just
        // generated — and this is the value the losing writer needs to recognise that it lost.
        var readWith = recipe.RowVersion;

        // Staged, not saved: the audit row joins the recipe's UPDATE in one batch. IAuditWriter exists to be
        // used exactly this way.
        auditWriter.Record(audit);

        try
        {
            // One save, one transaction — the recipe's UPDATE carries the WHERE clause quoting the RowVersion
            // this aggregate was read with, so nothing commits unless the recipe is still where the command
            // left off.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // One guard rather than UpdateAsync's two: no version is inserted here, so the unique index on
            // version numbers cannot refuse the batch first and this exception is the only way to lose.
            //
            // Nothing was written, and nothing may be left staged either — the recipe is still Modified and
            // the audit row still Added, so the next SaveChanges on this scope would commit a transition the
            // caller was told had been refused.
            context.ChangeTracker.Clear();

            return false;
        }

        return true;
    }

    public Task<RecipeUpdateOutcome> RestoreAsync(
        TaggedRecipe loaded,
        RecipeVersionFacts version,
        IReadOnlyList<WorkspaceTag> tags,
        CancellationToken cancellationToken) =>
        // Nothing to do to the tags: Business reconciled the links from the archive and resolved these rows
        // through FindWorkspaceTagsAsync, so unlike an edit there is no vocabulary to create or reuse here.
        CommitAsync(loaded, version, tags, cancellationToken);

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

        return await CommitAsync(loaded, version, vocabulary, cancellationToken);
    }

    /// <summary>
    /// Captures the version, saves the aggregate and the version together, and translates the race both
    /// writes can lose.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="UpdateAsync"/> and <see cref="RestoreAsync"/> because the two differ only in how
    /// their tags were settled and in what the version says about itself — everything from the snapshot
    /// onwards is one operation, and two copies of the two-guard conflict logic below would be two chances to
    /// get it subtly differently right.
    /// </remarks>
    private async Task<RecipeUpdateOutcome> CommitAsync(
        TaggedRecipe loaded,
        RecipeVersionFacts version,
        IReadOnlyList<WorkspaceTag> vocabulary,
        CancellationToken cancellationToken)
    {
        var recipe = loaded.Recipe.Recipe;

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

        return new TaggedRecipe(
            loaded,
            await workspaceTags.FindByIdsAsync(tagIds, cancellationToken),

            // Only for a copy, which most recipes are not — so the common read costs nothing, and the
            // resolution happens here rather than in each of the three seams that answer with a detail.
            loaded.Recipe.DuplicatedFromVersionId is { } versionId
                ? await versions.FindDuplicateSourceLineageAsync(versionId, cancellationToken)
                : null);
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

            // What this version replaced and where its content came from are two different facts, and on a
            // restore they are two different versions. The parent above is the one superseded; this is the one
            // copied, and it is null on every write that is not a restore — a pairing the database enforces.
            RestoredFromVersionId = facts.RestoredFromVersionId,

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
