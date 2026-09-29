using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Polly.Timeout;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// <see cref="IAiOperationWorker"/>: the orchestration loop that claims a queued operation, resolves and
/// validates the workspace it belongs to, dispatches it to a task handler, and stores the outcome. Runs a
/// scripted <see cref="IAiTaskHandler"/> rather than a real generation capability -- what the handler does with
/// the claim is this module's own concern (<see cref="AiCompletionGatewayTests"/>,
/// <see cref="AiDiffCalculatorTests"/>); what this file covers is that the worker drives any handler correctly,
/// including one that calls the real provider gateway.
/// </summary>
public sealed class AiOperationWorkerTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid MembershipB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    /// <summary>The same person as <see cref="MembershipA"/>, in the other workspace.</summary>
    private static readonly Guid MembershipAInB = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    /// <summary>
    /// The Identity account behind <see cref="MembershipA"/> and <see cref="MembershipAInB"/> — which the
    /// worker resolves from the operation's own membership, never from the ambient context a test set up.
    /// </summary>
    private const string AccountA = "user-a";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly ScriptedTaskHandler _handler = new();

    public AiOperationWorkerTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddTenancy()
            .AddSingleton<IClock>(_clock)
            .AddAudit()
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddAiUsageModule()
            .AddApplicationTime()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddScoped<AiOperationClaimRepository>()
            .AddScoped<IAiOperationWorker, AiOperationWorker>()
            .AddKeyedSingleton<IAiTaskHandler>(AiTaskType.Diagnostic, _handler)

            // The same scripted handler under a second task type, because Diagnostic is the one task the
            // worker declares unmeterable -- so every quota branch below would be unreachable through it.
            .AddKeyedSingleton<IAiTaskHandler>(AiTaskType.RecipeConcepts, _handler)
            .Configure<AiQuotaOptions>(options =>
            {
                // Ten credits a call, two calls a run, one credit an input token: an admitted run holds twenty
                // and a settled charge is readable at a glance.
                options.DefaultTaskEstimate = 10m;
                options.EstimatedCallsPerRun = 2;
                options.ModelRates["test-model"] = new AiQuotaModelRate(1_000_000m, 2_000_000m);
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

        // WorkspaceMembership.UserId is a real foreign key into AspNetUsers, so a membership row needs one --
        // minimal rows rather than the whole Auth module, which nothing here exercises.
        db.Users.AddRange(
            new ApplicationUser
            {
                Id = "user-a", UserName = "user-a", NormalizedUserName = "USER-A",
                Email = "user-a@example.com", NormalizedEmail = "USER-A@EXAMPLE.COM",
                DisplayName = "User A", CreatedAt = _clock.UtcNow,
            },
            new ApplicationUser
            {
                Id = "user-b", UserName = "user-b", NormalizedUserName = "USER-B",
                Email = "user-b@example.com", NormalizedEmail = "USER-B@EXAMPLE.COM",
                DisplayName = "User B", CreatedAt = _clock.UtcNow,
            });

        // The membership a worker resolves through IWorkspaceResolutionFacade.ResolveForOperationAsync, which
        // -- unlike the request-time resolution AiOperationQueueTests exercises with a hand-resolved context --
        // is a real read against these rows.
        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership
            {
                Id = MembershipA, WorkspaceId = WorkspaceA, UserId = "user-a",
                Role = WorkspaceRole.Contributor, Status = WorkspaceMembershipStatus.Active, JoinedAt = _clock.UtcNow,
            },
            new WorkspaceMembership
            {
                Id = MembershipB, WorkspaceId = WorkspaceB, UserId = "user-b",
                Role = WorkspaceRole.Contributor, Status = WorkspaceMembershipStatus.Active, JoinedAt = _clock.UtcNow,
            },

            // user-a in the second workspace as well. Membership is the workspace-scoped identity, so one
            // person is two ids here and one account -- which is the only way to exercise USAGE-001's claim
            // that an allowance follows the person rather than the workspace.
            new WorkspaceMembership
            {
                Id = MembershipAInB, WorkspaceId = WorkspaceB, UserId = "user-a",
                Role = WorkspaceRole.Contributor, Status = WorkspaceMembershipStatus.Active, JoinedAt = _clock.UtcNow,
            });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    // ---- the empty queue --------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_queue_claims_nothing()
    {
        var summary = await RunPendingAsync();

        Assert.Equal(0, summary.Claimed);
        Assert.Equal(0, _handler.Calls);
    }

    // ---- restart ------------------------------------------------------------------------------------------

    /// <summary>
    /// A worker that claims an operation and then dies before storing an outcome leaves it Running with a
    /// lease. A later pass must not see it as queued work -- only the maintenance sweep, once the lease has
    /// lapsed, returns it to the queue for a fresh worker to pick up and finish.
    /// </summary>
    [Fact]
    public async Task An_operation_abandoned_by_a_dead_worker_is_recovered_and_completed_on_restart()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1");

        // Simulates the crash: claimed directly, bypassing IAiOperationWorker entirely, so no outcome is ever
        // stored for it -- exactly what a worker process dying mid-flight leaves behind.
        await ClaimDirectlyAsync();

        _handler.Behavior = (_, _) => Task.FromResult(Success(operationId, WorkspaceA));

        // Nothing to claim yet: the abandoned operation is still Running, and a fresh pass must not touch it.
        var tooSoon = await RunPendingAsync();
        Assert.Equal(0, tooSoon.Claimed);
        Assert.Equal(0, _handler.Calls);

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
        var maintenance = await RunMaintenanceAsync();
        Assert.Equal(1, maintenance.Requeued);

        // The recovery backoff (AiPolicy.RequeueDelayFor) pushed AvailableAt into the future, same as a real
        // requeue -- a fresh worker does not hammer an operation the instant it is recovered.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var restarted = await RunPendingAsync();

        Assert.Equal(1, restarted.Claimed);
        Assert.Equal(1, restarted.Proposed);
        Assert.Equal(1, _handler.Calls);
        Assert.Equal(AiOperationStatus.Proposed, (await LoadAsync(operationId)).Status);
    }

    // ---- duplicate delivery -----------------------------------------------------------------------------

    /// <summary>
    /// An operation another claimant already holds is not Requested any more, so the worker's own claim step
    /// cannot see it -- the guarantee <c>AiOperationClaimRepository</c>'s row-version race proves at the
    /// repository level, exercised here through the worker's own public surface.
    /// </summary>
    [Fact]
    public async Task An_operation_already_claimed_elsewhere_is_not_claimed_or_run_again()
    {
        await RequestAsync(WorkspaceA, MembershipA, "key-1");
        await ClaimDirectlyAsync();

        var summary = await RunPendingAsync();

        Assert.Equal(0, summary.Claimed);
        Assert.Equal(0, _handler.Calls);
    }

    /// <summary>
    /// A handler that ran the operation but whose lease was reclaimed before it could store an outcome must
    /// not have its result applied -- the write reports it dropped rather than silently overwriting whatever
    /// completed the operation instead.
    /// </summary>
    [Fact]
    public async Task A_handler_result_is_dropped_when_the_lease_was_lost_while_it_ran()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1");

        _handler.Behavior = async (context, ct) =>
        {
            // Simulates another worker's recovery pass reclaiming this operation while the handler was still
            // running -- the operation is put back to Requested and reclaimed with a different lease, all
            // before this handler's own StoreProposalAsync call, which is what the worker issues right after
            // this delegate returns.
            using var scope = _provider.CreateScope();
            Resolve(scope, WorkspaceA);
            var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();
            await operations.FailAsync(
                operationId, context.LeaseToken, AiFailureCategory.LeaseAbandoned, "reclaimed", [], null, ct);

            return Success(operationId, WorkspaceA);
        };

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Claimed);
        Assert.Equal(0, summary.Proposed);
        Assert.Equal(1, summary.Skipped);

        // The FailAsync the handler's own delegate issued is what stands -- not a proposal from the stale run.
        Assert.Equal(AiOperationStatus.Failed, (await LoadAsync(operationId)).Status);
        Assert.Equal(0, await CountProposalsAsync(WorkspaceA));
    }

    // ---- cancellation -------------------------------------------------------------------------------------

    /// <summary>
    /// Shutdown mid-run must not falsely mark an operation Failed or Proposed: the lease is left exactly as it
    /// was, for the maintenance sweep to recover like any other worker crash.
    /// </summary>
    [Fact]
    public async Task Cancellation_during_a_handler_leaves_the_operation_running_with_its_lease_intact()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1");

        using var cts = new CancellationTokenSource();
        _handler.Behavior = (_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => RunPendingAsync(cts.Token));

        var stored = await LoadAsync(operationId);
        Assert.Equal(AiOperationStatus.Running, stored.Status);
        Assert.NotNull(stored.LeasedBy);
        Assert.NotNull(stored.LeaseExpiresAt);
        Assert.Equal(0, await CountProposalsAsync(WorkspaceA));
    }

    /// <summary>
    /// An unexpected exception from a handler is the worker's own fault, not the task's: it is not written as
    /// a Failed operation (there is no <c>AiFailureCategory</c> for "the worker crashed"), and it does not stop
    /// the rest of the pass -- a second, working operation still gets claimed and run.
    /// </summary>
    [Fact]
    public async Task An_unexpected_handler_exception_does_not_fail_the_operation_or_stop_the_pass()
    {
        var broken = await RequestAsync(WorkspaceA, MembershipA, "key-broken");
        var working = await RequestAsync(WorkspaceB, MembershipB, "key-working");

        _handler.Behavior = (context, _) => context.OperationId == broken
            ? throw new InvalidOperationException("boom")
            : Task.FromResult(Success(context.OperationId, context.WorkspaceId));

        var summary = await RunPendingAsync();

        Assert.Equal(2, summary.Claimed);
        Assert.Equal(1, summary.Proposed);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(AiOperationStatus.Running, (await LoadAsync(broken)).Status);
        Assert.Equal(AiOperationStatus.Proposed, (await LoadAsync(working)).Status);
    }

    // ---- workspace isolation ------------------------------------------------------------------------------

    /// <summary>
    /// Two operations in two workspaces, drained in one pass: each is run against its own resolved workspace
    /// and never the other's -- proving the fresh-scope-per-claim design actually isolates them, not merely
    /// that the underlying query filter would refuse a mismatch.
    /// </summary>
    [Fact]
    public async Task Two_operations_in_two_workspaces_are_each_run_against_their_own_workspace_only()
    {
        var operationA = await RequestAsync(WorkspaceA, MembershipA, "key-a");
        var operationB = await RequestAsync(WorkspaceB, MembershipB, "key-b");

        var seen = new List<(Guid OperationId, Guid WorkspaceId)>();
        _handler.Behavior = (context, _) =>
        {
            seen.Add((context.OperationId, context.WorkspaceId));
            return Task.FromResult(Success(context.OperationId, context.WorkspaceId));
        };

        var summary = await RunPendingAsync();

        Assert.Equal(2, summary.Claimed);
        Assert.Equal(2, summary.Proposed);
        Assert.Contains((operationA, WorkspaceA), seen);
        Assert.Contains((operationB, WorkspaceB), seen);

        var proposalA = await SingleProposalAsync(WorkspaceA);
        var proposalB = await SingleProposalAsync(WorkspaceB);
        Assert.Equal(WorkspaceA, proposalA.WorkspaceId);
        Assert.Equal(WorkspaceB, proposalB.WorkspaceId);
    }

    /// <summary>
    /// A claim naming a membership that no longer resolves -- removed from the workspace after the request was
    /// queued -- cannot be written at all (the operation row is workspace-owned and unreadable without a
    /// resolved context), so it is left for the lease-expiry backstop rather than crashing the pass.
    /// </summary>
    [Fact]
    public async Task An_operation_whose_membership_no_longer_resolves_is_left_for_lease_recovery()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1");

        using (var scope = _provider.CreateScope())
        {
            Resolve(scope, WorkspaceA);
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.WorkspaceMemberships.Remove(db.WorkspaceMemberships.Single(m => m.Id == MembershipA));
            db.SaveChanges();
        }

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Claimed);
        Assert.Equal(0, summary.Proposed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, _handler.Calls);
        Assert.Equal(AiOperationStatus.Running, (await LoadAsync(operationId)).Status);
    }

    // ---- no handler registered ---------------------------------------------------------------------------

    /// <summary>
    /// A deployment/config mismatch: a task type stored on an operation (Diagnostic is the only real one that
    /// exists today) with no <see cref="IAiTaskHandler"/> registered for it in this particular host. Retrying
    /// would not fix it, so it fails outright rather than exhausting the lease-recovery bound. Uses its own
    /// provider, sharing this test's schema, with no keyed handler registered at all -- the main fixture
    /// registers one for every other test's sake.
    /// </summary>
    [Fact]
    public async Task An_operation_with_no_registered_handler_fails_immediately_rather_than_retrying()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1");

        using var provider = BuildProviderWithNoHandlers();
        using (var scope = provider.CreateScope())
        {
            var summary = await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>()
                .RunPendingAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, summary.Failed);
        }

        var stored = await LoadAsync(operationId);
        Assert.Equal(AiOperationStatus.Failed, stored.Status);
        Assert.Equal(AiFailureCategory.TemplateUnavailable, stored.FailureCategory);
        Assert.Equal(0, _handler.Calls);
    }

    /// <summary>The main fixture's own container, minus any <see cref="IAiTaskHandler"/> registration.</summary>
    private ServiceProvider BuildProviderWithNoHandlers() => new ServiceCollection()
        .AddLogging()
        .AddTenancy()
        .AddSingleton<IClock>(_clock)
        .AddAudit()
        .AddScoped<IAiOperationRepository, AiOperationRepository>()
        .AddAiUsageModule()
        .AddApplicationTime()
        .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
        .AddScoped<AiOperationClaimRepository>()
        .AddScoped<IAiOperationWorker, AiOperationWorker>()
        .AddDbContext<CreatorPantryDbContext>(options => options
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
        .BuildServiceProvider(validateScopes: true);

    // ---- invokes the provider wrapper ---------------------------------------------------------------------

    /// <summary>
    /// Proves the worker correctly drives a handler that <em>does</em> call the real
    /// <see cref="IAiCompletionGateway"/> -- against a fake <see cref="IChatClient"/>, never a network or a
    /// model -- through to a stored proposal and its execution telemetry. <see cref="DiagnosticAiTaskHandler"/>
    /// never takes this path, per its own documented contract; this is what proves the worker's contract does
    /// not depend on that choice.
    /// </summary>
    [Fact]
    public async Task A_handler_that_calls_the_provider_gateway_stores_the_proposal_and_its_attempts()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1");
        var chatClient = FakeChatClient.Returning(EmptyAnswer);

        _handler.Behavior = async (context, ct) =>
        {
            var gateway = BuildGateway(chatClient);

            var envelope = new PromptEnvelopeBuilder(context.WorkspaceId)
                .WithTask("Propose nothing; this is a fixture.")
                .WithOutputSchema(AiOutputSchema.Json)
                .Build();

            var outcome = await gateway.CompleteAsync(
                new AiCompletionRequest<AiOutputDocument>(
                    envelope, Schema, context.Scope, "fixture.worker", "1.0.0", context.CorrelationId,
                    AiOutputValidator.AsDelegate),
                ct);

            Assert.True(outcome.Succeeded, outcome.Failure?.Message);

            var assembly = AiProposalAssembler.Assemble(
                context.WorkspaceId,
                context.OperationId,
                pinnedVersionId: null,
                currentVersionId: null,
                outcome.Document!,
                diff: [],
                new AiProposalProvenance(Schema, "fixture.worker", "1.0.0", "sha256:test", "test-provider", "test-model", "test-deployment"),
                _clock.UtcNow);

            return AiTaskHandlerOutcome.ForProposal(assembly.Proposal!, outcome.Attempts);
        };

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Proposed);
        Assert.Equal(1, chatClient.Calls);

        var proposal = await SingleProposalAsync(WorkspaceA);
        Assert.Equal("test-provider", proposal.ProviderName);
        Assert.Equal("test-model", proposal.ModelName);
        Assert.Equal(1, await CountExecutionRowsAsync(WorkspaceA));
        Assert.Equal(AiOperationStatus.Proposed, (await LoadAsync(operationId)).Status);
    }

    // ---- quota admission -----------------------------------------------------------------------------------

    /// <summary>
    /// USAGE-006: admission happens before the handler, which is the thing that calls a provider. The
    /// assertion is made <em>inside</em> the handler rather than after the pass, because a hold taken
    /// afterwards would be a report rather than a control and would look identical from outside.
    /// </summary>
    [Fact]
    public async Task A_meterable_run_holds_its_allowance_before_the_handler_is_called()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1", AiTaskType.RecipeConcepts);

        AccountAiQuotaReservation? whenTheHandlerRan = null;

        _handler.Behavior = async (_, _) =>
        {
            whenTheHandlerRan = (await ReservationsAsync()).SingleOrDefault();
            return Success(operationId, WorkspaceA);
        };

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Proposed);
        Assert.NotNull(whenTheHandlerRan);
        Assert.Equal(AiQuotaReservationStatus.Held, whenTheHandlerRan.Status);
        Assert.Equal(20m, whenTheHandlerRan.ReservedAmount);

        // And by the time the pass returns it has settled and been applied: this run reached no provider, so
        // the whole hold goes back.
        var settled = Assert.Single(await ReservationsAsync());
        Assert.Equal(AiQuotaReservationStatus.Released, settled.Status);
        Assert.NotNull(settled.PostedAt);

        var period = Assert.Single(await PeriodsAsync());
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(0m, period.Consumed);
    }

    /// <summary>
    /// The whole round trip through the worker: admitted, run, charged what the provider reported, and applied
    /// to the period — with the ledger entry that describes the same attempt written in the same transaction.
    /// </summary>
    [Fact]
    public async Task A_run_that_reached_a_provider_is_charged_and_the_period_is_updated()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1", AiTaskType.RecipeConcepts);

        _handler.Behavior = (_, _) => Task.FromResult(
            AiTaskHandlerOutcome.ForProposal(
                Success(operationId, WorkspaceA).Proposal!, [Attempt(inputTokens: 12)]));

        Assert.Equal(1, (await RunPendingAsync()).Proposed);

        var reservation = Assert.Single(await ReservationsAsync());
        Assert.Equal(AiQuotaReservationStatus.Settled, reservation.Status);
        Assert.True(reservation.UsageReported);
        Assert.Equal(12m, reservation.SettledAmount);
        Assert.NotNull(reservation.PostedAt);

        var period = Assert.Single(await PeriodsAsync());
        Assert.Equal(0m, period.Reserved);
        Assert.Equal(12m, period.Consumed);

        Assert.Equal(1, await CountLedgerEntriesAsync());
    }

    /// <summary>
    /// The refusal USAGE-006 asks for: before the operation runs and before any provider call. Terminal rather
    /// than requeued — a requeue would burn the attempt bound and then report <c>LeaseAbandoned</c>, which
    /// names the wrong reason, and would re-ask a question whose answer cannot change until the period resets.
    /// </summary>
    [Fact]
    public async Task An_over_quota_run_fails_before_the_handler_is_ever_called()
    {
        await GiveQuotaAsync(allowance: 5m);
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1", AiTaskType.RecipeConcepts);

        var summary = await RunPendingAsync();

        Assert.Equal(1, summary.Failed);
        Assert.Equal(0, _handler.Calls);

        var stored = await LoadAsync(operationId);
        Assert.Equal(AiOperationStatus.Failed, stored.Status);
        Assert.Equal(AiFailureCategory.Quota, stored.FailureCategory);

        // A refusal spends nothing and bills nothing: no hold, no attempt row, no ledger entry.
        Assert.Empty(await ReservationsAsync());
        Assert.Equal(0, await CountExecutionRowsAsync(WorkspaceA));
        Assert.Equal(0, await CountLedgerEntriesAsync());
    }

    /// <summary>
    /// A suspension is its own category, because it has no reset time to offer and asking again cannot change
    /// the answer (USAGE-007). No period is even opened for it.
    /// </summary>
    [Fact]
    public async Task A_suspended_account_fails_the_run_under_its_own_category()
    {
        await GiveQuotaAsync(allowance: 1000m, suspended: true);
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1", AiTaskType.RecipeConcepts);

        Assert.Equal(1, (await RunPendingAsync()).Failed);
        Assert.Equal(0, _handler.Calls);

        Assert.Equal(AiFailureCategory.AccountSuspended, (await LoadAsync(operationId)).FailureCategory);
        Assert.Empty(await PeriodsAsync());
    }

    /// <summary>
    /// A diagnostic run never reaches a provider, so it holds nothing and no period is opened for it. Making
    /// it queue behind an allowance would be a refusal nothing earned.
    /// </summary>
    [Fact]
    public async Task A_diagnostic_run_holds_no_allowance_at_all()
    {
        var operationId = await RequestAsync(WorkspaceA, MembershipA, "key-1");
        _handler.Behavior = (_, _) => Task.FromResult(Success(operationId, WorkspaceA));

        Assert.Equal(1, (await RunPendingAsync()).Proposed);

        Assert.Empty(await ReservationsAsync());
        Assert.Empty(await PeriodsAsync());
    }

    /// <summary>
    /// One person, two workspaces, one allowance. Both runs spend the same period — which is the property
    /// USAGE-001 asks for and the one the workspace-scoped AI tables structurally cannot express.
    /// </summary>
    [Fact]
    public async Task Two_workspaces_one_account_spend_one_allowance()
    {
        await RequestAsync(WorkspaceA, MembershipA, "key-a", AiTaskType.RecipeConcepts);
        await RequestAsync(WorkspaceB, MembershipAInB, "key-b", AiTaskType.RecipeConcepts);

        _handler.Behavior = (context, _) => Task.FromResult(
            AiTaskHandlerOutcome.ForProposal(
                Success(context.OperationId, context.WorkspaceId).Proposal!, [Attempt(inputTokens: 5)]));

        Assert.Equal(2, (await RunPendingAsync()).Proposed);

        // Two memberships, two workspaces, one period — because the quota is keyed by the account behind both.
        var period = Assert.Single(await PeriodsAsync());
        Assert.Equal(AccountA, period.AccountId);
        Assert.Equal(10m, period.Consumed);

        var reservations = await ReservationsAsync();
        Assert.Equal(2, reservations.Count);
        Assert.All(reservations, reservation => Assert.Equal(AccountA, reservation.AccountId));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private const string Schema = "fixture.worker.v1";

    private const string EmptyAnswer = """
        { "schemaVersion": "fixture.worker.v1", "changes": [] }
        """;

    private static AiCompletionGateway BuildGateway(IChatClient client)
    {
        const string pipelineKey = "test-ai";

        var services = new ServiceCollection();
        services.AddResiliencePipeline(pipelineKey, pipeline => pipeline
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(30) }));
        var provider = services.BuildServiceProvider();

        return new AiCompletionGateway(
            client,
            new DefaultAiFailureClassifier(),
            new ConfiguredAiCostEstimator(new AiCostOptions()),
            new StoppedClock(),
            provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
            new AiGatewayOptions
            {
                ResiliencePipelineKey = pipelineKey,
                ProviderName = "test-provider",
                ModelName = "test-model",
                ModelDeployment = "test-deployment",
            },
            NullLogger<AiCompletionGateway>.Instance);
    }

    /// <summary>
    /// Resolves <see cref="IAiOperationWorker"/> in its own scope, disposed as soon as the pass returns -- the
    /// same lifetime <c>AiOperationWorkerHostedService</c> gives it per tick in production. Safe to dispose
    /// immediately: <see cref="AiOperationWorker"/>'s own per-operation work runs in scopes it creates itself
    /// from the container's one <c>IServiceScopeFactory</c>, not from this one.
    /// </summary>
    private async Task<AiOperationWorkerPassSummary> RunPendingAsync(CancellationToken? cancellationToken = null)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>()
            .RunPendingAsync(cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private async Task<AiOperationMaintenanceSummary> RunMaintenanceAsync()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAiOperationWorker>()
            .RunMaintenanceAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> RequestAsync(
        Guid workspaceId, Guid membershipId, string key, AiTaskType taskType = AiTaskType.Diagnostic)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var operations = scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();

        var requested = await operations.RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspaceId,
                TaskType = taskType,
                Scope = AiOperationScope.WholeRecipe,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = key,
                RequestedByMembershipId = membershipId,
                RequestedAt = _clock.UtcNow,
                StatusChangedAt = _clock.UtcNow,
                AvailableAt = _clock.UtcNow,
            },
            TestContext.Current.CancellationToken);

        return requested.Operation!.Id;
    }

    /// <summary>Claims the next due operation directly through the repository, standing in for another worker.</summary>
    private async Task ClaimDirectlyAsync()
    {
        using var scope = _provider.CreateScope();
        var claims = scope.ServiceProvider.GetRequiredService<AiOperationClaimRepository>();
        await claims.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
    }

    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "a" : "b",
            workspaceId == WorkspaceA ? MembershipA : MembershipB,
            WorkspaceRole.Owner, "test-account");

    private AiTaskHandlerOutcome Success(Guid operationId, Guid workspaceId) => AiTaskHandlerOutcome.ForProposal(
        new AiProposal
        {
            WorkspaceId = workspaceId,
            AiOperationId = operationId,
            OutputSchemaVersion = "fixture.v1",
            PromptTemplateId = "fixture.worker",
            PromptTemplateVersion = "1.0.0",
            PromptTemplateBodyChecksum = "sha256:abc",
            ProviderName = "test-provider",
            ModelName = "test-model",
            CreatedAt = _clock.UtcNow,
        },
        []);

    private async Task<AiOperation> LoadAsync(Guid operationId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(operation => operation.Id == operationId, TestContext.Current.CancellationToken);
    }

    private async Task<int> CountProposalsAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiProposals.CountAsync(TestContext.Current.CancellationToken);
    }

    private async Task<AiProposal> SingleProposalAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiProposals.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountExecutionRowsAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, workspaceId);
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiExecutionMetadata.CountAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The quota tables read without a resolved workspace and without <c>IgnoreQueryFilters</c>: nothing in the
    /// AiUsage module is workspace-owned, so there is no filter to opt out of. That is the property, not a
    /// convenience of this fixture.
    /// </summary>
    private async Task<List<AccountAiQuotaReservation>> ReservationsAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc cref="ReservationsAsync"/>
    private async Task<List<AccountAiQuotaPeriod>> PeriodsAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc cref="ReservationsAsync"/>
    private async Task<int> CountLedgerEntriesAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries.CountAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The terms this fixture's one account is spending under, for the tests that need them set.</summary>
    private async Task GiveQuotaAsync(decimal allowance, bool suspended = false)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = AccountA,
            Unit = AiQuotaUnit.Credits,
            Allowance = allowance,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = suspended,
            EffectiveFrom = _clock.UtcNow.AddDays(-30),
            LastChangedAt = _clock.UtcNow.AddDays(-30),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One provider attempt, in the terms a handler hands back.</summary>
    private AiAttemptRecord Attempt(int inputTokens) => new()
    {
        AttemptNumber = 1,
        ProviderName = "test-provider",
        ModelName = "test-model",
        PromptTemplateId = "fixture.worker",
        PromptTemplateVersion = "1.0.0",
        StartedAt = _clock.UtcNow,
        CompletedAt = _clock.UtcNow,
        LatencyMilliseconds = 10,
        InputTokens = inputTokens,
        CorrelationId = Guid.NewGuid(),
    };

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    }

    /// <summary>A handler whose behaviour is set per test, and that records how many times it ran.</summary>
    private sealed class ScriptedTaskHandler : IAiTaskHandler
    {
        public int Calls { get; private set; }

        public Func<AiTaskExecutionContext, CancellationToken, Task<AiTaskHandlerOutcome>> Behavior { get; set; } =
            (context, _) => Task.FromResult(AiTaskHandlerOutcome.ForFailure(
                AiFailureCategory.Validation, "No behavior was scripted for this test.", []));

        public Task<AiTaskHandlerOutcome> HandleAsync(AiTaskExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Behavior(context, cancellationToken);
        }
    }

    /// <summary>A scripted <see cref="IChatClient"/>: no network, no model, fully deterministic.</summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly string _text;

        private FakeChatClient(string text) => _text = text;

        public static FakeChatClient Returning(string text) => new(text);

        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _text)) { ModelId = "test-model" });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The gateway does not stream.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
