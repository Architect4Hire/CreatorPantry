using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Recipes.Data;

/// <summary>
/// The purpose-built paged query behind one recipe's test history. Persistence only — no domain decisions, no
/// caching, and never <c>IgnoreQueryFilters</c>: workspace scope comes from the global query filter, so another
/// workspace's tests are simply not there to be filtered, counted or paged past.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Separate from <see cref="IRecipeTestRunRepository"/> on purpose</strong>, and for the reason
/// <see cref="IRecipeSearchRepository"/> is separate from <see cref="IRecipeRepository"/>. That interface is EF
/// Core access for the test-run <em>aggregate</em>: its reads return a whole run with its observations, issues and
/// resolutions tracked, because its callers are about to change one. Nothing here materialises an aggregate or
/// tracks a row — it projects summaries and counts in SQL. Putting this on that interface would make "returns the
/// aggregate, tracked" stop being true of it.
/// </para>
/// <para>
/// <strong>No cursor is minted here.</strong> A cursor is bound to the recipe and filters it was issued for, and a
/// repository is handed a predicate rather than a route — so this reports only whether another page follows, and
/// Business turns that into a cursor with <c>PageBuilder</c>.
/// </para>
/// </remarks>
public interface IRecipeTestRunHistoryRepository
{
    /// <summary>
    /// Reads one page of test summaries for the criteria's recipe, most recently cooked first.
    /// </summary>
    /// <returns>
    /// The page's rows, and whether another page follows. An empty page with no more rows is the honest answer for
    /// a recipe nobody has tested and for filters nothing matched alike — neither is an error, and this does not
    /// know whether the recipe exists.
    /// </returns>
    /// <remarks>
    /// Fetches one row beyond the limit and discards it, which is how "does another page follow" is answered
    /// without a second <c>COUNT</c> against a set that may have changed in between, and without ever promising a
    /// further page that turns out not to exist.
    /// </remarks>
    Task<(IReadOnlyList<TestRunHistoryRecord> Rows, bool HasMore)> ListAsync(
        TestRunHistoryCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// Counts the whole filtered set, broken down by verdict and by outstanding issues.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A second method rather than fields on the page, so a caller that does not want the figures does not pay for
    /// them. Both methods are built from the same private filter, which is what stops the counted set and the
    /// paged set from drifting apart — a summary that described a different set than it paged would be worse than
    /// no summary at all.
    /// </para>
    /// <para>
    /// <see cref="TestRunHistoryCriteria.Position"/> and <see cref="TestRunHistoryCriteria.Limit"/> are
    /// deliberately ignored. Applying the cursor would count what remains after the current page, which reads like
    /// a total and is not one.
    /// </para>
    /// <para>
    /// Being separate statements, these can disagree with the rows of a page read beside them by however many
    /// tests were recorded in between — the accepted cost <see cref="TestRunHistorySummaryServiceModel"/> states.
    /// </para>
    /// </remarks>
    Task<TestRunHistoryCounts> CountAsync(
        TestRunHistoryCriteria criteria,
        CancellationToken cancellationToken);
}

internal sealed class RecipeTestRunHistoryRepository(CreatorPantryDbContext context) : IRecipeTestRunHistoryRepository
{
    public async Task<(IReadOnlyList<TestRunHistoryRecord> Rows, bool HasMore)> ListAsync(
        TestRunHistoryCriteria criteria,
        CancellationToken cancellationToken)
    {
        var fetched = await Project(Ordered(Positioned(Filtered(criteria), criteria), criteria))
            .ToListAsync(cancellationToken);

        return fetched.ToPage(criteria.Limit);
    }

    public async Task<TestRunHistoryCounts> CountAsync(
        TestRunHistoryCriteria criteria,
        CancellationToken cancellationToken)
    {
        // Filtered, and deliberately neither positioned nor limited: a count of what follows the current page is
        // not a total, and a count capped at the page size is not one either.
        var filtered = Filtered(criteria);

        // One statement for the verdicts. GROUP BY returns only the outcomes some run actually holds, which is why
        // the mapper fills the rest in with zero rather than this pretending to have counted them.
        var byOutcome = await filtered
            .GroupBy(run => run.Outcome)
            .Select(group => new { Outcome = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Outcome, row => row.Count, cancellationToken);

        // One statement for both issue figures, grouped by run so that "how many runs" and "how many issues" come
        // back together. Two separate counts would be two statements to answer one question, and they could
        // disagree with each other across a concurrent write in a way these two cannot.
        //
        // The row count here is the number of runs with something outstanding — tens at most — not the number of
        // issues, so nothing unbounded is materialised.
        var unresolvedByRun = await context.TestIssues
            .AsNoTracking()
            .Where(issue =>
                filtered.Any(run => run.Id == issue.RecipeTestRunId)
                && !context.TestIssueResolutions.Any(resolution => resolution.TestIssueId == issue.Id))
            .GroupBy(issue => issue.RecipeTestRunId)
            .Select(group => group.Count())
            .ToListAsync(cancellationToken);

        return new TestRunHistoryCounts(byOutcome, unresolvedByRun.Count, unresolvedByRun.Sum());
    }

    /// <summary>
    /// The recipe, every filter, and nothing else — no ordering, no keyset, no limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one place the filters are expressed, shared by the page and the counts. Two copies would be two
    /// predicates that had to stay identical, and the first time they diverged the product would report a summary
    /// for a set it had not paged.
    /// </para>
    /// <para>
    /// <strong>No <c>WorkspaceId</c> predicate anywhere below.</strong> The global query filter already scopes
    /// every table this touches — <c>RecipeTestRuns</c>, <c>RecipeVersions</c>, <c>TestObservations</c>,
    /// <c>TestIssues</c>, <c>TestIssueResolutions</c> and <c>TestAttachmentLinks</c> are all workspace-owned — and
    /// writing one by hand would suggest the filter is optional. That is also what makes the correlated subqueries
    /// safe: another workspace's issue or version is not merely filtered out of them, it is not visible to them.
    /// </para>
    /// <para>
    /// <strong>The recipe predicate is what makes a version number mean anything.</strong> A number is unique only
    /// within its recipe, so the version filter below is safe precisely because it is evaluated against runs
    /// already narrowed to one recipe.
    /// </para>
    /// </remarks>
    private IQueryable<RecipeTestRun> Filtered(TestRunHistoryCriteria criteria)
    {
        var filters = criteria.Filters;

        // Never tracked. Nothing on this path changes a run, and tracking a hundred of them with their
        // concurrency tokens would be a change tracker full of rows nobody intends to write.
        var runs = context.RecipeTestRuns
            .AsNoTracking()
            .Where(run => run.RecipeId == criteria.RecipeId);

        if (filters.VersionNumbers is { Count: > 0 } versionNumbers)
        {
            // By the version the run points at rather than by a number stored on the run, because no such number
            // is stored — and should not be, since it would be a second copy of the version's own identity. An
            // index seek of the RecipeVersions primary key per candidate row.
            runs = runs.Where(run => context.RecipeVersions.Any(version =>
                version.Id == run.RecipeVersionId && versionNumbers.Contains(version.VersionNumber)));
        }

        if (filters.TesterMembershipIds is { Count: > 0 } testerIds)
        {
            // Whoever cooked it, never whoever entered the record. Collapsing the two would credit the wrong
            // kitchen — see RecipeTestRun.TestedByMembershipId.
            runs = runs.Where(run => testerIds.Contains(run.TestedByMembershipId));
        }

        if (filters.Outcomes is { Count: > 0 } outcomes)
        {
            runs = runs.Where(run => outcomes.Contains(run.Outcome));
        }

        if (filters.TestedOnOrAfter is { } testedFrom)
        {
            runs = runs.Where(run => run.TestedAt >= testedFrom);
        }

        if (filters.TestedBefore is { } testedBefore)
        {
            runs = runs.Where(run => run.TestedAt < testedBefore);
        }

        if (filters.Issues is { } issues)
        {
            // Branched in C# rather than compared to a boolean parameter, so this stays EXISTS / NOT EXISTS and can
            // short-circuit on the first match instead of evaluating a CASE for every row. Written out twice
            // rather than factored into a helper for the reason RecipeSearchRepository gives: EF Core translates
            // the expression tree it is given and does not inline a method call, so a shared
            // `HasUnresolved(run.Id)` would compile and then fail to translate at runtime.
            //
            // "Unresolved" is the absence of a resolution row and nothing else, which is the one meaning
            // TestIssue establishes. There is no flag to disagree with.
            runs = issues switch
            {
                TestRunIssueFilter.HasUnresolved => runs.Where(run => context.TestIssues.Any(issue =>
                    issue.RecipeTestRunId == run.Id
                    && !context.TestIssueResolutions.Any(resolution => resolution.TestIssueId == issue.Id))),

                TestRunIssueFilter.AllResolved => runs.Where(run => !context.TestIssues.Any(issue =>
                    issue.RecipeTestRunId == run.Id
                    && !context.TestIssueResolutions.Any(resolution => resolution.TestIssueId == issue.Id))),

                _ => throw new ArgumentOutOfRangeException(
                    nameof(criteria), issues, "Unknown test-run issue filter."),
            };
        }

        return runs;
    }

    /// <summary>
    /// Resumes after the last row of the previous page.
    /// </summary>
    /// <remarks>
    /// The predicate and the <c>ORDER BY</c> in <see cref="Ordered"/> must name the same two columns in the same
    /// two directions. That is the whole correctness condition of a keyset: if they disagree by one column or one
    /// direction, paging silently repeats or skips rows rather than failing.
    /// </remarks>
    private static IQueryable<RecipeTestRun> Positioned(
        IQueryable<RecipeTestRun> runs,
        TestRunHistoryCriteria criteria) =>
        criteria.Position is not { } position
            ? runs
            : runs.Where(run =>
                run.TestedAt < position.TestedAt
                || (run.TestedAt == position.TestedAt && run.Id.CompareTo(position.TestRunId) < 0));

    /// <summary>
    /// Orders by tested time, breaks every tie by id, and fetches one row more than asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tie-break is not decoration. Two bakes recorded at the same time — the same morning's pair, or a batch
    /// of notes written up with one timestamp — would otherwise have no defined order between them, and a page
    /// boundary falling between them would repeat one and skip the other on every read.
    /// </para>
    /// <para>
    /// <c>IX_RecipeTestRuns_Workspace_Recipe_TestedAt</c> has exactly these columns in exactly these directions
    /// after the workspace and the recipe, so this is a seek and a scan along the index rather than a sort. That
    /// index was built for this route by the entity change it shipped with; the ordering is why it is descending.
    /// </para>
    /// </remarks>
    private static IQueryable<RecipeTestRun> Ordered(
        IQueryable<RecipeTestRun> runs,
        TestRunHistoryCriteria criteria) =>
        runs
            .OrderByDescending(run => run.TestedAt)
            .ThenByDescending(run => run.Id)
            // One row past the limit. That row is never returned — it is how the page learns whether another
            // follows, without a second COUNT against a set that may have changed in between.
            .Take(criteria.Limit + 1);

    /// <summary>
    /// Projects summaries in SQL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nothing here reads an attachment or a snapshot.</strong> <c>TestAttachmentLinks</c> appears once, as
    /// a <c>COUNT</c>; <c>RecipeVersionSnapshots</c> does not appear at all. No child collection is loaded and no
    /// observation or issue text is fetched — the three counts are aggregates, so a run with fifty observations
    /// costs three index seeks rather than fifty rows.
    /// </para>
    /// <para>
    /// The version number is a scalar subquery on the primary key rather than a join, which keeps the page size
    /// exact: a join through a collection would multiply rows and corrupt both the limit and every cursor after
    /// it. It is <c>FirstOrDefault</c> on a required, composite-foreign-key-constrained column, so the zero it
    /// could theoretically return is unreachable — the alternative, a nullable number, would model an impossible
    /// state and push handling it up two layers.
    /// </para>
    /// </remarks>
    private IQueryable<TestRunHistoryRecord> Project(IQueryable<RecipeTestRun> runs) =>
        runs.Select(run => new TestRunHistoryRecord(
            run.Id,
            run.RecipeVersionId,
            context.RecipeVersions
                .Where(version => version.Id == run.RecipeVersionId)
                .Select(version => version.VersionNumber)
                .FirstOrDefault(),
            run.TestedAt,
            run.TestedByMembershipId,
            run.Outcome,
            run.Rating,
            run.SummaryNotes,
            run.ActualYieldText,
            run.ActualYieldQuantity,
            run.ActualYieldUnitId,
            run.ActualPrepTimeMinutes,
            run.ActualCookTimeMinutes,
            run.ActualRestTimeMinutes,
            run.ActualTotalTimeMinutes,
            context.TestObservations.Count(observation => observation.RecipeTestRunId == run.Id),
            context.TestIssues.Count(issue => issue.RecipeTestRunId == run.Id),
            context.TestIssues.Count(issue =>
                issue.RecipeTestRunId == run.Id
                && !context.TestIssueResolutions.Any(resolution => resolution.TestIssueId == issue.Id)),
            context.TestAttachmentLinks.Count(link => link.RecipeTestRunId == run.Id),
            run.CreatedAt,
            run.UpdatedAt,
            run.RowVersion));
}
