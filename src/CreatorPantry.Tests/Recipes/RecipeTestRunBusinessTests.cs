using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Recipes.Business;
using CreatorPantry.Domain.Modules.Recipes.Data;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Recipes;

/// <summary>
/// What Business decides on a recorded test, against a recording DataLayer. No database: under test is the
/// graph it hands down, the order it refuses things in, and what it takes from the resolved context rather
/// than from the request.
/// </summary>
public sealed class RecipeTestRunBusinessTests
{
    private static readonly Guid ActorMembershipId = Guid.NewGuid();
    private static readonly Guid RecipeId = Guid.NewGuid();
    private static readonly Guid VersionId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingTestRunDataLayer _dataLayer = new();
    private readonly IRecipeTestRunBusiness _business;

    public RecipeTestRunBusinessTests() =>
        _business = new ServiceCollection()
            .AddSingleton<IRecipeTestRunDataLayer>(_dataLayer)
            .AddSingleton<IWorkspaceContext>(new StubTestRunWorkspaceContext(ActorMembershipId))
            .AddSingleton<IClock>(new StubTestRunClock(Now))
            .AddSingleton<IRecipeTestRunBusiness, RecipeTestRunBusiness>()
            .BuildServiceProvider()
            .GetRequiredService<IRecipeTestRunBusiness>();

    private static CanonicalCreateTestRun Request(
        IReadOnlyList<CanonicalTestObservation>? observations = null,
        IReadOnlyList<CanonicalTestIssue>? issues = null,
        DateTimeOffset? testedAt = null) =>
        new()
        {
            SourceVersionNumber = 2,
            TestedAt = testedAt ?? Now.AddDays(-1),
            Outcome = TestRunOutcome.SucceededWithIssues,
            Observations = observations ?? [],
            Issues = issues ?? [],
        };

    private Task<OperationResult<CreatedRecipeTestRunServiceModel>> CreateAsync(
        CanonicalCreateTestRun? request = null,
        MeasurementDimension? yieldUnitDimension = null) =>
        _business.CreateAsync(RecipeId, request ?? Request(), yieldUnitDimension, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_recipe_that_is_not_visible_is_not_found()
    {
        // Null from the DataLayer covers both "no such recipe" and "another workspace's recipe" — they are
        // deliberately indistinguishable, so this refusal must not describe which it was.
        _dataLayer.Target = null;

        var result = await CreateAsync();

        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
        Assert.Empty(result.Error.FieldErrors);
        Assert.Null(_dataLayer.Created);
    }

    [Fact]
    public async Task A_version_the_recipe_does_not_have_is_not_found_and_names_the_field()
    {
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Draft, VersionId: null);

        var result = await CreateAsync();

        Assert.Equal(RecipeErrorCodes.VersionNotFound, result.Error!.Code);
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.SourceVersionNumber).ToLowerFirst(),
            result.Error.FieldErrors.Keys);
        Assert.Null(_dataLayer.Created);
    }

    [Fact]
    public async Task An_unknown_version_is_refused_before_the_archive_gate()
    {
        // Both wrong at once. The version wins, matching RestoreVersionAsync: bringing the recipe back would
        // not make version 2 exist, so telling the caller to do that first would send them somewhere useless.
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Archived, VersionId: null);

        Assert.Equal(RecipeErrorCodes.VersionNotFound, (await CreateAsync()).Error!.Code);
    }

    [Fact]
    public async Task An_archived_recipe_refuses_a_test()
    {
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Archived, VersionId);

        var result = await CreateAsync();

        Assert.Equal(RecipeErrorCodes.RecipeArchivedConflict, result.Error!.Code);
        Assert.Null(_dataLayer.Created);
    }

    [Fact]
    public async Task A_test_dated_in_the_future_is_refused()
    {
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Draft, VersionId);

        var result = await CreateAsync(Request(testedAt: Now.AddDays(1)));

        Assert.Equal(RecipeErrorCodes.TestRunInvalidRequest, result.Error!.Code);
        Assert.Contains(
            nameof(CreateRecipeTestRunViewModel.TestedAt).ToLowerFirst(),
            result.Error.FieldErrors.Keys);
        Assert.Null(_dataLayer.Created);
    }

    [Fact]
    public async Task A_test_just_inside_the_clock_skew_tolerance_is_accepted()
    {
        // A client's clock is not the server's, and refusing a test recorded seconds "in the future" would
        // reject correct requests.
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Draft, VersionId);

        Assert.True((await CreateAsync(Request(testedAt: Now.Add(TestRunPolicy.FutureTolerance)))).Succeeded);
    }

    [Fact]
    public async Task A_test_from_last_year_is_accepted()
    {
        // There is deliberately no floor: a creator entering their own history is not making a mistake.
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Draft, VersionId);

        Assert.True((await CreateAsync(Request(testedAt: Now.AddYears(-1)))).Succeeded);
    }

    [Fact]
    public async Task A_recorded_test_pins_the_version_and_takes_its_actor_from_the_context()
    {
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Approved, VersionId);

        var result = await CreateAsync();
        var run = _dataLayer.Created!;

        Assert.True(result.Succeeded);
        Assert.Equal(RecipeId, run.RecipeId);
        Assert.Equal(VersionId, run.RecipeVersionId);

        // The tester, the author and the editor all come from the resolved membership, never from the request —
        // which has no field for any of them.
        Assert.Equal(ActorMembershipId, run.TestedByMembershipId);
        Assert.Equal(ActorMembershipId, run.CreatedByMembershipId);
        Assert.Equal(ActorMembershipId, run.UpdatedByMembershipId);

        // One read of the clock for the whole operation, and TestedAt is the caller's own value rather than it.
        Assert.Equal(Now, run.CreatedAt);
        Assert.Equal(Now, run.UpdatedAt);
        Assert.Equal(Now.AddDays(-1), run.TestedAt);

        // WorkspaceId is left for the ownership interceptor. Business assigning it is the defect tenancy.md
        // names, and a test that accepted either value would not notice the day it started being assigned here.
        Assert.Equal(Guid.Empty, run.WorkspaceId);
    }

    [Fact]
    public async Task An_issue_is_attached_to_the_observation_its_index_named()
    {
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Draft, VersionId);

        var result = await CreateAsync(Request(
            observations:
            [
                new CanonicalTestObservation(0, TestObservationKind.Appearance, "Pale on top."),
                new CanonicalTestObservation(1, TestObservationKind.Texture, "The crumb was close."),
            ],
            issues:
            [
                new CanonicalTestIssue(TestIssueSeverity.Major, "Crumb too dense", null, ObservationIndex: 1),
                new CanonicalTestIssue(TestIssueSeverity.Minor, "Filed on its own", null, ObservationIndex: null),
            ]));

        var run = _dataLayer.Created!;
        var texture = run.Observations.Single(observation => observation.Text == "The crumb was close.");

        var linked = run.Issues.Single(issue => issue.Title == "Crumb too dense");
        Assert.Equal(texture.Id, linked.TestObservationId);

        // An issue the creator filed directly points at nothing, which is a legitimate state rather than a
        // missing link.
        Assert.Null(run.Issues.Single(issue => issue.Title == "Filed on its own").TestObservationId);

        // Every issue carries the run's recipe, which is what lets a resolution's correction version be
        // constrained to a version of this same recipe.
        Assert.All(run.Issues, issue => Assert.Equal(RecipeId, issue.RecipeId));

        // Ids come back in submitted order, which is the contract the ServiceModel publishes.
        Assert.Equal(
            [.. run.Observations.OrderBy(observation => observation.SortOrder).Select(observation => observation.Id)],
            result.Value!.ObservationIds);
        Assert.Equal(2, result.Value.IssueIds.Count);
    }

    [Fact]
    public async Task Positions_run_from_zero_in_submitted_order()
    {
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Draft, VersionId);

        await CreateAsync(Request(
            observations:
            [
                new CanonicalTestObservation(0, TestObservationKind.Unspecified, "first"),
                new CanonicalTestObservation(1, TestObservationKind.Unspecified, "second"),
                new CanonicalTestObservation(2, TestObservationKind.Unspecified, "third"),
            ]));

        var run = _dataLayer.Created!;

        Assert.Equal(
            ["first", "second", "third"],
            [.. run.Observations.OrderBy(observation => observation.SortOrder).Select(observation => observation.Text)]);
        Assert.Equal([0, 1, 2], [.. run.Observations.Select(observation => observation.SortOrder).Order()]);
    }

    [Fact]
    public async Task The_resolved_yield_unit_dimension_is_stored_beside_the_unit()
    {
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Draft, VersionId);
        var unitId = Guid.NewGuid();

        await CreateAsync(
            Request() with { ActualYieldQuantity = 10m, ActualYieldUnitId = unitId },
            MeasurementDimension.Count);

        // The pair the composite foreign key pins together. Business is handed the dimension rather than
        // looking it up, because reading the unit catalogue is facade-to-facade work.
        Assert.Equal(unitId, _dataLayer.Created!.ActualYieldUnitId);
        Assert.Equal(MeasurementDimension.Count, _dataLayer.Created.ActualYieldUnitDimension);
    }

    [Fact]
    public async Task Nothing_about_a_recorded_test_reaches_the_recipe()
    {
        // The whole restriction, asserted on the only surface Business has: it calls FindTargetAsync, which
        // reads, and CreateAsync, which writes a run. A recipe write would have to go through a method this
        // fake does not have.
        _dataLayer.Target = new TestRunTarget(RecipeStatus.Draft, VersionId);

        await CreateAsync(Request() with
        {
            ActualYieldText = "got 10, not 12",
            ActualTotalTimeMinutes = 50,
        });

        Assert.Equal(1, _dataLayer.Writes);
        Assert.Equal("got 10, not 12", _dataLayer.Created!.ActualYieldText);
    }

    // ---- Reading the history ----

    /// <summary>
    /// Null from the DataLayer covers both "no such recipe" and "another workspace's recipe" — deliberately
    /// indistinguishable, so this refusal must not describe which it was, and it must not be an empty page either.
    /// </summary>
    [Fact]
    public async Task A_history_of_a_recipe_that_is_not_visible_is_not_found()
    {
        _dataLayer.History = null;

        var result = await ListAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(RecipeErrorCodes.RecipeNotFound, result.Error!.Code);
    }

    [Fact]
    public async Task A_history_with_no_tests_is_an_empty_page_rather_than_a_refusal()
    {
        _dataLayer.History = new TestRunHistoryPage([], false, Counts(new Dictionary<TestRunOutcome, int>()));

        var result = await ListAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!.Page.Items);
        Assert.Equal(0, result.Value.Page.Summary!.TotalCount);
    }

    /// <summary>
    /// The row version becomes the opaque token a client quotes, and the tester membership travels beside the page
    /// rather than inside it — Business may not name a person, so it hands the id to the Facade unspent.
    /// </summary>
    [Fact]
    public async Task A_row_publishes_its_token_and_leaves_the_tester_to_be_named()
    {
        var row = HistoryRow();
        _dataLayer.History = new TestRunHistoryPage([row], false, Counts(new Dictionary<TestRunOutcome, int>()));

        var result = await ListAsync();
        var published = Assert.Single(result.Value!.Page.Items);

        Assert.Equal(RecipeConcurrencyToken.From(row.RowVersion), published.ConcurrencyToken);
        Assert.Null(published.TestedByName);
        Assert.Equal(row.TestedByMembershipId, published.TestedByMembershipId);
        Assert.Equal(
            row.TestedByMembershipId,
            result.Value.TesterMembershipByTestRunId[row.Id]);
    }

    /// <summary>
    /// A cursor is minted here rather than in the repository, bound to the scope the criteria carries — and only
    /// when another page actually follows, so a client loops until it is null rather than asking one page too many.
    /// </summary>
    [Fact]
    public async Task A_cursor_is_minted_only_when_another_page_follows()
    {
        _dataLayer.History = new TestRunHistoryPage([HistoryRow()], false, null);
        Assert.Null((await ListAsync()).Value!.Page.NextCursor);

        _dataLayer.History = new TestRunHistoryPage([HistoryRow()], true, null);
        Assert.NotNull((await ListAsync()).Value!.Page.NextCursor);
    }

    /// <summary>
    /// The published breakdown names every verdict, at zero where the <c>GROUP BY</c> returned no row — so a screen
    /// never has to tell "no failures" from "failures not counted". The total is summed from it rather than counted
    /// again, which is what stops the headline figure disagreeing with the numbers beneath it.
    /// </summary>
    [Fact]
    public async Task The_published_summary_names_every_outcome_and_totals_the_breakdown()
    {
        _dataLayer.History = new TestRunHistoryPage(
            [],
            false,
            Counts(new Dictionary<TestRunOutcome, int>
            {
                [TestRunOutcome.Succeeded] = 2,
                [TestRunOutcome.Failed] = 1,
            }));

        var summary = (await ListAsync()).Value!.Page.Summary!;

        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(Enum.GetValues<TestRunOutcome>().Order(), summary.ByOutcome.Keys.Order());
        Assert.Equal(2, summary.ByOutcome[TestRunOutcome.Succeeded]);
        Assert.Equal(0, summary.ByOutcome[TestRunOutcome.NotStated]);
    }

    /// <summary>
    /// Null means "not asked for" and never "nothing to report", so a caller that turned the summary off gets no
    /// figures rather than zeros they might render.
    /// </summary>
    [Fact]
    public async Task A_history_read_without_a_summary_publishes_none()
    {
        _dataLayer.History = new TestRunHistoryPage([HistoryRow()], false, null);

        Assert.Null((await ListAsync()).Value!.Page.Summary);
    }

    private Task<OperationResult<TestRunHistoryPageResult>> ListAsync() =>
        _business.ListAsync(
            new TestRunHistoryCriteria(RecipeId, new TestRunHistoryFilters(), "scope"),
            TestContext.Current.CancellationToken);

    private static TestRunHistoryCounts Counts(IReadOnlyDictionary<TestRunOutcome, int> byOutcome) =>
        new(byOutcome, 0, 0);

    private static TestRunHistoryRecord HistoryRow() =>
        new(
            Guid.NewGuid(),
            VersionId,
            2,
            Now.AddDays(-1),
            ActorMembershipId,
            TestRunOutcome.Succeeded,
            4,
            "Good but dense.",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            0,
            Now,
            Now,
            [1, 2, 3, 4, 5, 6, 7, 8]);

    private sealed class RecordingTestRunDataLayer : IRecipeTestRunDataLayer
    {
        public TestRunTarget? Target { get; set; } = new(RecipeStatus.Draft, Guid.NewGuid());

        /// <summary>Named <c>Status</c> rather than <c>RecipeStatus</c>, which would shadow the enum itself.</summary>
        public RecipeStatus? Status { get; set; } = RecipeStatus.Draft;

        public RecipeTestRun? Created { get; private set; }

        public RecipeTestRun? Loaded { get; set; }

        public RecipeTestRun? Updated { get; private set; }

        public TestIssueResolutionTarget? IssueTarget { get; set; }

        public TestIssueResolution? Resolved { get; private set; }

        public Guid? VersionIdForNumber { get; set; }

        /// <summary>Forces the save to report the run as having moved on, for the conflict path.</summary>
        public bool UpdateConflicts { get; set; }

        /// <summary>Forces the resolution insert to lose the race the unique index settles.</summary>
        public bool ResolutionRaceLost { get; set; }

        /// <summary>
        /// What a history read returns. Null — the default — is a recipe the caller may not see, which is the
        /// answer an unknown recipe and another workspace's recipe both produce.
        /// </summary>
        public TestRunHistoryPage? History { get; set; }

        /// <summary>The criteria the last history read was handed, so a test can prove they reached the query.</summary>
        public TestRunHistoryCriteria? ListedCriteria { get; private set; }

        public int Writes { get; private set; }

        public Task<TestRunTarget?> FindTargetAsync(
            Guid recipeId, int versionNumber, CancellationToken cancellationToken) =>
            Task.FromResult(Target);

        public Task<RecipeStatus?> FindRecipeStatusAsync(Guid recipeId, CancellationToken cancellationToken) =>
            Task.FromResult(Status);

        public Task<CreatedRecipeTestRun> CreateAsync(RecipeTestRun run, CancellationToken cancellationToken)
        {
            Created = run;
            Writes++;

            return Task.FromResult(new CreatedRecipeTestRun(
                run.Id,
                [.. run.Observations.OrderBy(observation => observation.SortOrder).Select(observation => observation.Id)],
                [.. run.Issues.OrderBy(issue => issue.SortOrder).Select(issue => issue.Id)]));
        }

        public Task<RecipeTestRun?> GetForUpdateAsync(
            Guid recipeId, Guid testRunId, CancellationToken cancellationToken) =>
            Task.FromResult(Loaded);

        public Task<TestRunUpdateOutcome> UpdateAsync(RecipeTestRun run, CancellationToken cancellationToken)
        {
            if (UpdateConflicts)
            {
                return Task.FromResult(TestRunUpdateOutcome.Conflict());
            }

            Updated = run;
            Writes++;

            return Task.FromResult(TestRunUpdateOutcome.Applied());
        }

        public Task<TestIssueResolutionTarget?> FindIssueForResolutionAsync(
            Guid recipeId, Guid testRunId, Guid issueId, CancellationToken cancellationToken) =>
            Task.FromResult(IssueTarget);

        public Task<Guid?> FindVersionIdAsync(
            Guid recipeId, int versionNumber, CancellationToken cancellationToken) =>
            Task.FromResult(VersionIdForNumber);

        public Task<TestRunHistoryPage?> ListAsync(
            TestRunHistoryCriteria criteria, CancellationToken cancellationToken)
        {
            ListedCriteria = criteria;

            return Task.FromResult(History);
        }

        public Task<TestIssueResolutionOutcome> ResolveAsync(
            TestIssueResolution resolution, CancellationToken cancellationToken)
        {
            if (ResolutionRaceLost)
            {
                return Task.FromResult(TestIssueResolutionOutcome.AlreadyResolved());
            }

            Resolved = resolution;
            Writes++;

            return Task.FromResult(TestIssueResolutionOutcome.Applied(resolution));
        }
    }

    private sealed class StubTestRunWorkspaceContext(Guid membershipId) : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public string WorkspaceSlug => "workspace-a";

        public Guid MembershipId { get; } = membershipId;

        public WorkspaceRole Role => WorkspaceRole.Owner;

        public string AccountId => "account-a";
    }

    private sealed class StubTestRunClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}

internal static class FieldKeyExtensions
{
    /// <summary>
    /// The camelCase key <c>OperationError.Validation</c> produces from a PascalCase field name, so a test can
    /// assert the key a client actually receives rather than the one the domain wrote.
    /// </summary>
    public static string ToLowerFirst(this string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
