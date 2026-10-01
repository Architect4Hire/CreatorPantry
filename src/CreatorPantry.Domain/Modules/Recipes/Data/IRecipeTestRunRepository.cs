using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// EF Core access for <see cref="RecipeTestRun"/> and the rows beneath it. Persistence only.
/// </summary>
/// <remarks>
/// A test run is an aggregate root of its own: it is listed and read without its recipe, and it is pinned to
/// an immutable version rather than owned by one. It is not part of the recipe aggregate and is never loaded
/// through <see cref="IRecipeRepository"/>.
/// </remarks>
public interface IRecipeTestRunRepository
{
    /// <summary>
    /// Stages a whole run — the root and every observation, issue and attachment hanging off it — for the next
    /// save.
    /// </summary>
    /// <remarks>
    /// One call for the entire graph rather than one per child, because EF walks the navigation collections
    /// and because a half-staged test is not a thing any caller should be able to produce. Nothing is written
    /// until the DataLayer saves.
    /// </remarks>
    void Add(RecipeTestRun run);

    /// <summary>
    /// One complete test run of one recipe, tracked and ready to be edited, or null when there is no such run
    /// in the resolved workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scoped by the recipe as well as the run id. A run id that names another recipe's test answers null
    /// rather than being loaded and refused, which keeps the route's 404 the same answer for an unknown id, a
    /// foreign workspace's and a different recipe's.
    /// </para>
    /// <para>
    /// <strong>Resolutions come with it.</strong> They are read-only to an edit, but the edit has to know which
    /// issues carry one: dropping a resolved issue is refused, and a reconciliation that could not see the
    /// resolution would instead hand the delete to the database and get an immutability failure that names
    /// nothing a creator can act on.
    /// </para>
    /// <para>
    /// Attachments are not loaded. Nothing writes one yet, and an edit that cannot change them has no reason
    /// to read them.
    /// </para>
    /// </remarks>
    Task<RecipeTestRun?> GetForUpdateAsync(Guid recipeId, Guid testRunId, CancellationToken cancellationToken);

    /// <summary>
    /// One complete test run of one recipe for reading, or null when there is no such run in the resolved
    /// workspace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same graph as <see cref="GetForUpdateAsync"/> and the same scoping, untracked. A separate method
    /// rather than a <c>tracked</c> flag on that one: the change tracker is the difference between a read and
    /// the start of a write, and a caller that passed the wrong flag would get a method that silently behaves
    /// as the other one. Tracking a graph nothing intends to change also costs a snapshot of every row.
    /// </para>
    /// <para>
    /// <strong>Resolutions come with it</strong>, because what was decided about a problem is most of what a
    /// reader wants to know about it. Attachments do not: nothing writes one yet.
    /// </para>
    /// </remarks>
    Task<RecipeTestRun?> GetForReadAsync(Guid recipeId, Guid testRunId, CancellationToken cancellationToken);

    /// <summary>
    /// One issue of one run, with its resolution if it has one and the version number the run was recorded
    /// against — everything a resolution has to judge, in one read.
    /// </summary>
    /// <remarks>
    /// The tested version's <em>number</em> comes back because that is what a correction version is compared
    /// against: numbers run upward within a recipe, so "later than what was tested" is a comparison of two
    /// integers rather than of two timestamps. Null when the run or the issue is not there.
    /// </remarks>
    Task<TestIssueResolutionTarget?> FindIssueForResolutionAsync(
        Guid recipeId, Guid testRunId, Guid issueId, CancellationToken cancellationToken);

    /// <summary>Stages one resolution for the next save.</summary>
    void AddResolution(TestIssueResolution resolution);
}

internal sealed class RecipeTestRunRepository(CreatorPantryDbContext context) : IRecipeTestRunRepository
{
    public void Add(RecipeTestRun run) => context.RecipeTestRuns.Add(run);

    public void AddResolution(TestIssueResolution resolution) => context.TestIssueResolutions.Add(resolution);

    public async Task<RecipeTestRun?> GetForUpdateAsync(
        Guid recipeId,
        Guid testRunId,
        CancellationToken cancellationToken) =>
        await context.RecipeTestRuns
            // Tracked, deliberately: this is the graph the edit mutates, and the reconciliation works by
            // changing loaded entities rather than by issuing statements.
            //
            // No WorkspaceId predicate: every entity here is workspace-owned, so the global query filter
            // already scopes all four sets. Another workspace's run is not found rather than found and refused.
            .Include(run => run.Observations)
            .Include(run => run.Issues)
                .ThenInclude(issue => issue.Resolution)
            .SingleOrDefaultAsync(
                run => run.Id == testRunId && run.RecipeId == recipeId, cancellationToken);

    public async Task<RecipeTestRun?> GetForReadAsync(
        Guid recipeId,
        Guid testRunId,
        CancellationToken cancellationToken) =>
        await context.RecipeTestRuns
            .AsNoTracking()
            // Same includes and same scoping as the tracked read above — including the resolutions, which are
            // most of what a reader of an issue wants. No WorkspaceId predicate: every set here is
            // workspace-owned, so the global query filter already scopes all four, and another workspace's run
            // is not found rather than found and refused.
            .Include(run => run.Observations)
            .Include(run => run.Issues)
                .ThenInclude(issue => issue.Resolution)
            .SingleOrDefaultAsync(
                run => run.Id == testRunId && run.RecipeId == recipeId, cancellationToken);

    public async Task<TestIssueResolutionTarget?> FindIssueForResolutionAsync(
        Guid recipeId,
        Guid testRunId,
        Guid issueId,
        CancellationToken cancellationToken) =>

        // No tracking anywhere in this query: nothing about the issue, the run or the version is written by a
        // resolution. TESTRUN-002's "resolving an issue does not rewrite the test observation" is stated here
        // as the shape of the read rather than as a promise the write has to keep.
        //
        // Joined rather than loaded: all this needs from the run is which version it tested, and all it needs
        // from that version is its number. No WorkspaceId predicate — every set here is workspace-owned and the
        // global query filter scopes all three.
        await (from issue in context.TestIssues.AsNoTracking()
               where issue.Id == issueId
                   && issue.RecipeTestRunId == testRunId
                   && issue.RecipeId == recipeId
               join run in context.RecipeTestRuns on issue.RecipeTestRunId equals run.Id
               join version in context.RecipeVersions on run.RecipeVersionId equals version.Id
               select new TestIssueResolutionTarget(
                   issue.Id,
                   issue.RecipeId,
                   run.RecipeVersionId,
                   version.VersionNumber,
                   issue.Resolution != null))
            .SingleOrDefaultAsync(cancellationToken);
}

/// <summary>
/// Everything a resolution has to judge about one issue: which recipe it belongs to, which version was tested,
/// and whether somebody has already decided.
/// </summary>
public sealed record TestIssueResolutionTarget(
    Guid IssueId,
    Guid RecipeId,
    Guid TestedVersionId,
    int TestedVersionNumber,
    bool AlreadyResolved);
