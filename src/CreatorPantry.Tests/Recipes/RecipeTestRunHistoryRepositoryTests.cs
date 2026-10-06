using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// The test-history query against a real SQL Server. These are the tests that prove the keyset predicate, the
/// correlated subqueries and the filters actually translate and actually agree with the <c>ORDER BY</c> — a query
/// that compiles in C# and throws, or silently repeats a row, would pass every test above this layer.
/// </summary>
/// <remarks>
/// Each test starts from empty test-run and recipe tables, which is what lets them assert totals rather than only
/// the presence of a known id. Deleting is done in SQL because <c>RecipeVersion</c> and
/// <c>TestIssueResolution</c> are immutable records and <c>ImmutableRecordInterceptor</c> refuses to delete one
/// through the change tracker — correctly, and the alternative would be a test that could not clean up after
/// itself.
/// </remarks>
public sealed class RecipeTestRunHistoryRepositoryTests(SqlServerRecipeFixture fixture)
    : IClassFixture<SqlServerRecipeFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SqlServerRecipeFixture.Now;

    /// <summary>
    /// Stands in for the real cursor scope. Any string does here, because the repository never reads it — a cursor
    /// is bound to a resource and filters a repository is not told about, so minting one is Business's job. These
    /// tests only need the scope they encode a position under to match the one they decode it with, which
    /// <see cref="PositionFrom"/> guarantees by using this same value.
    /// </summary>
    private const string TestScope = "t";

    private Guid _recipeId;

    private readonly Dictionary<int, Guid> _versionIdsByNumber = [];

    public async ValueTask InitializeAsync()
    {
        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        // Resolutions and issues first, then runs, then the versions their foreign keys point at: a version's
        // reference to its recipe is Restrict, which is exactly the constraint that makes history outlive an edit.
        await SqlServerRecipeFixture.Db(scope).Database.ExecuteSqlRawAsync(
            """
            DELETE FROM TestIssueResolutions;
            DELETE FROM TestIssues;
            DELETE FROM TestObservations;
            DELETE FROM TestAttachmentLinks;
            DELETE FROM RecipeTestRuns;
            DELETE FROM RecipeVersionSnapshots;
            DELETE FROM RecipeVersions;
            DELETE FROM Recipes;
            """,
            TestContext.Current.CancellationToken);

        // One recipe with three versions, in workspace A. Every test here is about the tests of one recipe, so the
        // recipe itself is fixture rather than subject.
        (_recipeId, var versions) = await SeedRecipeAsync("Olive oil cake", versionCount: 3);

        _versionIdsByNumber.Clear();
        foreach (var (number, id) in versions)
        {
            _versionIdsByNumber[number] = id;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // --- Filters ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_unfiltered_read_returns_every_test_of_the_recipe()
    {
        await AddRunAsync(versionNumber: 1);
        await AddRunAsync(versionNumber: 2);

        Assert.Equal(2, (await ListAsync(new TestRunHistoryFilters())).Count);
    }

    /// <summary>
    /// The read is scoped to one recipe, so another recipe's tests are absent even though they sit in the same
    /// table in the same workspace. Without the recipe predicate this route would list the whole workspace's
    /// testing under whichever recipe was asked about.
    /// </summary>
    [Fact]
    public async Task Another_recipes_tests_are_not_in_this_recipes_history()
    {
        await AddRunAsync(versionNumber: 1);

        var (otherRecipeId, otherVersions) = await SeedRecipeAsync("Weeknight chilli", versionCount: 1);
        await AddRunAsync(versionNumber: 1, recipeId: otherRecipeId, versionId: otherVersions[1]);

        Assert.Single(await ListAsync(new TestRunHistoryFilters()));
        Assert.Equal(1, (await CountAsync(new TestRunHistoryFilters())).OutcomeCounts.Values.Sum());
    }

    [Fact]
    public async Task A_version_filter_includes_only_tests_of_the_named_versions()
    {
        await AddRunAsync(versionNumber: 1, summary: "first");
        await AddRunAsync(versionNumber: 2, summary: "second");
        await AddRunAsync(versionNumber: 3, summary: "third");

        var rows = await ListAsync(new TestRunHistoryFilters(VersionNumbers: [1, 3]));

        Assert.Equal(["first", "third"], rows.Select(row => row.SummaryNotes).Order());
    }

    /// <summary>
    /// A version number is unique only within its recipe, and the recipe predicate is what makes the filter mean
    /// anything. Another recipe's version 1 must not be reachable by asking this recipe for version 1.
    /// </summary>
    [Fact]
    public async Task A_version_filter_cannot_reach_another_recipes_identically_numbered_version()
    {
        var (otherRecipeId, otherVersions) = await SeedRecipeAsync("Weeknight chilli", versionCount: 1);
        await AddRunAsync(versionNumber: 1, recipeId: otherRecipeId, versionId: otherVersions[1], summary: "other");

        Assert.Empty(await ListAsync(new TestRunHistoryFilters(VersionNumbers: [1])));
    }

    [Fact]
    public async Task A_tester_filter_reads_who_cooked_it_not_who_entered_it()
    {
        await AddRunAsync(
            versionNumber: 1,
            testerId: SqlServerRecipeFixture.AuthorOne,
            enteredById: SqlServerRecipeFixture.AuthorTwo,
            summary: "cooked by one");
        await AddRunAsync(versionNumber: 1, testerId: SqlServerRecipeFixture.AuthorTwo, summary: "cooked by two");

        var rows = await ListAsync(
            new TestRunHistoryFilters(TesterMembershipIds: [SqlServerRecipeFixture.AuthorOne]));

        Assert.Equal("cooked by one", Assert.Single(rows).SummaryNotes);
    }

    [Fact]
    public async Task An_outcome_filter_includes_only_the_named_verdicts()
    {
        await AddRunAsync(versionNumber: 1, outcome: TestRunOutcome.Succeeded, summary: "ok");
        await AddRunAsync(versionNumber: 1, outcome: TestRunOutcome.Failed, summary: "bad");
        await AddRunAsync(versionNumber: 1, outcome: TestRunOutcome.NotStated, summary: "silent");

        var rows = await ListAsync(new TestRunHistoryFilters(
            Outcomes: [TestRunOutcome.Succeeded, TestRunOutcome.NotStated]));

        Assert.Equal(["ok", "silent"], rows.Select(row => row.SummaryNotes).Order());
    }

    /// <summary>
    /// Half-open bounds: inclusive below, exclusive above, so adjacent ranges neither overlap nor leave a gap and a
    /// caller does not have to know the precision the column stores.
    /// </summary>
    [Fact]
    public async Task Date_bounds_are_inclusive_below_and_exclusive_above()
    {
        await AddRunAsync(versionNumber: 1, testedAt: Now.AddDays(-1), summary: "before");
        await AddRunAsync(versionNumber: 1, testedAt: Now, summary: "on the lower bound");
        await AddRunAsync(versionNumber: 1, testedAt: Now.AddDays(1), summary: "on the upper bound");

        var rows = await ListAsync(new TestRunHistoryFilters(
            TestedOnOrAfter: Now, TestedBefore: Now.AddDays(1)));

        Assert.Equal("on the lower bound", Assert.Single(rows).SummaryNotes);
    }

    /// <summary>
    /// The dates bound when the cooking happened, never when the record was written. A tester who writes a bake up
    /// a week late must still find it under the day they cooked it.
    /// </summary>
    [Fact]
    public async Task Date_bounds_read_the_tested_time_and_not_the_created_time()
    {
        await AddRunAsync(
            versionNumber: 1, testedAt: Now, createdAt: Now.AddDays(7), summary: "written up a week later");

        Assert.Single(await ListAsync(new TestRunHistoryFilters(
            TestedOnOrAfter: Now, TestedBefore: Now.AddDays(1))));
    }

    [Fact]
    public async Task An_unresolved_filter_finds_only_tests_with_something_still_open()
    {
        await AddRunAsync(versionNumber: 1, summary: "nothing wrong");
        await AddRunAsync(versionNumber: 1, summary: "one open", issues: [false]);
        await AddRunAsync(versionNumber: 1, summary: "all dealt with", issues: [true, true]);
        await AddRunAsync(versionNumber: 1, summary: "one of two open", issues: [true, false]);

        var rows = await ListAsync(new TestRunHistoryFilters(Issues: TestRunIssueFilter.HasUnresolved));

        Assert.Equal(["one of two open", "one open"], rows.Select(row => row.SummaryNotes).Order());
    }

    /// <summary>
    /// A test that raised no issues at all has nothing left to decide, so it counts as fully resolved. Vacuous, and
    /// the honest answer: the alternative would report an open problem the test does not have.
    /// </summary>
    [Fact]
    public async Task A_test_that_raised_no_issues_counts_as_fully_resolved()
    {
        await AddRunAsync(versionNumber: 1, summary: "nothing wrong");
        await AddRunAsync(versionNumber: 1, summary: "all dealt with", issues: [true]);
        await AddRunAsync(versionNumber: 1, summary: "one open", issues: [false]);

        var rows = await ListAsync(new TestRunHistoryFilters(Issues: TestRunIssueFilter.AllResolved));

        Assert.Equal(["all dealt with", "nothing wrong"], rows.Select(row => row.SummaryNotes).Order());
    }

    [Fact]
    public async Task Filters_from_different_dimensions_narrow_together()
    {
        await AddRunAsync(
            versionNumber: 2,
            testerId: SqlServerRecipeFixture.AuthorOne,
            outcome: TestRunOutcome.Failed,
            testedAt: Now,
            issues: [false],
            summary: "everything");

        // Each of these differs from the row above in exactly one dimension, so a filter silently dropped from the
        // query shows up as one of them arriving in the result.
        await AddRunAsync(versionNumber: 1, testerId: SqlServerRecipeFixture.AuthorOne, outcome: TestRunOutcome.Failed, testedAt: Now, issues: [false], summary: "wrong version");
        await AddRunAsync(versionNumber: 2, testerId: SqlServerRecipeFixture.AuthorTwo, outcome: TestRunOutcome.Failed, testedAt: Now, issues: [false], summary: "wrong tester");
        await AddRunAsync(versionNumber: 2, testerId: SqlServerRecipeFixture.AuthorOne, outcome: TestRunOutcome.Succeeded, testedAt: Now, issues: [false], summary: "wrong outcome");
        await AddRunAsync(versionNumber: 2, testerId: SqlServerRecipeFixture.AuthorOne, outcome: TestRunOutcome.Failed, testedAt: Now.AddDays(-5), issues: [false], summary: "outside the range");
        await AddRunAsync(versionNumber: 2, testerId: SqlServerRecipeFixture.AuthorOne, outcome: TestRunOutcome.Failed, testedAt: Now, issues: [true], summary: "nothing open");

        var rows = await ListAsync(new TestRunHistoryFilters(
            VersionNumbers: [2],
            TesterMembershipIds: [SqlServerRecipeFixture.AuthorOne],
            Outcomes: [TestRunOutcome.Failed],
            TestedOnOrAfter: Now,
            TestedBefore: Now.AddDays(1),
            Issues: TestRunIssueFilter.HasUnresolved));

        Assert.Equal("everything", Assert.Single(rows).SummaryNotes);
    }

    // --- Ordering --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_order_is_most_recently_cooked_first()
    {
        await AddRunAsync(versionNumber: 1, testedAt: Now.AddDays(-2), summary: "oldest");
        await AddRunAsync(versionNumber: 1, testedAt: Now, summary: "newest");
        await AddRunAsync(versionNumber: 1, testedAt: Now.AddDays(-1), summary: "middle");

        var rows = await ListAsync(new TestRunHistoryFilters());

        Assert.Equal(["newest", "middle", "oldest"], rows.Select(row => row.SummaryNotes));
    }

    /// <summary>
    /// Ordered by when the cooking happened, not by when the record was written. A history ordered by
    /// <c>CreatedAt</c> would be a history of typing.
    /// </summary>
    [Fact]
    public async Task The_order_reads_the_tested_time_and_not_the_created_time()
    {
        await AddRunAsync(versionNumber: 1, testedAt: Now, createdAt: Now.AddDays(10), summary: "cooked first");
        await AddRunAsync(versionNumber: 1, testedAt: Now.AddDays(1), createdAt: Now.AddDays(2), summary: "cooked second");

        var rows = await ListAsync(new TestRunHistoryFilters());

        Assert.Equal(["cooked second", "cooked first"], rows.Select(row => row.SummaryNotes));
    }

    /// <summary>
    /// The property the whole keyset depends on: the ordering is total, so walking it one row at a time visits
    /// exactly the same rows in exactly the same order as reading it in one page.
    /// </summary>
    /// <remarks>
    /// Every row here shares one <c>TestedAt</c>, so only the tie-break separates them — deliberately the hardest
    /// case. If the <c>WHERE</c> and the <c>ORDER BY</c> disagreed about it, by a column, a direction, or by the
    /// database ordering GUIDs differently than C# does, a row would be repeated or skipped here and nowhere else.
    /// The expected order is never written down in C#, because what matters is that the two agree with each other.
    /// </remarks>
    [Fact]
    public async Task Tests_that_tie_on_the_tested_time_still_have_one_total_order()
    {
        for (var index = 0; index < 5; index++)
        {
            await AddRunAsync(versionNumber: 1, testedAt: Now);
        }

        var wholePage = await ListAsync(new TestRunHistoryFilters(), limit: 100);
        var oneAtATime = await DrainAsync(new TestRunHistoryFilters(), limit: 1);

        Assert.Equal(5, wholePage.Count);
        Assert.Equal(wholePage.Select(row => row.Id), oneAtATime.Select(row => row.Id));
    }

    // --- Paging ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Paging_to_the_end_yields_every_test_once_in_order()
    {
        for (var index = 0; index < 11; index++)
        {
            await AddRunAsync(versionNumber: 1, testedAt: Now.AddMinutes(index), summary: $"test {index:00}");
        }

        var all = await DrainAsync(new TestRunHistoryFilters(), limit: 4);

        Assert.Equal(11, all.Count);
        Assert.Equal(11, all.Select(row => row.Id).Distinct().Count());
        Assert.Equal(
            all.Select(row => row.SummaryNotes).OrderDescending(StringComparer.Ordinal),
            all.Select(row => row.SummaryNotes));
    }

    /// <summary>
    /// Paging still terminates and still visits every row when the cursor's own filter is the expensive one. A
    /// keyset combined with a <c>NOT EXISTS</c> is where a predicate that disagreed with the ordering would show up.
    /// </summary>
    [Fact]
    public async Task Paging_a_filtered_history_yields_every_matching_test_once()
    {
        for (var index = 0; index < 7; index++)
        {
            await AddRunAsync(
                versionNumber: 1,
                testedAt: Now.AddMinutes(index),
                issues: index % 2 == 0 ? [false] : [true],
                summary: $"test {index:00}");
        }

        var all = await DrainAsync(new TestRunHistoryFilters(Issues: TestRunIssueFilter.HasUnresolved), limit: 2);

        Assert.Equal(4, all.Count);
        Assert.Equal(4, all.Select(row => row.Id).Distinct().Count());
    }

    [Fact]
    public async Task The_last_page_has_no_next_position()
    {
        await AddRunAsync(versionNumber: 1);

        var (rows, hasMore) = await PageAsync(new TestRunHistoryFilters(), limit: 25);

        Assert.Single(rows);
        Assert.False(hasMore);
    }

    /// <summary>
    /// A page that exactly fills the limit still reports nothing following. The probe row is what makes that
    /// distinguishable — without it a full page is ambiguous, and a client would ask for one page too many.
    /// </summary>
    [Fact]
    public async Task An_exactly_full_final_page_reports_nothing_following()
    {
        for (var index = 0; index < 3; index++)
        {
            await AddRunAsync(versionNumber: 1, testedAt: Now.AddMinutes(-index));
        }

        var (rows, hasMore) = await PageAsync(new TestRunHistoryFilters(), limit: 3);

        Assert.Equal(3, rows.Count);
        Assert.False(hasMore);
    }

    /// <summary>
    /// The page size is clamped by the criteria itself, so no caller — a controller, a worker, an AI plugin — can
    /// ask the repository for an unbounded read.
    /// </summary>
    [Fact]
    public async Task A_page_size_clamped_up_from_zero_still_pages_to_the_end()
    {
        for (var index = 0; index < 4; index++)
        {
            await AddRunAsync(versionNumber: 1, testedAt: Now.AddMinutes(-index));
        }

        Assert.Equal(4, (await DrainAsync(new TestRunHistoryFilters(), limit: 0)).Count);
    }

    // --- Counting --------------------------------------------------------------------------------------------

    /// <summary>
    /// The counts and the page are built from one filter, so they cannot describe different sets. This is the
    /// assertion that catches them drifting apart — the failure that would make a summary worse than none.
    /// </summary>
    [Fact]
    public async Task The_total_agrees_with_a_full_drain_under_every_filter()
    {
        await AddRunAsync(versionNumber: 1, outcome: TestRunOutcome.Succeeded, testerId: SqlServerRecipeFixture.AuthorOne, testedAt: Now);
        await AddRunAsync(versionNumber: 2, outcome: TestRunOutcome.Failed, testerId: SqlServerRecipeFixture.AuthorTwo, testedAt: Now.AddDays(-1), issues: [false]);
        await AddRunAsync(versionNumber: 2, outcome: TestRunOutcome.SucceededWithIssues, testerId: SqlServerRecipeFixture.AuthorOne, testedAt: Now.AddDays(-2), issues: [true, false]);
        await AddRunAsync(versionNumber: 3, outcome: TestRunOutcome.NotStated, testerId: SqlServerRecipeFixture.AuthorTwo, testedAt: Now.AddYears(-1));

        TestRunHistoryFilters[] cases =
        [
            new(),
            new(VersionNumbers: [2]),
            new(VersionNumbers: [1, 3]),
            new(TesterMembershipIds: [SqlServerRecipeFixture.AuthorOne]),
            new(Outcomes: [TestRunOutcome.Failed, TestRunOutcome.SucceededWithIssues]),
            new(TestedOnOrAfter: Now.AddDays(-2)),
            new(TestedBefore: Now),
            new(Issues: TestRunIssueFilter.HasUnresolved),
            new(Issues: TestRunIssueFilter.AllResolved),
            new(VersionNumbers: [2], Issues: TestRunIssueFilter.HasUnresolved),
        ];

        foreach (var filters in cases)
        {
            var drained = await DrainAsync(filters, limit: 2);
            var counts = await CountAsync(filters);

            Assert.Equal(drained.Count, counts.OutcomeCounts.Values.Sum());
        }
    }

    [Fact]
    public async Task The_breakdown_counts_each_verdict_and_omits_the_ones_nothing_holds()
    {
        await AddRunAsync(versionNumber: 1, outcome: TestRunOutcome.Succeeded);
        await AddRunAsync(versionNumber: 1, outcome: TestRunOutcome.Succeeded);
        await AddRunAsync(versionNumber: 1, outcome: TestRunOutcome.Failed);

        var counts = await CountAsync(new TestRunHistoryFilters());

        Assert.Equal(2, counts.OutcomeCounts[TestRunOutcome.Succeeded]);
        Assert.Equal(1, counts.OutcomeCounts[TestRunOutcome.Failed]);

        // A GROUP BY cannot invent a row for a verdict nothing holds. Zero-filling is the published model's job,
        // and pretending otherwise here would have this record claim knowledge the statement did not return.
        Assert.False(counts.OutcomeCounts.ContainsKey(TestRunOutcome.NotStated));
    }

    /// <summary>
    /// Two different numbers, and the reason both are published: one bake with three outstanding problems and three
    /// bakes with one each are identical on the left and very different situations.
    /// </summary>
    [Fact]
    public async Task The_unresolved_counts_distinguish_tests_from_issues()
    {
        await AddRunAsync(versionNumber: 1, issues: [false, false, false]);
        await AddRunAsync(versionNumber: 1, issues: [true, false]);
        await AddRunAsync(versionNumber: 1, issues: [true]);
        await AddRunAsync(versionNumber: 1);

        var counts = await CountAsync(new TestRunHistoryFilters());

        Assert.Equal(2, counts.RunsWithUnresolvedIssues);
        Assert.Equal(4, counts.UnresolvedIssueCount);
    }

    [Fact]
    public async Task A_history_with_nothing_open_counts_zero_rather_than_reporting_nothing()
    {
        await AddRunAsync(versionNumber: 1, issues: [true]);

        var counts = await CountAsync(new TestRunHistoryFilters());

        Assert.Equal(0, counts.RunsWithUnresolvedIssues);
        Assert.Equal(0, counts.UnresolvedIssueCount);
    }

    /// <summary>
    /// A total is a total: it counts the whole filtered set, not what is left after the current page, and not what
    /// would fit on one.
    /// </summary>
    [Fact]
    public async Task The_counts_ignore_the_position_and_the_page_size()
    {
        for (var index = 0; index < 6; index++)
        {
            await AddRunAsync(versionNumber: 1, testedAt: Now.AddMinutes(-index), issues: [false]);
        }

        var (rows, _) = await PageAsync(new TestRunHistoryFilters(), limit: 2);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceA);

        var counts = await History(scope).CountAsync(
            Criteria(new TestRunHistoryFilters(), PositionFrom(rows[^1]), limit: 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(6, counts.OutcomeCounts.Values.Sum());
        Assert.Equal(6, counts.RunsWithUnresolvedIssues);
    }

    // --- Projection ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_row_names_the_version_that_was_cooked_by_number_as_well_as_by_id()
    {
        await AddRunAsync(versionNumber: 3);

        var row = Assert.Single(await ListAsync(new TestRunHistoryFilters()));

        Assert.Equal(3, row.VersionNumber);
        Assert.Equal(_versionIdsByNumber[3], row.RecipeVersionId);
    }

    [Fact]
    public async Task A_row_counts_what_the_test_holds_rather_than_carrying_it()
    {
        await AddRunAsync(versionNumber: 1, observations: 4, issues: [true, false, false], attachments: 2);

        var row = Assert.Single(await ListAsync(new TestRunHistoryFilters()));

        Assert.Equal(4, row.ObservationCount);
        Assert.Equal(3, row.IssueCount);
        Assert.Equal(2, row.UnresolvedIssueCount);
        Assert.Equal(2, row.AttachmentCount);
    }

    /// <summary>
    /// A test with many children still occupies one row. Nothing in the projection joins a collection — a join
    /// instead of an aggregate would multiply rows and corrupt the page size and every cursor after it.
    /// </summary>
    [Fact]
    public async Task A_test_with_many_children_is_one_row()
    {
        await AddRunAsync(versionNumber: 1, observations: 6, issues: [false, false, true], attachments: 4);

        Assert.Single(await ListAsync(new TestRunHistoryFilters()));
        Assert.Equal(1, (await CountAsync(new TestRunHistoryFilters())).OutcomeCounts.Values.Sum());
    }

    /// <summary>
    /// The unresolved count is on every row, not only when it is filtered on, because a creator cannot act on a
    /// filter whose result they cannot see.
    /// </summary>
    [Fact]
    public async Task A_row_reports_open_issues_without_being_asked_to_filter_on_them()
    {
        await AddRunAsync(versionNumber: 1, testedAt: Now, issues: [true]);
        await AddRunAsync(versionNumber: 1, testedAt: Now.AddMinutes(-1), issues: [false, false]);

        var rows = await ListAsync(new TestRunHistoryFilters());

        Assert.Equal(0, rows[0].UnresolvedIssueCount);
        Assert.Equal(2, rows[1].UnresolvedIssueCount);
    }

    [Fact]
    public async Task A_row_carries_the_concurrency_token_the_next_edit_must_quote()
    {
        await AddRunAsync(versionNumber: 1);

        var row = Assert.Single(await ListAsync(new TestRunHistoryFilters()));

        // Server-generated, and eight bytes, which is what RecipeConcurrencyToken will accept back.
        Assert.Equal(RecipeConcurrencyToken.ByteLength, row.RowVersion.Length);
    }

    // --- Workspace isolation ---------------------------------------------------------------------------------

    /// <summary>
    /// Both workspaces hold a recipe with the same title, tested on the same day by the same membership id, so
    /// isolation cannot pass by the two being distinguishable. Another workspace's test is not filtered out of the
    /// answer — it is not visible to the query at all, which is what lets the seam above answer 404 without
    /// disclosing that it is real.
    /// </summary>
    [Fact]
    public async Task Each_workspace_lists_and_counts_only_its_own_tests()
    {
        await AddRunAsync(versionNumber: 1, testerId: SqlServerRecipeFixture.AuthorOne, summary: "shared");

        var (inB, versionsInB) = await SeedRecipeAsync(
            "Olive oil cake", versionCount: 1, workspaceId: SqlServerRecipeFixture.WorkspaceB);

        await AddRunAsync(
            versionNumber: 1,
            recipeId: inB,
            versionId: versionsInB[1],
            testerId: SqlServerRecipeFixture.AuthorOne,
            summary: "shared",
            workspaceId: SqlServerRecipeFixture.WorkspaceB);
        await AddRunAsync(
            versionNumber: 1,
            recipeId: inB,
            versionId: versionsInB[1],
            summary: "also in B",
            workspaceId: SqlServerRecipeFixture.WorkspaceB);

        var fromA = await ListAsync(new TestRunHistoryFilters());
        var fromB = await ListAsync(new TestRunHistoryFilters(), recipeId: inB, workspaceId: SqlServerRecipeFixture.WorkspaceB);

        Assert.Single(fromA);
        Assert.Equal(2, fromB.Count);
        Assert.Empty(fromA.Select(row => row.Id).Intersect(fromB.Select(row => row.Id)));

        Assert.Equal(1, (await CountAsync(new TestRunHistoryFilters())).OutcomeCounts.Values.Sum());
        Assert.Equal(
            2,
            (await CountAsync(new TestRunHistoryFilters(), inB, SqlServerRecipeFixture.WorkspaceB))
                .OutcomeCounts.Values.Sum());
    }

    /// <summary>
    /// Asking workspace B for workspace A's recipe finds nothing at all — not its tests, and not an error naming it.
    /// The repository cannot see the recipe, which is why the DataLayer's existence check above is the thing that
    /// turns this into a 404 rather than an empty page.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_recipe_id_lists_nothing()
    {
        await AddRunAsync(versionNumber: 1);

        var fromB = await ListAsync(
            new TestRunHistoryFilters(), recipeId: _recipeId, workspaceId: SqlServerRecipeFixture.WorkspaceB);

        Assert.Empty(fromB);
    }

    /// <summary>
    /// Both workspaces have a tester with the same membership id — which cannot happen through the routes, and is
    /// the point: filtering by it from inside B must find only B's tests, so the filter is not what keeps the two
    /// apart. The query filter is.
    /// </summary>
    [Fact]
    public async Task A_tester_filter_cannot_reach_the_other_workspaces_tests()
    {
        await AddRunAsync(versionNumber: 1, testerId: SqlServerRecipeFixture.AuthorOne, summary: "in A");

        var (inB, versionsInB) = await SeedRecipeAsync(
            "Olive oil cake", versionCount: 1, workspaceId: SqlServerRecipeFixture.WorkspaceB);

        await AddRunAsync(
            versionNumber: 1,
            recipeId: inB,
            versionId: versionsInB[1],
            testerId: SqlServerRecipeFixture.AuthorOne,
            summary: "in B",
            workspaceId: SqlServerRecipeFixture.WorkspaceB);

        var fromB = await ListAsync(
            new TestRunHistoryFilters(TesterMembershipIds: [SqlServerRecipeFixture.AuthorOne]),
            recipeId: inB,
            workspaceId: SqlServerRecipeFixture.WorkspaceB);

        Assert.Equal("in B", Assert.Single(fromB).SummaryNotes);
    }

    /// <summary>
    /// A position minted while reading one workspace, replayed against the other, pages that other workspace's rows
    /// and never reaches across. The scope fingerprint is what turns this into a rejected cursor rather than a wrong
    /// page, and binding the workspace into that scope is the read seam's job — this is the evidence that the
    /// repository does not leak even when the check above it has been bypassed entirely.
    /// </summary>
    [Fact]
    public async Task A_position_from_one_workspace_pages_only_the_other_workspaces_tests()
    {
        await AddRunAsync(versionNumber: 1, testedAt: Now, summary: "A one");
        await AddRunAsync(versionNumber: 1, testedAt: Now.AddMinutes(-1), summary: "A two");

        var (inB, versionsInB) = await SeedRecipeAsync(
            "Olive oil cake", versionCount: 1, workspaceId: SqlServerRecipeFixture.WorkspaceB);

        for (var index = 0; index < 2; index++)
        {
            await AddRunAsync(
                versionNumber: 1,
                recipeId: inB,
                versionId: versionsInB[1],
                testedAt: Now.AddMinutes(-index),
                summary: $"B {index}",
                workspaceId: SqlServerRecipeFixture.WorkspaceB);
        }

        var (firstInA, _) = await PageAsync(new TestRunHistoryFilters(), limit: 1);

        await using var scope = fixture.ScopeFor(SqlServerRecipeFixture.WorkspaceB);

        var (rows, _) = await History(scope).ListAsync(
            Criteria(new TestRunHistoryFilters(), PositionFrom(firstInA[0]), recipeId: inB),
            TestContext.Current.CancellationToken);

        Assert.All(rows, row => Assert.StartsWith("B ", row.SummaryNotes));
    }

    // --- Helpers ---------------------------------------------------------------------------------------------

    private static IRecipeTestRunHistoryRepository History(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IRecipeTestRunHistoryRepository>();

    private TestRunHistoryCriteria Criteria(
        TestRunHistoryFilters filters,
        TestRunHistoryPosition? position = null,
        int limit = 25,
        Guid? recipeId = null) =>
        new(recipeId ?? _recipeId, filters, TestScope, position, limit);

    private async Task<IReadOnlyList<TestRunHistoryRecord>> ListAsync(
        TestRunHistoryFilters filters,
        int limit = 25,
        Guid? recipeId = null,
        Guid? workspaceId = null)
    {
        var (rows, _) = await PageAsync(filters, limit, position: null, recipeId, workspaceId);

        return rows;
    }

    private async Task<(IReadOnlyList<TestRunHistoryRecord> Rows, bool HasMore)> PageAsync(
        TestRunHistoryFilters filters,
        int limit,
        TestRunHistoryPosition? position = null,
        Guid? recipeId = null,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);

        return await History(scope).ListAsync(
            Criteria(filters, position, limit, recipeId), TestContext.Current.CancellationToken);
    }

    private async Task<TestRunHistoryCounts> CountAsync(
        TestRunHistoryFilters filters,
        Guid? recipeId = null,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);

        return await History(scope).CountAsync(
            Criteria(filters, recipeId: recipeId), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Follows every page to the end, guarding against a query that never terminates.
    /// </summary>
    /// <remarks>
    /// The position is rebuilt by encoding the last row into a real cursor and decoding it back, which is the round
    /// trip Business and the read seam actually perform. Building the position directly from the row would skip the
    /// wire format, and a sort value that could not survive it — a truncated timestamp, say — would then only fail
    /// in production.
    /// </remarks>
    private async Task<List<TestRunHistoryRecord>> DrainAsync(TestRunHistoryFilters filters, int limit)
    {
        var all = new List<TestRunHistoryRecord>();
        TestRunHistoryPosition? position = null;

        for (var safety = 0; safety < 100; safety++)
        {
            var (rows, hasMore) = await PageAsync(filters, limit, position);
            all.AddRange(rows);

            if (!hasMore)
            {
                return all;
            }

            Assert.NotEmpty(rows);
            position = PositionFrom(rows[^1]);
        }

        Assert.Fail("paging did not terminate");

        return all;
    }

    private static TestRunHistoryPosition PositionFrom(TestRunHistoryRecord row)
    {
        var encoded = ReferenceCursor.Encode(row.SortValue, row.TieBreaker, TestScope);

        Assert.True(ReferenceCursor.TryDecode(encoded, out var cursor));
        Assert.True(TestRunHistoryPosition.TryCreate(cursor!, out var position));

        return position!;
    }

    /// <summary>
    /// Seeds one recipe with the requested number of versions, numbered from one.
    /// </summary>
    /// <remarks>
    /// <c>WorkspaceId</c> is never set on anything — the ownership interceptor stamps it from the resolved context,
    /// and feature code assigning it is a defect.
    /// </remarks>
    private async Task<(Guid RecipeId, Dictionary<int, Guid> VersionIds)> SeedRecipeAsync(
        string title,
        int versionCount,
        Guid? workspaceId = null)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        var recipe = SqlServerRecipeFixture.NewRecipe(
            title,
            workspaceId == SqlServerRecipeFixture.WorkspaceB
                ? SqlServerRecipeFixture.TagIdB
                : SqlServerRecipeFixture.TagIdA,
            SqlServerRecipeFixture.MediaAssetIdFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA));

        db.Recipes.Add(recipe);

        var versionIds = new Dictionary<int, Guid>();

        for (var number = 1; number <= versionCount; number++)
        {
            var version = new RecipeVersion
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe.Id,
                VersionNumber = number,
                Source = RecipeVersionSource.CreatorEdit,
                Readiness = RecipeVersionReadiness.Draft,
                CreatedByMembershipId = SqlServerRecipeFixture.AuthorOne,
                CreatedAt = Now.AddDays(-versionCount + number),
                SnapshotSchemaVersion = 1,
            };

            db.RecipeVersions.Add(version);
            versionIds[number] = version.Id;
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        return (recipe.Id, versionIds);
    }

    /// <summary>
    /// Seeds one test run, and only the parts a test asked for. <paramref name="issues"/> is one entry per issue,
    /// <c>true</c> meaning it has been resolved.
    /// </summary>
    private async Task AddRunAsync(
        int versionNumber,
        Guid? recipeId = null,
        Guid? versionId = null,
        Guid? workspaceId = null,
        Guid? testerId = null,
        Guid? enteredById = null,
        TestRunOutcome outcome = TestRunOutcome.Succeeded,
        DateTimeOffset? testedAt = null,
        DateTimeOffset? createdAt = null,
        string? summary = null,
        int observations = 0,
        IReadOnlyList<bool>? issues = null,
        int attachments = 0)
    {
        await using var scope = fixture.ScopeFor(workspaceId ?? SqlServerRecipeFixture.WorkspaceA);
        var db = SqlServerRecipeFixture.Db(scope);

        var recipe = recipeId ?? _recipeId;
        var tester = testerId ?? SqlServerRecipeFixture.AuthorOne;

        var run = new RecipeTestRun
        {
            Id = Guid.NewGuid(),
            RecipeId = recipe,
            RecipeVersionId = versionId ?? _versionIdsByNumber[versionNumber],
            TestedAt = testedAt ?? Now,
            TestedByMembershipId = tester,
            Outcome = outcome,
            SummaryNotes = summary,
            CreatedByMembershipId = enteredById ?? tester,
            UpdatedByMembershipId = enteredById ?? tester,
            CreatedAt = createdAt ?? testedAt ?? Now,
            UpdatedAt = createdAt ?? testedAt ?? Now,
        };

        for (var index = 0; index < observations; index++)
        {
            run.Observations.Add(new TestObservation
            {
                Id = Guid.NewGuid(),
                RecipeTestRunId = run.Id,
                Kind = TestObservationKind.Texture,
                Text = $"observation {index}",
                SortOrder = index,
            });
        }

        for (var index = 0; index < attachments; index++)
        {
            run.Attachments.Add(new TestAttachmentLink
            {
                Id = Guid.NewGuid(),
                RecipeTestRunId = run.Id,
                MediaAssetId = SqlServerRecipeFixture.MediaAssetIdFor(
                    workspaceId ?? SqlServerRecipeFixture.WorkspaceA),
                SortOrder = index,
            });
        }

        var resolutions = new List<TestIssueResolution>();

        for (var index = 0; index < (issues?.Count ?? 0); index++)
        {
            var issue = new TestIssue
            {
                Id = Guid.NewGuid(),
                RecipeId = recipe,
                RecipeTestRunId = run.Id,
                Severity = TestIssueSeverity.Minor,
                Title = $"issue {index}",
                SortOrder = index,
            };

            run.Issues.Add(issue);

            if (issues![index])
            {
                resolutions.Add(new TestIssueResolution
                {
                    Id = Guid.NewGuid(),
                    RecipeId = recipe,
                    TestIssueId = issue.Id,
                    Kind = TestIssueResolutionKind.WontFix,
                    ResolvedByMembershipId = tester,
                    ResolvedAt = Now,
                });
            }
        }

        db.RecipeTestRuns.Add(run);
        db.TestIssueResolutions.AddRange(resolutions);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
    }
}
