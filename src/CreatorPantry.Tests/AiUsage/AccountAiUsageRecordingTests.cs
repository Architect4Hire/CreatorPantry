using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Posting per-account usage from the code path that settles a provider attempt (USAGE-001/005): what is
/// recorded, that it commits with the attempt record, that a duplicate posts once, and that one account
/// working in two workspaces keeps one ledger.
/// </summary>
public sealed class AccountAiUsageRecordingTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipInA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid MembershipInB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    /// <summary>One person. Two memberships above, one of these — which is the whole point of USAGE-001.</summary>
    private const string Account = "user-sam";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    public AccountAiUsageRecordingTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
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
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    // ---- success ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_attempt_posts_one_entry_attributed_to_the_account()
    {
        var settled = await SettleProposalAsync(WorkspaceA, MembershipInA, Attempt(inputTokens: 120, outputTokens: 40));

        var entry = Assert.Single(await LedgerAsync());

        Assert.Equal(Account, entry.AccountId);
        Assert.Equal(WorkspaceA, entry.WorkspaceId);
        Assert.Equal(settled.OperationId, entry.AiOperationId);
        Assert.Equal(1, entry.AttemptNumber);
        Assert.Equal(AiTaskType.RecipeConcepts, entry.TaskType);
        Assert.Equal(AiUsageOutcome.Succeeded, entry.Outcome);
        Assert.Equal(120, entry.InputTokens);
        Assert.Equal(40, entry.OutputTokens);
        Assert.True(entry.UsageReported);
        Assert.True(entry.IsBillable);
    }

    /// <summary>
    /// Null until a gateway reports a provider total — never input plus output. A computed total would be
    /// indistinguishable from a reported one, and providers report totals that are legitimately not the sum.
    /// </summary>
    [Fact]
    public async Task A_total_is_not_invented_from_the_parts()
    {
        await SettleProposalAsync(WorkspaceA, MembershipInA, Attempt(inputTokens: 120, outputTokens: 40));

        Assert.Null(Assert.Single(await LedgerAsync()).TotalTokens);
    }

    /// <summary>
    /// The guarantee the whole phase rests on: a settled attempt that posts no usage is a defect, so the
    /// diagnostic record and the ledger entry are written by one save and neither can exist alone.
    /// </summary>
    [Fact]
    public async Task The_ledger_entry_and_the_attempt_record_arrive_together()
    {
        await SettleProposalAsync(WorkspaceA, MembershipInA, Attempt());

        var attempts = await ExecutionRecordsAsync();
        var ledger = await LedgerAsync();

        Assert.Single(attempts);
        Assert.Single(ledger);
        Assert.Equal(attempts[0].AttemptNumber, ledger[0].AttemptNumber);
        Assert.Equal(attempts[0].CompletedAt, ledger[0].OccurredAt);
    }

    // ---- failures still post --------------------------------------------------------------------------

    [Theory]
    [InlineData(AiFailureCategory.Provider, AiUsageOutcome.Failed)]
    [InlineData(AiFailureCategory.Timeout, AiUsageOutcome.TimedOut)]
    [InlineData(AiFailureCategory.Cancelled, AiUsageOutcome.Cancelled)]
    [InlineData(AiFailureCategory.SafetyBlocked, AiUsageOutcome.SafetyBlocked)]
    [InlineData(AiFailureCategory.OutputSchemaInvalid, AiUsageOutcome.Failed)]
    public async Task A_failed_attempt_still_posts_an_entry_carrying_its_outcome(
        AiFailureCategory category, AiUsageOutcome expected)
    {
        await SettleFailureAsync(WorkspaceA, MembershipInA, category, Attempt(failure: category));

        Assert.Equal(expected, Assert.Single(await LedgerAsync()).Outcome);
    }

    /// <summary>
    /// A blocked attempt reads as blocked whichever way it was noticed — the provider flagging the response,
    /// or a check refusing it. A creator-facing outcome should not depend on which of the two got there first.
    /// </summary>
    [Fact]
    public async Task A_provider_flagged_block_records_the_same_outcome_as_a_refused_one()
    {
        await SettleFailureAsync(
            WorkspaceA,
            MembershipInA,
            AiFailureCategory.Provider,
            Attempt(failure: AiFailureCategory.Provider, safetyBlocked: true));

        Assert.Equal(AiUsageOutcome.SafetyBlocked, Assert.Single(await LedgerAsync()).Outcome);
    }

    /// <summary>
    /// USAGE-005: "we do not know what this cost" has to be visible rather than absent. The entry exists, its
    /// counts are null rather than zero, and the flag says the provider reported nothing.
    /// </summary>
    [Fact]
    public async Task An_attempt_with_no_reported_usage_posts_as_unreported_not_as_zero()
    {
        await SettleFailureAsync(
            WorkspaceA, MembershipInA, AiFailureCategory.Timeout, Attempt(failure: AiFailureCategory.Timeout));

        var entry = Assert.Single(await LedgerAsync());

        Assert.False(entry.UsageReported);
        Assert.Null(entry.InputTokens);
        Assert.Null(entry.OutputTokens);
        Assert.Null(entry.TotalTokens);

        // Still billable: a call that timed out may well have been served and charged for, and treating an
        // unknown cost as free is the silent under-attribution USAGE-005 exists to prevent.
        Assert.True(entry.IsBillable);
    }

    /// <summary>
    /// Billable is false only when the attempt was structurally incapable of costing anything. These four
    /// categories all fail before a provider is reached.
    /// </summary>
    [Theory]
    [InlineData(AiFailureCategory.Validation)]
    [InlineData(AiFailureCategory.Quota)]
    [InlineData(AiFailureCategory.TemplateUnavailable)]
    [InlineData(AiFailureCategory.LeaseAbandoned)]
    public async Task An_attempt_that_never_reached_a_provider_is_not_billable(AiFailureCategory category)
    {
        await SettleFailureAsync(WorkspaceA, MembershipInA, category, Attempt(failure: category));

        var entry = Assert.Single(await LedgerAsync());

        Assert.False(entry.IsBillable);

        // Recorded all the same: an unbillable attempt is still an attempt, and an absent row would make the
        // ledger disagree with the operation's own history.
        Assert.Equal(AiUsageOutcome.Failed, entry.Outcome);
    }

    /// <summary>The inert lifecycle task never calls a model, so it can never cost anything.</summary>
    [Fact]
    public async Task A_diagnostic_task_is_not_billable()
    {
        await SettleProposalAsync(
            WorkspaceA, MembershipInA, Attempt(inputTokens: 5), taskType: AiTaskType.Diagnostic);

        Assert.False(Assert.Single(await LedgerAsync()).IsBillable);
    }

    // ---- retry -----------------------------------------------------------------------------------------

    /// <summary>
    /// A requeued operation's second run numbers its attempt after the first, and the ledger follows the same
    /// numbering — so an account is charged twice for two real attempts rather than once for a run that
    /// silently overwrote its predecessor.
    /// </summary>
    [Fact]
    public async Task A_retried_operation_posts_a_second_entry_numbered_after_the_first()
    {
        var settled = await SettleFailureAsync(
            WorkspaceA,
            MembershipInA,
            AiFailureCategory.Provider,
            Attempt(failure: AiFailureCategory.Provider, inputTokens: 10));

        var leaseToken = await RequeueAsync(settled.OperationId);

        await SettleProposalAsync(
            WorkspaceA,
            MembershipInA,
            Attempt(inputTokens: 90),
            existing: new SettledOperation(settled.OperationId, leaseToken));

        var ledger = await LedgerAsync();

        Assert.Equal(2, ledger.Count);
        Assert.Equal([1, 2], ledger.Select(entry => entry.AttemptNumber).Order());
        Assert.Equal([10, 90], ledger.OrderBy(entry => entry.AttemptNumber).Select(entry => entry.InputTokens));
        Assert.All(ledger, entry => Assert.Equal(settled.OperationId, entry.AiOperationId));
    }

    // ---- duplicate delivery ----------------------------------------------------------------------------

    /// <summary>
    /// The same attempt delivered twice posts once. The dedup read is the routine path, not the unique index:
    /// a constraint violation would roll back the caller's whole transaction and take the attempt record with
    /// it, which is precisely the state USAGE-001 forbids.
    /// </summary>
    [Fact]
    public async Task A_duplicate_delivery_posts_once()
    {
        var settled = await SettleProposalAsync(WorkspaceA, MembershipInA, Attempt(inputTokens: 33));

        var restaged = await StageDirectlyAsync(settled.OperationId, attemptNumber: 1);

        Assert.Equal(0, restaged);
        var entry = Assert.Single(await LedgerAsync());
        Assert.Equal(33, entry.InputTokens);
    }

    [Fact]
    public async Task A_new_attempt_number_for_the_same_operation_still_posts()
    {
        var settled = await SettleProposalAsync(WorkspaceA, MembershipInA, Attempt());

        var staged = await StageDirectlyAsync(settled.OperationId, attemptNumber: 2);

        Assert.Equal(1, staged);
        Assert.Equal(2, (await LedgerAsync()).Count);
    }

    // ---- one account, two workspaces -------------------------------------------------------------------

    /// <summary>
    /// The question the existing AI tables structurally cannot answer. One person, two workspaces, two
    /// memberships — and one ledger that reads across both without <c>IgnoreQueryFilters()</c>, because the
    /// entry is not workspace-owned in the first place.
    /// </summary>
    [Fact]
    public async Task One_account_working_in_two_workspaces_keeps_one_ledger_across_both()
    {
        await SettleProposalAsync(WorkspaceA, MembershipInA, Attempt(inputTokens: 100));
        await SettleProposalAsync(WorkspaceB, MembershipInB, Attempt(inputTokens: 250));

        var ledger = await LedgerAsync();

        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, entry => Assert.Equal(Account, entry.AccountId));
        Assert.Equal([WorkspaceA, WorkspaceB], ledger.Select(entry => entry.WorkspaceId).Order());
        Assert.Equal(350, ledger.Sum(entry => entry.InputTokens));
    }

    /// <summary>
    /// The same read from inside a resolved workspace still sees both. The ledger carries no query filter, so
    /// a workspace scope neither narrows nor is needed — which is the property that lets 9A.8 answer a
    /// creator's whole-account total from any request.
    /// </summary>
    [Fact]
    public async Task A_resolved_workspace_scope_does_not_narrow_the_ledger()
    {
        await SettleProposalAsync(WorkspaceA, MembershipInA, Attempt());
        await SettleProposalAsync(WorkspaceB, MembershipInB, Attempt());

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA, MembershipInA);

        var visible = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, visible.Count);
    }

    // ---- attribution guard -----------------------------------------------------------------------------

    /// <summary>
    /// An invariant rather than a branch: the worker resolves its scope from the operation's own requester, so
    /// the two always agree. It throws rather than charging whoever holds the scope, because a mis-attributed
    /// entry is worse than a failed write — the write is retried, the wrong bill is not noticed.
    /// </summary>
    [Fact]
    public async Task Attempts_are_refused_when_the_scope_is_not_the_operations_requester()
    {
        var requested = await RequestAsync(WorkspaceA, MembershipInA, AiTaskType.RecipeConcepts);
        var claim = await ClaimAsync();

        using var scope = _provider.CreateScope();

        // Same workspace, a different member of it — the shape a future caller could plausibly have.
        Resolve(scope, WorkspaceA, MembershipInB);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>().StoreProposalAsync(
                requested, claim!.LeaseToken, Proposal(WorkspaceA, requested), [Attempt()],
                TestContext.Current.CancellationToken));

        Assert.Empty(await LedgerAsync());
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private sealed record SettledOperation(Guid OperationId, Guid LeaseToken);

    private async Task<SettledOperation> SettleProposalAsync(
        Guid workspaceId,
        Guid membershipId,
        AiAttemptRecord attempt,
        AiTaskType taskType = AiTaskType.RecipeConcepts,
        SettledOperation? existing = null)
    {
        var settled = existing ?? await ClaimedAsync(workspaceId, membershipId, taskType);

        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, membershipId);

        var outcome = await scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>()
            .StoreProposalAsync(
                settled.OperationId,
                settled.LeaseToken,
                Proposal(workspaceId, settled.OperationId),
                [attempt],
                TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationWriteOutcome.Applied, outcome);

        return settled;
    }

    private async Task<SettledOperation> SettleFailureAsync(
        Guid workspaceId, Guid membershipId, AiFailureCategory category, AiAttemptRecord attempt)
    {
        var settled = await ClaimedAsync(workspaceId, membershipId, AiTaskType.RecipeConcepts);

        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, membershipId);

        var outcome = await scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>()
            .FailAsync(
                settled.OperationId,
                settled.LeaseToken,
                category,
                "sanitized",
                [attempt],
                TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationWriteOutcome.Applied, outcome);

        return settled;
    }

    private async Task<SettledOperation> ClaimedAsync(Guid workspaceId, Guid membershipId, AiTaskType taskType)
    {
        var operationId = await RequestAsync(workspaceId, membershipId, taskType);
        var claim = await ClaimAsync();

        Assert.NotNull(claim);
        Assert.Equal(operationId, claim.OperationId);

        return new SettledOperation(operationId, claim.LeaseToken);
    }

    private async Task<Guid> RequestAsync(Guid workspaceId, Guid membershipId, AiTaskType taskType)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId, membershipId);

        var request = await scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>().RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspaceId,
                TaskType = taskType,
                Scope = AiOperationScope.WholeRecipe,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = Guid.NewGuid().ToString(),
                RequestedByMembershipId = membershipId,
                RequestedAt = _clock.UtcNow,
                StatusChangedAt = _clock.UtcNow,
                AvailableAt = _clock.UtcNow,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationRequestOutcome.Created, request.Outcome);

        return request.Operation!.Id;
    }

    private async Task<AiOperationClaim?> ClaimAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AiOperationClaimRepository>()
            .ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
    }

    /// <summary>Puts a settled operation back on the queue the way lease recovery does, and re-claims it.</summary>
    private async Task<Guid> RequeueAsync(Guid operationId)
    {
        using (var scope = _provider.CreateScope())
        {
            Resolve(scope, WorkspaceA, MembershipInA);
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

            var operation = await db.AiOperations.SingleAsync(
                row => row.Id == operationId, TestContext.Current.CancellationToken);

            operation.Status = AiOperationStatus.Requested;
            operation.FailureCategory = null;
            operation.CompletedAt = null;
            operation.LeasedBy = null;
            operation.LeaseExpiresAt = null;
            operation.AvailableAt = _clock.UtcNow;

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var claim = await ClaimAsync();
        Assert.NotNull(claim);

        return claim.LeaseToken;
    }

    /// <summary>
    /// Posts through the module's own facade, which is how a reconciliation pass or a redelivered message
    /// would arrive — the AI write path itself is already replay-safe through the operation's lease.
    /// </summary>
    private async Task<int> StageDirectlyAsync(Guid operationId, int attemptNumber)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA, MembershipInA);

        var staged = await scope.ServiceProvider.GetRequiredService<IAiUsageRecordingFacade>()
            .StageAttemptsAsync(
                new AiUsageAttributionServiceModel(Account, WorkspaceA, operationId, AiTaskType.RecipeConcepts),
                [
                    new AiUsageAttemptServiceModel
                    {
                        AttemptNumber = attemptNumber,
                        OccurredAt = _clock.UtcNow,
                        ProviderName = "test-provider",
                        ModelName = "test-model",
                        Outcome = AiUsageOutcome.Succeeded,
                        IsBillable = true,
                    },
                ],
                TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        return staged;
    }

    private async Task<List<AccountAiUsageEntry>> LedgerAsync()
    {
        using var scope = _provider.CreateScope();

        // No IgnoreQueryFilters and no resolved workspace: the ledger carries no filter, so it is readable
        // before a workspace exists. That is the property, not a convenience.
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AiExecutionMetadata>> ExecutionRecordsAsync()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA, MembershipInA);

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AiExecutionMetadata.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>What the worker does before touching anything, with this file's one account behind both members.</summary>
    private static void Resolve(IServiceScope scope, Guid workspaceId, Guid membershipId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "a" : "b",
            membershipId,
            WorkspaceRole.Owner,
            Account);

    private AiProposal Proposal(Guid workspaceId, Guid operationId) => new()
    {
        WorkspaceId = workspaceId,
        AiOperationId = operationId,
        OutputSchemaVersion = "fixture.v1",
        PromptTemplateId = "fixture.concepts",
        PromptTemplateVersion = "1.0.0",
        PromptTemplateBodyChecksum = "sha256:abc",
        ProviderName = "test-provider",
        ModelName = "test-model",
        CreatedAt = _clock.UtcNow,
    };

    private AiAttemptRecord Attempt(
        int? inputTokens = null,
        int? outputTokens = null,
        AiFailureCategory? failure = null,
        bool safetyBlocked = false) => new()
    {
        AttemptNumber = 1,
        ProviderName = "test-provider",
        ModelName = "test-model",
        ModelDeployment = "test-deployment",
        PromptTemplateId = "fixture.concepts",
        PromptTemplateVersion = "1.0.0",
        StartedAt = _clock.UtcNow,
        CompletedAt = _clock.UtcNow,
        LatencyMilliseconds = 10,
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        EstimatedCost = inputTokens is null ? null : 0.001m,
        SafetyBlocked = safetyBlocked,
        FailureCategory = failure,
        FailureSummary = failure is null ? null : "sanitized",
        CorrelationId = Guid.NewGuid(),
    };

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
