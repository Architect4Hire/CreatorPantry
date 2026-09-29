using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Posting account usage for provider attempts written before the recording seam existed, and reporting
/// attempts written since that the ledger has no entry for (USAGE-002).
/// </summary>
/// <remarks>
/// The second documented <c>IgnoreQueryFilters</c> carve-out in the domain, so these cover not only that the
/// pass works but what it is permitted to carry across the workspace boundary while doing it.
/// </remarks>
public sealed class AiUsageReconciliationTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipInA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid MembershipInB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    /// <summary>A second person, so an attempt in workspace B is not automatically the first person's.</summary>
    private static readonly Guid OtherMembershipInB = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private const string Account = "user-sam";
    private const string OtherAccount = "user-alex";

    /// <summary>Everything seeded is older than this; the backfill's range is everything below it.</summary>
    private static readonly DateTimeOffset Cutoff = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

    public AiUsageReconciliationTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddAiUsageReconciliation()
            .AddSingleton<IClock>(_clock)
            .Configure<AiUsageReconciliationOptions>(options =>
            {
                options.Before = Cutoff;
                options.BatchSize = 10;
            })
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            new Workspace { Id = WorkspaceA, Name = "A", Slug = "a", CreatedAt = _clock.UtcNow },
            new Workspace { Id = WorkspaceB, Name = "B", Slug = "b", CreatedAt = _clock.UtcNow });

        db.Users.AddRange(User("user-sam", "Sam"), User("user-alex", "Alex"));

        db.WorkspaceMemberships.AddRange(
            Membership(MembershipInA, WorkspaceA, Account),

            // The same person in the other workspace: one account, two memberships (USAGE-001).
            Membership(MembershipInB, WorkspaceB, Account),
            Membership(OtherMembershipInB, WorkspaceB, OtherAccount));

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- the backfill ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_attempt_with_no_entry_is_posted_with_everything_the_ledger_records()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1, inputTokens: 120, outputTokens: 40);

        var summary = await RunPassAsync();

        Assert.Equal(1, summary.Posted);

        var entry = Assert.Single(await LedgerAsync());
        Assert.Equal(Account, entry.AccountId);
        Assert.Equal(WorkspaceA, entry.WorkspaceId);
        Assert.Equal(operationId, entry.AiOperationId);
        Assert.Equal(1, entry.AttemptNumber);
        Assert.Equal(AiTaskType.RecipeConcepts, entry.TaskType);
        Assert.Equal(120, entry.InputTokens);
        Assert.Equal(40, entry.OutputTokens);
        Assert.True(entry.UsageReported);
        Assert.True(entry.IsBillable);
        Assert.Equal(AiUsageOutcome.Succeeded, entry.Outcome);

        // The attempt's own end, not the moment the backfill ran. A run months later has to land its entries
        // in the period the work actually happened in.
        Assert.Equal(Cutoff.AddDays(-1), entry.OccurredAt);
    }

    /// <summary>
    /// Bounded passes, and the bound is where resumability comes from: each batch commits on its own, and
    /// "what is left" is the query rather than a stored cursor.
    /// </summary>
    [Fact]
    public async Task A_backfill_larger_than_one_batch_is_finished_across_passes()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);

        for (var attempt = 1; attempt <= 25; attempt++)
        {
            await SeedAttemptAsync(operationId, WorkspaceA, attempt, inputTokens: attempt);
        }

        var first = await RunPassAsync();

        Assert.Equal(10, first.Posted);
        Assert.True(first.Remaining);
        Assert.Equal(10, (await LedgerAsync()).Count);

        var second = await RunPassAsync();
        Assert.Equal(10, second.Posted);
        Assert.True(second.Remaining);

        var third = await RunPassAsync();
        Assert.Equal(5, third.Posted);
        Assert.False(third.Remaining);

        Assert.Equal(25, (await LedgerAsync()).Count);
        Assert.Equal(
            Enumerable.Range(1, 25),
            (await LedgerAsync()).Select(entry => entry.AttemptNumber).Order());
    }

    /// <summary>
    /// A partial run loses nothing. Restart is not a special case: the pass that follows finds exactly the
    /// attempts the one before it did not post, because nothing recorded that they had been attempted.
    /// </summary>
    [Fact]
    public async Task A_pass_that_ran_before_is_resumed_rather_than_repeated()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);

        for (var attempt = 1; attempt <= 12; attempt++)
        {
            await SeedAttemptAsync(operationId, WorkspaceA, attempt);
        }

        await RunPassAsync();

        // A fresh container over the same database, standing in for the process having been restarted between
        // the two passes. Nothing is carried across in memory, and nothing needs to be.
        await using var restarted = BuildProvider();
        await using var scope = restarted.CreateAsyncScope();

        var summary = await scope.ServiceProvider.GetRequiredService<IAiUsageReconciler>()
            .RunPassAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, summary.Posted);
        Assert.Equal(12, (await LedgerAsync()).Count);
    }

    /// <summary>Re-running a finished backfill posts nothing. Idempotent by the ledger's own identity.</summary>
    [Fact]
    public async Task A_completed_backfill_posts_nothing_a_second_time()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1);

        Assert.Equal(1, (await RunPassAsync()).Posted);
        Assert.Equal(0, (await RunPassAsync()).Posted);
        Assert.Equal(0, (await RunPassAsync()).Posted);

        Assert.Single(await LedgerAsync());
    }

    [Fact]
    public async Task An_attempt_that_already_has_an_entry_is_left_alone()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1, inputTokens: 500);
        await SeedLedgerEntryAsync(operationId, WorkspaceA, attemptNumber: 1, inputTokens: 7);

        Assert.Equal(0, (await RunPassAsync()).Posted);

        // Unchanged: the backfill must not restate what the live path already recorded.
        Assert.Equal(7, Assert.Single(await LedgerAsync()).InputTokens);
    }

    // ---- the cutoff ------------------------------------------------------------------------------------

    /// <summary>
    /// The line between repairing and reporting. An attempt at or after the cutoff is counted and left, so a
    /// gap in current traffic stays visible instead of being quietly filled in.
    /// </summary>
    [Fact]
    public async Task An_attempt_after_the_cutoff_is_reported_and_never_posted()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);

        // After the cutoff and inside the drift window, which is the shape of a gap in current traffic.
        await SeedAttemptAsync(
            operationId, WorkspaceA, attemptNumber: 1, completedAt: _clock.UtcNow.AddHours(-1));

        var summary = await RunPassAsync();

        Assert.Equal(0, summary.Posted);
        Assert.Equal(1, summary.Drift);
        Assert.Empty(await LedgerAsync());
    }

    /// <summary>
    /// The two ranges do not overlap and do not leave a hole between them: an attempt after the cutoff but
    /// older than the drift window is neither posted nor reported. That is the trade the window buys, and it
    /// is stated here rather than discovered later.
    /// </summary>
    [Fact]
    public async Task An_attempt_between_the_cutoff_and_the_window_is_neither_posted_nor_reported()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1, completedAt: Cutoff.AddHours(1));

        var summary = await RunPassAsync();

        Assert.Equal(0, summary.Posted);
        Assert.Equal(0, summary.Drift);
        Assert.Empty(await LedgerAsync());
    }

    [Fact]
    public async Task The_cutoff_is_exclusive_at_its_own_instant()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, 1, completedAt: Cutoff.AddTicks(-1));
        await SeedAttemptAsync(operationId, WorkspaceA, 2, completedAt: Cutoff);

        var summary = await RunPassAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Equal(1, Assert.Single(await LedgerAsync()).AttemptNumber);
    }

    /// <summary>
    /// With no cutoff configured — the steady state once a backfill has drained — the pass reports and never
    /// writes, whatever it finds.
    /// </summary>
    [Fact]
    public async Task With_no_cutoff_configured_the_pass_only_reports()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1, completedAt: _clock.UtcNow.AddHours(-1));

        await using var provider = BuildProvider(before: null);
        await using var scope = provider.CreateAsyncScope();

        var summary = await scope.ServiceProvider.GetRequiredService<IAiUsageReconciler>()
            .RunPassAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, summary.Posted);
        Assert.Equal(1, summary.Drift);
        Assert.Empty(await LedgerAsync());
    }

    /// <summary>
    /// Drift is a trailing window, so the check stays a range seek on a table that grows forever. An old gap
    /// falls out of it — which is why the backfill's range is the one that repairs.
    /// </summary>
    [Fact]
    public async Task Drift_older_than_the_window_is_not_reported()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(
            operationId, WorkspaceA, attemptNumber: 1, completedAt: _clock.UtcNow.AddHours(-1));

        Assert.Equal(1, (await RunPassAsync()).Drift);

        // A fortnight on, the same gap is outside a twenty-four hour window.
        _clock.Advance(TimeSpan.FromDays(14));

        Assert.Equal(0, (await RunPassAsync()).Drift);
    }

    // ---- attribution -----------------------------------------------------------------------------------

    /// <summary>
    /// One account, two workspaces, one ledger — and each entry keeps the workspace it was earned in as a
    /// reporting dimension. This is the question the workspace-scoped tables structurally cannot answer, which
    /// is why the pass is allowed outside the filter at all.
    /// </summary>
    [Fact]
    public async Task One_account_working_in_two_workspaces_is_reconciled_into_one_ledger()
    {
        var inA = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        var inB = await SeedOperationAsync(WorkspaceB, MembershipInB, AiTaskType.RecipeReview);

        await SeedAttemptAsync(inA, WorkspaceA, 1, inputTokens: 100);
        await SeedAttemptAsync(inB, WorkspaceB, 1, inputTokens: 250);

        Assert.Equal(2, (await RunPassAsync()).Posted);

        var ledger = await LedgerAsync();
        Assert.All(ledger, entry => Assert.Equal(Account, entry.AccountId));
        Assert.Equal([WorkspaceA, WorkspaceB], ledger.Select(entry => entry.WorkspaceId).Order());
        Assert.Equal(350, ledger.Sum(entry => entry.InputTokens));
    }

    /// <summary>
    /// Two people in one workspace are two accounts. A pass that read the workspace and guessed the person
    /// would put one creator's spend on another's ledger — worse than a gap, because a gap is visible.
    /// </summary>
    [Fact]
    public async Task Two_accounts_in_one_workspace_are_not_merged()
    {
        var sam = await SeedOperationAsync(WorkspaceB, MembershipInB, AiTaskType.RecipeConcepts);
        var alex = await SeedOperationAsync(WorkspaceB, OtherMembershipInB, AiTaskType.RecipeConcepts);

        await SeedAttemptAsync(sam, WorkspaceB, 1);
        await SeedAttemptAsync(alex, WorkspaceB, 1);

        Assert.Equal(2, (await RunPassAsync()).Posted);

        var ledger = await LedgerAsync();
        Assert.Equal([OtherAccount, Account], ledger.Select(entry => entry.AccountId).Order());
    }

    /// <summary>
    /// Removal is a status, not a delete. A departed member's past attempts still belong to their account, and
    /// a pass that narrowed to active memberships would silently drop exactly the history it exists to
    /// recover.
    /// </summary>
    [Fact]
    public async Task A_removed_members_attempts_are_still_attributed_to_their_account()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1);
        await SetMembershipStatusAsync(MembershipInA, WorkspaceMembershipStatus.Removed);

        Assert.Equal(1, (await RunPassAsync()).Posted);
        Assert.Equal(Account, Assert.Single(await LedgerAsync()).AccountId);
    }

    /// <summary>
    /// The pairing this join would be unsafe without. A membership is matched on its workspace as well as its
    /// id, so an operation naming an id that belongs to another workspace's membership resolves to nobody
    /// rather than to that workspace's account — which would otherwise write a permanent ledger row showing
    /// one person's spend to a workspace they were never a member of.
    /// </summary>
    [Fact]
    public async Task A_membership_from_another_workspace_is_never_matched()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1);

        // The operation now names a membership that exists, is active, and belongs to workspace B. Only the
        // workspace half of the predicate can tell that apart from a legitimate requester.
        await PointOperationAtAsync(operationId, WorkspaceA, OtherMembershipInB);

        var summary = await RunPassAsync();

        Assert.Equal(0, summary.Posted);
        Assert.Equal(1, summary.Unattributable);
        Assert.Empty(await LedgerAsync());
    }

    /// <summary>
    /// Nobody to charge means nothing is charged. Counted so the figure is visible, and left where it is:
    /// guessing an account would be a wrong bill, and a wrong bill is not noticed the way a gap is.
    /// </summary>
    [Fact]
    public async Task An_attempt_with_no_resolvable_account_is_counted_and_not_posted()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1);
        await DeleteMembershipAsync(MembershipInA);

        var summary = await RunPassAsync();

        Assert.Equal(0, summary.Posted);
        Assert.Equal(1, summary.Unattributable);
        Assert.Empty(await LedgerAsync());
    }

    /// <summary>
    /// One unattributable operation does not stop the batch. The rest of the pass still posts, which is what
    /// keeps a single unresolvable row from stalling a backfill indefinitely.
    /// </summary>
    [Fact]
    public async Task An_unattributable_attempt_does_not_block_the_rest_of_the_batch()
    {
        var orphaned = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        var healthy = await SeedOperationAsync(WorkspaceB, OtherMembershipInB, AiTaskType.RecipeConcepts);

        await SeedAttemptAsync(orphaned, WorkspaceA, 1);
        await SeedAttemptAsync(healthy, WorkspaceB, 1);
        await DeleteMembershipAsync(MembershipInA);

        var summary = await RunPassAsync();

        Assert.Equal(1, summary.Posted);
        Assert.Equal(1, summary.Unattributable);
        Assert.Equal(OtherAccount, Assert.Single(await LedgerAsync()).AccountId);
    }

    [Fact]
    public async Task A_diagnostic_task_is_reconciled_as_not_billable()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.Diagnostic);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1);

        await RunPassAsync();

        Assert.False(Assert.Single(await LedgerAsync()).IsBillable);
    }

    [Fact]
    public async Task An_attempt_the_provider_reported_nothing_for_posts_as_unreported()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1);

        await RunPassAsync();

        var entry = Assert.Single(await LedgerAsync());
        Assert.False(entry.UsageReported);
        Assert.Null(entry.InputTokens);
        Assert.Null(entry.TotalTokens);
    }

    // ---- what it must not do ---------------------------------------------------------------------------

    /// <summary>
    /// Backfilled usage is history, not spend. A creator's attempts from before the ledger existed must not
    /// retroactively eat the allowance they have now, and a period that has finalized must not change.
    /// </summary>
    [Fact]
    public async Task Reconciliation_never_moves_a_quota_period()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1, inputTokens: 900);
        await SeedFinalizedPeriodAsync();

        Assert.Equal(1, (await RunPassAsync()).Posted);

        var period = Assert.Single(await PeriodsAsync());

        Assert.Equal(0m, period.Consumed);
        Assert.Equal(0m, period.Reserved);
        Assert.NotNull(period.SettledAt);
        Assert.Empty(await ReservationsAsync());
    }

    /// <summary>
    /// The pass saves the ambient unit of work from a change set built outside the workspace filter, so it
    /// refuses to run anywhere a workspace is resolved. Without this, a registration that reached the API host
    /// would commit a request's pending writes and stamp them against a workspace the pass never consulted.
    /// </summary>
    [Fact]
    public async Task A_pass_refuses_to_run_inside_a_resolved_workspace_scope()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1);

        await using var scope = _provider.CreateAsyncScope();
        Resolve(scope, WorkspaceA, MembershipInA);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IAiUsageReconciler>()
                .RunPassAsync(TestContext.Current.CancellationToken));

        Assert.Empty(await LedgerAsync());
    }

    /// <summary>
    /// A cutoff that is not in the past is not a cutoff: it puts current traffic back into the backfill's
    /// range and drives the drift figure to zero. Refused rather than clamped, so a mistake is loud instead of
    /// silently becoming the outcome the split exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_cutoff_that_is_not_in_the_past_is_refused()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(operationId, WorkspaceA, attemptNumber: 1);

        await using var provider = BuildProvider(before: _clock.UtcNow.AddYears(1));
        await using var scope = provider.CreateAsyncScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IAiUsageReconciler>()
                .RunPassAsync(TestContext.Current.CancellationToken));

        Assert.Empty(await LedgerAsync());
    }

    /// <summary>
    /// A batch it can attribute none of is a batch every later pass reads again, so reporting more to do
    /// would be a sweep claiming progress it is not making, forever. It answers false and the host says why.
    /// </summary>
    [Fact]
    public async Task A_batch_of_nothing_but_unattributable_attempts_does_not_claim_progress()
    {
        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await SeedAttemptAsync(operationId, WorkspaceA, attempt);
        }

        await DeleteMembershipAsync(MembershipInA);

        var summary = await RunPassAsync();

        Assert.Equal(0, summary.Posted);
        Assert.Equal(10, summary.Unattributable);
        Assert.False(summary.Remaining);
    }

    /// <summary>
    /// The constraint the carve-out rests on, held by the type system rather than by review: what crosses the
    /// workspace boundary is identifiers, counts, instants and enums. <c>AiExecutionMetadata.FailureSummary</c>
    /// is the row's one free-text column and is deliberately not among them — the ledger it feeds has no
    /// free-text column at all, and a query that has stepped outside the filter is the last place to start
    /// carrying creator-adjacent text.
    /// </summary>
    [Theory]
    [InlineData(typeof(UnpostedAiAttempt), "ProviderName", "ModelName", "ModelDeployment")]
    [InlineData(typeof(AiAttemptAttribution), "AccountId")]
    public void The_reconciliation_carries_no_creator_content(Type carried, params string[] allowedText)
    {
        var text = carried.GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(allowedText.Order(StringComparer.Ordinal), text);

        Assert.All(
            carried.GetProperties(),
            property => Assert.True(
                property.PropertyType == typeof(string)
                    || property.PropertyType == typeof(Guid)
                    || property.PropertyType == typeof(int)
                    || property.PropertyType == typeof(int?)
                    || property.PropertyType == typeof(bool)
                    || property.PropertyType == typeof(decimal?)
                    || property.PropertyType == typeof(DateTimeOffset)
                    || property.PropertyType.IsEnum
                    || Nullable.GetUnderlyingType(property.PropertyType)?.IsEnum == true,
                $"{carried.Name}.{property.Name} is {property.PropertyType.Name}; this boundary carries "
                    + "identifiers, counts, instants and enums only"));
    }

    /// <summary>
    /// The row this reads has one free-text column and the ledger has none, so a real summary is seeded and
    /// the whole ledger row is then searched for it. The type-shape test above is the stronger guarantee;
    /// this is the one that would notice if a column were added and quietly copied through.
    /// </summary>
    [Fact]
    public async Task A_failure_summary_never_reaches_the_ledger()
    {
        const string Sanitized = "the provider refused this request";

        var operationId = await SeedOperationAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        await SeedAttemptAsync(
            operationId,
            WorkspaceA,
            attemptNumber: 1,
            failureCategory: AiFailureCategory.Provider,
            failureSummary: Sanitized);

        Assert.Equal(1, (await RunPassAsync()).Posted);

        var entry = Assert.Single(await LedgerAsync());

        Assert.Equal(AiUsageOutcome.Failed, entry.Outcome);
        Assert.DoesNotContain(
            entry.GetType().GetProperties()
                .Select(property => property.GetValue(entry))
                .OfType<string>(),
            value => value.Contains("refused", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_reconciliation_has_nowhere_to_put_a_failure_summary()
    {
        Assert.Contains(
            typeof(AiExecutionMetadata).GetProperties(),
            property => property.Name == nameof(AiExecutionMetadata.FailureSummary));

        Assert.DoesNotContain(
            typeof(UnpostedAiAttempt).GetProperties(),
            property => property.Name.Contains("Summary", StringComparison.Ordinal)
                || property.Name.Contains("Text", StringComparison.Ordinal)
                || property.Name.Contains("Title", StringComparison.Ordinal));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private ServiceProvider BuildProvider(DateTimeOffset? before = null, bool useCutoff = true) =>
        new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddAiUsageReconciliation()
            .AddSingleton<IClock>(_clock)
            .Configure<AiUsageReconciliationOptions>(options =>
            {
                options.Before = useCutoff ? before ?? Cutoff : null;
                options.BatchSize = 10;
            })
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

    private async Task<AiUsageReconciliationSummary> RunPassAsync()
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiUsageReconciler>()
            .RunPassAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedOperationAsync(Guid workspaceId, Guid membershipId, AiTaskType taskType)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, membershipId);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operation = new AiOperation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            TaskType = taskType,
            Scope = AiOperationScope.WholeRecipe,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = Guid.NewGuid().ToString(),
            RequestedByMembershipId = membershipId,
            RequestedAt = Cutoff.AddDays(-2),
            StatusChangedAt = Cutoff.AddDays(-1),
            AvailableAt = Cutoff.AddDays(-2),
        };

        db.AiOperations.Add(operation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return operation.Id;
    }

    /// <summary>
    /// An execution record with no ledger entry beside it — which is exactly the state every attempt written
    /// before the recording seam existed is in.
    /// </summary>
    private async Task SeedAttemptAsync(
        Guid operationId,
        Guid workspaceId,
        int attemptNumber,
        int? inputTokens = null,
        int? outputTokens = null,
        DateTimeOffset? completedAt = null,
        AiFailureCategory? failureCategory = null,
        string? failureSummary = null)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, workspaceId == WorkspaceA ? MembershipInA : MembershipInB);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var ended = completedAt ?? Cutoff.AddDays(-1);

        db.AiExecutionMetadata.Add(new AiExecutionMetadata
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            AiOperationId = operationId,
            AttemptNumber = attemptNumber,
            ProviderName = "test-provider",
            ModelName = "test-model",
            PromptTemplateId = "fixture.concepts",
            PromptTemplateVersion = "1.0.0",
            StartedAt = ended.AddSeconds(-1),
            CompletedAt = ended,
            LatencyMilliseconds = 1000,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CorrelationId = Guid.NewGuid(),
            FailureCategory = failureCategory,

            // The one free-text column on this row. A check constraint requires a category beside it, which is
            // why the two are set together.
            FailureSummary = failureSummary,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedLedgerEntryAsync(
        Guid operationId, Guid workspaceId, int attemptNumber, int inputTokens)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiUsageEntries.Add(new AccountAiUsageEntry
        {
            Id = Guid.NewGuid(),
            AccountId = Account,
            OccurredAt = Cutoff.AddDays(-1),
            AiOperationId = operationId,
            AttemptNumber = attemptNumber,
            TaskType = AiTaskType.RecipeConcepts,
            WorkspaceId = workspaceId,
            ProviderName = "test-provider",
            ModelName = "test-model",
            InputTokens = inputTokens,
            IsBillable = true,
            UsageReported = true,
            Outcome = AiUsageOutcome.Succeeded,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A period that has already closed, so a pass that touched one would be visible.</summary>
    private async Task SeedFinalizedPeriodAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotaPeriods.Add(new AccountAiQuotaPeriod
        {
            Id = Guid.NewGuid(),
            AccountId = Account,
            StartsAt = Cutoff.AddDays(-30),
            EndsAt = Cutoff.AddDays(-1),
            TimeZoneId = "Etc/UTC",
            LocalStartDate = new DateOnly(2026, 8, 21),
            Unit = AiQuotaUnit.Credits,
            Allowance = 1000m,
            CarriedOver = 0m,
            Consumed = 0m,
            Reserved = 0m,
            SettledAt = Cutoff,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SetMembershipStatusAsync(Guid membershipId, WorkspaceMembershipStatus status)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA, MembershipInA);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var membership = await db.WorkspaceMemberships.SingleAsync(
            row => row.Id == membershipId, TestContext.Current.CancellationToken);

        membership.Status = status;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Repoints an operation at a membership belonging to another workspace — the state the workspace half of
    /// the attribution predicate exists to refuse. No writer produces it, so it is manufactured here.
    /// </summary>
    private async Task PointOperationAtAsync(Guid operationId, Guid workspaceId, Guid membershipId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, membershipId);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var operation = await db.AiOperations.SingleAsync(
            row => row.Id == operationId, TestContext.Current.CancellationToken);

        operation.RequestedByMembershipId = membershipId;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Stands in for the cascade a workspace or account deletion would cause. Nothing in the product does this
    /// today, which is why the unattributable path is unreachable in practice and handled anyway.
    /// </summary>
    private async Task DeleteMembershipAsync(Guid membershipId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA, MembershipInA);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var membership = await db.WorkspaceMemberships.SingleAsync(
            row => row.Id == membershipId, TestContext.Current.CancellationToken);

        db.WorkspaceMemberships.Remove(membership);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AccountAiUsageEntry>> LedgerAsync()
    {
        using var scope = _provider.CreateScope();

        // No resolved workspace and no IgnoreQueryFilters: the ledger carries no filter, which is the property
        // that lets the reconciliation compare against it at all.
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AccountAiQuotaPeriod>> PeriodsAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AccountAiQuotaReservation>> ReservationsAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId, Guid membershipId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "a" : "b",
            membershipId,
            WorkspaceRole.Owner,
            Account);

    private ApplicationUser User(string id, string name) => new()
    {
        Id = id,
        UserName = $"{id}@example.com",
        NormalizedUserName = $"{id}@EXAMPLE.COM",
        Email = $"{id}@example.com",
        NormalizedEmail = $"{id}@EXAMPLE.COM",
        DisplayName = name,
        CreatedAt = _clock.UtcNow,
    };

    private WorkspaceMembership Membership(Guid id, Guid workspaceId, string userId) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        UserId = userId,
        Role = WorkspaceRole.Contributor,
        Status = WorkspaceMembershipStatus.Active,
        JoinedAt = _clock.UtcNow,
    };

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
