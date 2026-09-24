using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// EF Core access for <see cref="RecipeVersion"/>, which is an aggregate root of its own rather than part of
/// the recipe — it outlives the edits that supersede it and is read independently. Persistence only.
/// </summary>
public interface IRecipeVersionRepository
{
    /// <summary>
    /// Stages a version and, when one is attached, its snapshot for insertion.
    /// </summary>
    /// <remarks>
    /// Synchronous and non-saving, for the same reason <see cref="IRecipeRepository.Add"/> is: the DataLayer
    /// decides when the unit commits, and a version must commit in the same batch as the content it
    /// describes. Nothing here updates or deletes, because nothing may — these rows are
    /// <see cref="CreatorPantry.Domain.Managers.Persistence.IImmutableRecord"/>, and a repository method that
    /// offered it would only be a way to find that out at runtime.
    /// </remarks>
    void Add(RecipeVersion version);

    /// <summary>
    /// Reads one page of a recipe's history — version metadata only, highest version number first.
    /// </summary>
    /// <returns>
    /// The page's rows, and whether another page follows. An empty page is not an error and does not mean the
    /// recipe is missing: whether the recipe is visible at all is a question this method neither asks nor
    /// answers, and one the caller must have settled first.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>The snapshot table is never named.</strong> The document lives apart from its version precisely
    /// so that listing a history does not read the archive, and a projection here that reached for it would
    /// undo the reason the tables are separate — a recipe edited two hundred times would return two hundred
    /// complete copies of itself to render a list of dates.
    /// </para>
    /// <para>
    /// Ordered by <c>VersionNumber</c> descending, which is a backward scan of
    /// <c>UX_RecipeVersions_Workspace_Recipe_VersionNumber</c> — no sort, and no new index. By number rather
    /// than by <c>CreatedAt</c> for the reason every other read of these rows gives: the number is the
    /// gap-free identity creators cite, and two versions written in the same tick would make a timestamp
    /// ordering arbitrary.
    /// </para>
    /// <para>
    /// Fetches one row beyond the limit and discards it, which is how "does another page follow" is answered
    /// without a second <c>COUNT</c>. <strong>No cursor is minted here</strong>: a cursor is bound to the route
    /// that issued it, and a repository is handed a predicate rather than a route.
    /// </para>
    /// </remarks>
    Task<(IReadOnlyList<RecipeVersionHistoryRecord> Rows, bool HasMore)> ListHistoryAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the archived content of up to two of one recipe's versions, named by number.
    /// </summary>
    /// <returns>
    /// The rows that exist and carry a document — none, one, or two, in no particular order. Which numbers
    /// were asked for and what a shortfall means is the caller's to decide; this neither knows nor asks
    /// whether the recipe is visible.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Two numbers rather than a list, and one round trip rather than two.</strong> A comparison needs
    /// exactly two documents and there is no route that wants three; a second query per side would double the
    /// cost of the only read in this module that fetches whole recipes.
    /// </para>
    /// <para>
    /// <strong>The recipe predicate is what scopes the version numbers</strong>, and it is not optional
    /// decoration. A version number is unique only within its recipe, so without it this would match every
    /// recipe's version 2 in the workspace. With it, a number naming another recipe's version matches nothing
    /// — the containment is in the query rather than in a check a caller has to remember.
    /// </para>
    /// <para>
    /// <strong>No <c>WorkspaceId</c> predicate</strong>, for the reason <see cref="ListHistoryAsync"/> gives:
    /// both <c>RecipeVersions</c> and <c>RecipeVersionSnapshots</c> are workspace-owned, so the global query
    /// filter already scopes this on both sides of the join, and writing one by hand would suggest the filter
    /// is optional.
    /// </para>
    /// <para>
    /// <strong>Versions with no snapshot are not returned.</strong> The join is inner, so a version carrying no
    /// archived document is simply absent — there is nothing about it to compare, and returning it with an
    /// empty document would push that discovery one layer up to be handled there instead.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<RecipeVersionSnapshotRecord>> FindSnapshotsAsync(
        Guid recipeId,
        int firstVersionNumber,
        int secondVersionNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the archived content of one of a recipe's versions, named by number.
    /// </summary>
    /// <returns>
    /// The row, or <c>null</c> when this recipe has no such version or the version carries no document. What
    /// that means is the caller's to decide; as above, this neither knows nor asks whether the recipe is
    /// visible.
    /// </returns>
    /// <remarks>
    /// Separate from <see cref="FindSnapshotsAsync"/> rather than that method called with one number twice.
    /// The two-sided read exists for a comparison and says so — it returns an unordered set the caller matches
    /// by number, and a shortfall there means "one of the two is missing". A restore asks a different
    /// question with a different answer shape, and borrowing the comparison read would make every reader work
    /// out which of the two numbers was the real one.
    /// </remarks>
    Task<RecipeVersionSnapshotRecord?> FindSnapshotAsync(
        Guid recipeId,
        int versionNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the archived content of a recipe's most recent version.
    /// </summary>
    /// <returns>
    /// The row, or <c>null</c> when the recipe has no version carrying a document — which, for a recipe
    /// created through this API, means it is not visible here. As above, this neither knows nor asks whether
    /// it is.
    /// </returns>
    /// <remarks>
    /// <para>
    /// For a caller that wants a recipe's content as it currently stands and wants it from the archive: the
    /// current version's document and the live aggregate are the same content, because every change that
    /// alters a recipe writes a version. Reading the archive instead of the aggregate is what lets a copy
    /// name a real version as its source whether or not the request chose one.
    /// </para>
    /// <para>
    /// Ordered by <c>VersionNumber</c> descending and taking one, which is a top-one backward seek of
    /// <c>UX_RecipeVersions_Workspace_Recipe_VersionNumber</c> — no sort and no new index. By number rather
    /// than by <c>CreatedAt</c> for the reason every other read of these rows gives: the number is the
    /// gap-free identity creators cite, and two versions written in the same tick would make a timestamp
    /// ordering arbitrary.
    /// </para>
    /// </remarks>
    Task<RecipeVersionSnapshotRecord?> FindCurrentSnapshotAsync(
        Guid recipeId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves one version id into the recipe it belongs to, for naming what a copy was duplicated from.
    /// </summary>
    /// <returns>
    /// The version and its recipe, or <c>null</c> when neither is visible in the resolved workspace.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>By id, not by recipe and number</strong> — the one read in this file that is, and necessarily:
    /// the caller holds a stored <c>DuplicatedFromVersionId</c> and does not yet know which recipe it names.
    /// That is safe here in a way it would not be on a route, because the id came out of a row this workspace
    /// already owns rather than out of a request.
    /// </para>
    /// <para>
    /// <strong>No <c>WorkspaceId</c> predicate</strong>, as everywhere else: both tables are workspace-owned,
    /// so the global query filter scopes the join on both sides. The composite foreign key behind
    /// <c>Recipes.DuplicatedFromVersionId</c> already makes lineage across a boundary unrepresentable — this
    /// filter is what makes it unreadable too, so the two controls do not depend on each other.
    /// </para>
    /// <para>
    /// <strong>The archive is never touched.</strong> Naming a copy's source needs the version's number and
    /// its recipe's title, and nothing about what either of them said.
    /// </para>
    /// </remarks>
    Task<RecipeDuplicateSourceRecord?> FindDuplicateSourceLineageAsync(
        Guid versionId,
        CancellationToken cancellationToken);
}

internal sealed class RecipeVersionRepository(CreatorPantryDbContext context) : IRecipeVersionRepository
{
    public void Add(RecipeVersion version) => context.RecipeVersions.Add(version);

    public async Task<(IReadOnlyList<RecipeVersionHistoryRecord> Rows, bool HasMore)> ListHistoryAsync(
        RecipeVersionHistoryCriteria criteria,
        CancellationToken cancellationToken)
    {
        // No WorkspaceId predicate: RecipeVersions is workspace-owned, so the global query filter already
        // scopes this, and writing one by hand would suggest the filter is optional. That is also what makes
        // the recipe predicate safe on its own — another workspace's version is not merely filtered out, it is
        // not visible to be matched.
        var versions = context.RecipeVersions
            // Never tracked. These rows are immutable, so the only thing tracking them could add is the
            // possibility of trying to change one.
            .AsNoTracking()
            .Where(version => version.RecipeId == criteria.RecipeId);

        if (criteria.Position is { } position)
        {
            // Strictly below the last row returned. One column, matching the ORDER BY exactly — which is the
            // whole correctness condition of a keyset, and is trivially met here because the ordering is total
            // on this column alone.
            versions = versions.Where(version => version.VersionNumber < position.VersionNumber);
        }

        var fetched = await versions
            .OrderByDescending(version => version.VersionNumber)
            // One row past the limit; it is never returned, only counted.
            .Take(criteria.Limit + 1)
            // Projected in SQL, and deliberately column by column rather than by materialising the entity:
            // an entity projection would carry BasedOnRecipeRowVersion and SnapshotSchemaVersion along for
            // no reader, and would leave a future Include of the snapshot one line away.
            .Select(version => new RecipeVersionHistoryRecord(
                version.Id,
                version.VersionNumber,
                version.CreatedByMembershipId,
                version.Source,
                version.Readiness,
                version.Reason,
                version.CreatedAt,
                version.ParentVersionId,
                version.RestoredFromVersionId,
                version.AiProposalId))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(criteria.Limit);
    }

    public async Task<IReadOnlyList<RecipeVersionSnapshotRecord>> FindSnapshotsAsync(
        Guid recipeId,
        int firstVersionNumber,
        int secondVersionNumber,
        CancellationToken cancellationToken) =>
        await context.RecipeVersions
            // Never tracked, as above: these rows are immutable, and the documents are large enough that
            // handing the change tracker two whole recipes to snapshot would be a cost with no purchaser.
            .AsNoTracking()
            .Where(version => version.RecipeId == recipeId
                && (version.VersionNumber == firstVersionNumber || version.VersionNumber == secondVersionNumber)
                // Makes the projection's join inner. Without it a version with no snapshot would come back
                // carrying a null document, which nothing above could do anything useful with.
                && version.Snapshot != null)
            // No OrderBy: at most two rows, which the caller matches by number rather than by position. An
            // ordering here would be a promise about a set too small for one to mean anything.
            .Select(version => new RecipeVersionSnapshotRecord(
                version.Id,
                version.VersionNumber,
                version.Source,
                version.Readiness,
                version.CreatedAt,
                version.Snapshot!.Document))
            .ToListAsync(cancellationToken);

    public async Task<RecipeVersionSnapshotRecord?> FindSnapshotAsync(
        Guid recipeId,
        int versionNumber,
        CancellationToken cancellationToken) =>
        await context.RecipeVersions
            // Never tracked, as above: the row is immutable, and a restore reads its document rather than
            // touching the version it came from.
            .AsNoTracking()
            .Where(version => version.RecipeId == recipeId
                && version.VersionNumber == versionNumber
                // Makes the projection's join inner, as on the two-sided read: a version with no archived
                // document has nothing to restore, and returning it carrying a null document would push that
                // discovery a layer up.
                && version.Snapshot != null)
            .Select(version => new RecipeVersionSnapshotRecord(
                version.Id,
                version.VersionNumber,
                version.Source,
                version.Readiness,
                version.CreatedAt,
                version.Snapshot!.Document))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<RecipeVersionSnapshotRecord?> FindCurrentSnapshotAsync(
        Guid recipeId,
        CancellationToken cancellationToken) =>
        await context.RecipeVersions
            .AsNoTracking()
            .Where(version => version.RecipeId == recipeId && version.Snapshot != null)
            // Highest number first, then one row. The index is ordered on this column, so this is a seek
            // rather than a scan of the recipe's history.
            .OrderByDescending(version => version.VersionNumber)
            .Select(version => new RecipeVersionSnapshotRecord(
                version.Id,
                version.VersionNumber,
                version.Source,
                version.Readiness,
                version.CreatedAt,
                version.Snapshot!.Document))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<RecipeDuplicateSourceRecord?> FindDuplicateSourceLineageAsync(
        Guid versionId,
        CancellationToken cancellationToken) =>
        await (
            from version in context.RecipeVersions.AsNoTracking()
            where version.Id == versionId

            // An explicit join because RecipeVersion has no navigation to Recipe — its reference is
            // configured without one, so that a version cannot be used as a way into the aggregate it
            // describes. Both sides carry the workspace query filter.
            join recipe in context.Recipes.AsNoTracking() on version.RecipeId equals recipe.Id
            select new RecipeDuplicateSourceRecord(
                version.Id, version.VersionNumber, recipe.Id, recipe.Title))
            .SingleOrDefaultAsync(cancellationToken);
}
