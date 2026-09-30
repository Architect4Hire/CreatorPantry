using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// Complete persistence operations for the test-kitchen seam. Composes repositories and owns the transaction
/// boundary; makes no product decisions.
/// </summary>
public interface IRecipeTestRunDataLayer
{
    /// <summary>
    /// What a write needs to know about the recipe and the version a request named, in one lookup.
    /// </summary>
    /// <returns>
    /// Null when the recipe is not visible in the resolved workspace — an unknown recipe and another
    /// workspace's recipe are deliberately indistinguishable here (tenancy.md). Otherwise the recipe's status
    /// and the named version's id, which is itself null when the recipe has no such version.
    /// </returns>
    /// <remarks>
    /// Visibility is settled before the version is looked for, for the reason
    /// <c>RecipeDataLayer.FindDuplicateSourceAsync</c> gives: whether the recipe can be seen is a separate
    /// question from whether this version number exists, and a caller who may not see the recipe must be told
    /// that rather than which of its versions are missing.
    /// </remarks>
    Task<TestRunTarget?> FindTargetAsync(Guid recipeId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>
    /// The editorial state of a visible recipe, or null when there is no such recipe in the resolved
    /// workspace.
    /// </summary>
    /// <remarks>
    /// For the writes that gate on <see cref="RecipePolicy.AcceptsContentChanges"/> but name no version of
    /// their own. Separate from <see cref="FindTargetAsync"/> rather than called with a version number nobody
    /// asked about: that method's second argument means "the version the caller named", and passing version 1
    /// to learn a status would work today only because every recipe has one.
    /// </remarks>
    Task<RecipeStatus?> FindRecipeStatusAsync(Guid recipeId, CancellationToken cancellationToken);

    /// <summary>Writes one complete test run and everything beneath it, atomically.</summary>
    Task<CreatedRecipeTestRun> CreateAsync(RecipeTestRun run, CancellationToken cancellationToken);

    /// <summary>
    /// One complete test run of one recipe, tracked and ready to be edited, or null when there is no such run
    /// in the resolved workspace.
    /// </summary>
    Task<RecipeTestRun?> GetForUpdateAsync(Guid recipeId, Guid testRunId, CancellationToken cancellationToken);

    /// <summary>
    /// Commits an edit already applied to a tracked run, refusing it if the run has moved on since it was read.
    /// </summary>
    Task<TestRunUpdateOutcome> UpdateAsync(RecipeTestRun run, CancellationToken cancellationToken);

    /// <inheritdoc cref="IRecipeTestRunRepository.FindIssueForResolutionAsync"/>
    Task<TestIssueResolutionTarget?> FindIssueForResolutionAsync(
        Guid recipeId, Guid testRunId, Guid issueId, CancellationToken cancellationToken);

    /// <summary>
    /// The id of one version of one recipe, by the number the history lists it under, or null when this recipe
    /// has no such version.
    /// </summary>
    Task<Guid?> FindVersionIdAsync(Guid recipeId, int versionNumber, CancellationToken cancellationToken);

    /// <summary>Writes one resolution, refusing it if the issue has been resolved in the meantime.</summary>
    Task<TestIssueResolutionOutcome> ResolveAsync(
        TestIssueResolution resolution, CancellationToken cancellationToken);

    /// <summary>
    /// One page of a recipe's test history, and the counts beside it when the caller asked for them.
    /// </summary>
    /// <returns>
    /// Null when the recipe is not visible in the resolved workspace — an unknown recipe and another workspace's
    /// recipe are deliberately indistinguishable here, exactly as on <see cref="FindTargetAsync"/> (tenancy.md).
    /// Otherwise the page, whether another follows, and the counts or null.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>Visibility is settled first, and on every page rather than only the first.</strong> A cursor proves
    /// where a previous page ended, not that the recipe is still visible — and a caller removed from a workspace
    /// between two pages must be told the same thing they would be told about any recipe they may not see. The
    /// same reasoning, and the same shape, as <c>RecipeDataLayer.ListVersionsAsync</c>.
    /// </para>
    /// <para>
    /// <strong>An empty page is not the same answer as a missing recipe.</strong> A recipe nobody has tested
    /// exists and has no tests; that is a page of nothing, not a 404. Only this null means "you may not see it".
    /// </para>
    /// </remarks>
    Task<TestRunHistoryPage?> ListAsync(TestRunHistoryCriteria criteria, CancellationToken cancellationToken);
}

/// <summary>
/// What one read of a recipe's test history produced: the rows, whether another page follows, and the counts when
/// they were asked for.
/// </summary>
/// <remarks>
/// A named record rather than a tuple, unlike the recipe search's equivalent, because three members of which one
/// is optional is where a positional tuple stops reading as anything: <c>(rows, hasMore, counts)</c> at a call
/// site says nothing about which null means "not asked for".
/// </remarks>
/// <param name="Counts">
/// Null when <see cref="TestRunHistoryCriteria.IncludeSummary"/> was false. Never null to mean "nothing to
/// count" — an empty filtered set counts to zero.
/// </param>
public sealed record TestRunHistoryPage(
    IReadOnlyList<TestRunHistoryRecord> Rows,
    bool HasMore,
    TestRunHistoryCounts? Counts);

internal sealed class RecipeTestRunDataLayer(
    CreatorPantryDbContext context,
    IRecipeRepository recipes,
    IRecipeVersionRepository versions,
    IRecipeTestRunRepository testRuns,
    IRecipeTestRunHistoryRepository history) : IRecipeTestRunDataLayer
{
    public async Task<TestRunTarget?> FindTargetAsync(
        Guid recipeId,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        // First, and on its own. A version id read for a recipe nobody may see would be work done on behalf of
        // a caller who gets nothing, and the answer would have to be discarded anyway.
        if (await recipes.FindStatusAsync(recipeId, cancellationToken) is not { } status)
        {
            return null;
        }

        // Scoped by the recipe, which is what makes the number mean anything — another recipe's version 3
        // matches nothing here.
        return new TestRunTarget(
            status, await versions.FindVersionIdAsync(recipeId, versionNumber, cancellationToken));
    }

    public async Task<CreatedRecipeTestRun> CreateAsync(RecipeTestRun run, CancellationToken cancellationToken)
    {
        testRuns.Add(run);

        // The transaction boundary, and the whole of it. One SaveChangesAsync over every pending entity on one
        // DbContext is already one transaction — the same argument RecipeDataLayer.CreateAsync makes, and for
        // the same reason an explicit BeginTransactionAsync would be worse here: EnrichSqlServerDbContext
        // enables a retrying execution strategy, under which EF refuses a user-initiated transaction unless it
        // is wrapped in CreateExecutionStrategy().ExecuteAsync. This spans one statement batch.
        //
        // It matters more here than it looks: a run, its notes and its issues are one record of one cook, and
        // a partially written test would be evidence nobody could trust.
        await context.SaveChangesAsync(cancellationToken);

        // Read back off the staged graph rather than re-queried: these are the ids Business generated, and the
        // save is what made them real. Ordered as they were built, which is the order the request submitted —
        // that is the contract CreatedRecipeTestRunServiceModel publishes.
        return new CreatedRecipeTestRun(
            run.Id,
            [.. run.Observations.OrderBy(observation => observation.SortOrder).Select(observation => observation.Id)],
            [.. run.Issues.OrderBy(issue => issue.SortOrder).Select(issue => issue.Id)]);
    }

    public Task<RecipeStatus?> FindRecipeStatusAsync(Guid recipeId, CancellationToken cancellationToken) =>
        recipes.FindStatusAsync(recipeId, cancellationToken);

    public Task<RecipeTestRun?> GetForUpdateAsync(
        Guid recipeId,
        Guid testRunId,
        CancellationToken cancellationToken) =>
        testRuns.GetForUpdateAsync(recipeId, testRunId, cancellationToken);

    public Task<TestIssueResolutionTarget?> FindIssueForResolutionAsync(
        Guid recipeId,
        Guid testRunId,
        Guid issueId,
        CancellationToken cancellationToken) =>
        testRuns.FindIssueForResolutionAsync(recipeId, testRunId, issueId, cancellationToken);

    public Task<Guid?> FindVersionIdAsync(Guid recipeId, int versionNumber, CancellationToken cancellationToken) =>
        versions.FindVersionIdAsync(recipeId, versionNumber, cancellationToken);

    public async Task<TestRunUpdateOutcome> UpdateAsync(RecipeTestRun run, CancellationToken cancellationToken)
    {
        try
        {
            // One save, one transaction — the create's argument applies unchanged. What is added here is the
            // WHERE clause EF puts on the run's UPDATE, quoting the RowVersion this graph was read with:
            // nothing commits unless the run is still in the state the edit was composed against, and the
            // observations and issues reconciled alongside it commit or fail with it.
            //
            // Only one guard is needed, unlike the recipe's edit: this writes no version row, so there is no
            // unique index that could refuse the batch before the row-version check gets to.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was written, and nothing may be left staged either: the run is still Modified and its
            // reconciled children still Added or Deleted, so the next SaveChanges on this scope would commit an
            // edit the caller was told had been refused. Clearing here makes "nothing was written" a property
            // of this method rather than of whoever happens to save next.
            context.ChangeTracker.Clear();

            // Expected, not exceptional: two people writing up one bake is the ordinary case. Translated here
            // because this is the layer that knows the save failed and why (backend.md).
            return TestRunUpdateOutcome.Conflict();
        }

        return TestRunUpdateOutcome.Applied();
    }

    public async Task<TestIssueResolutionOutcome> ResolveAsync(
        TestIssueResolution resolution,
        CancellationToken cancellationToken)
    {
        testRuns.AddResolution(resolution);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // UX_TestIssueResolutions_Workspace_Recipe_Issue refusing a second resolution. Business has already
            // asked whether one exists and answered readably; reaching here means another request committed
            // between that read and this write, which is the race the index exists to settle.
            //
            // Caught as DbUpdateException rather than by reading SQL Server error numbers, and narrowed by the
            // question that matters: nothing else about this insert can fail that way. The composite foreign
            // keys were satisfied by ids Business resolved through workspace-filtered reads, and the check
            // constraints by rules the validator already enforced.
            context.ChangeTracker.Clear();

            return TestIssueResolutionOutcome.AlreadyResolved();
        }

        return TestIssueResolutionOutcome.Applied(resolution);
    }

    public async Task<TestRunHistoryPage?> ListAsync(
        TestRunHistoryCriteria criteria,
        CancellationToken cancellationToken)
    {
        // First, and on every page. See the interface's remarks: a cursor says where the last page ended, not that
        // the recipe is still readable.
        if (!await recipes.ExistsAsync(criteria.RecipeId, cancellationToken))
        {
            return null;
        }

        // The page first, so a caller that asked for the counts still gets their rows from the cheaper statement if
        // a count is what fails. Skipped entirely when unwanted: two unasked-for aggregates are two queries nobody
        // reads. The same ordering, for the same reason, as RecipeDataLayer.SearchAsync.
        var (rows, hasMore) = await history.ListAsync(criteria, cancellationToken);

        var counts = criteria.IncludeSummary
            ? await history.CountAsync(criteria, cancellationToken)
            : null;

        return new TestRunHistoryPage(rows, hasMore, counts);
    }
}

/// <summary>Whether an edit to a test run committed, or was refused because the run had moved on.</summary>
/// <remarks>
/// A named outcome rather than a bare bool, so a caller cannot get the polarity backwards at a call site where
/// <c>true</c> could plausibly mean either thing — the same reason <c>RecipeUpdateOutcome</c> exists.
/// </remarks>
public sealed record TestRunUpdateOutcome
{
    private TestRunUpdateOutcome(bool succeeded) => Succeeded = succeeded;

    public bool Succeeded { get; }

    public static TestRunUpdateOutcome Applied() => new(true);

    public static TestRunUpdateOutcome Conflict() => new(false);
}

/// <summary>Whether a resolution committed, or was refused because the issue had just been resolved.</summary>
public sealed class TestIssueResolutionOutcome
{
    private TestIssueResolutionOutcome(TestIssueResolution? resolution) => Resolution = resolution;

    /// <summary>The written resolution, or null when the issue was already resolved.</summary>
    public TestIssueResolution? Resolution { get; }

    public bool Succeeded => Resolution is not null;

    public static TestIssueResolutionOutcome Applied(TestIssueResolution resolution) => new(resolution);

    public static TestIssueResolutionOutcome AlreadyResolved() => new(null);
}
