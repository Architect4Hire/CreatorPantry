using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The AI operation queue: idempotent creation, claiming, leases, atomic proposal storage, recovery, expiry,
/// and the workspace boundary around all of it.
/// </summary>
public sealed class AiOperationQueueTests : IDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Membership = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

    public AiOperationQueueTests()
    {
        _connection.Open();

        // The module's data services only. AddAiModule would drag in the provider gateway, the prompt template
        // store and a resilience pipeline, none of which the queue touches — and a queue test that needed a
        // model provider registered would be describing the wrong dependency.
        _provider = new ServiceCollection()
            .AddTenancy()
            .AddSingleton<IClock>(_clock)

            // The data layer writes an audit entry when a disposition is recorded, which is a write this file
            // does not exercise — but a constructor dependency is not optional, and faking one here rather than
            // registering the real writer would only hide whether the entry is staged on the same context.
            .AddAudit()
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

    // ---- replay ----------------------------------------------------------------------------------------

    [Fact]
    public async Task One_key_and_one_payload_produce_one_operation()
    {
        var first = await RequestAsync(WorkspaceA, "key-1");
        var second = await RequestAsync(WorkspaceA, "key-1");

        Assert.Equal(AiOperationRequestOutcome.Created, first.Outcome);
        Assert.Equal(AiOperationRequestOutcome.Replayed, second.Outcome);
        Assert.Equal(first.Operation!.Id, second.Operation!.Id);
        Assert.Equal(1, await CountAsync(WorkspaceA));
    }

    /// <summary>
    /// The same key describing a different request is refused outright. Returning the original would answer a
    /// question nobody asked; creating a second would make the key meaningless.
    /// </summary>
    [Fact]
    public async Task One_key_and_two_payloads_are_refused()
    {
        await RequestAsync(WorkspaceA, "key-1", scope: AiOperationScope.Metadata);
        var conflicting = await RequestAsync(WorkspaceA, "key-1", scope: AiOperationScope.Ingredients);

        Assert.Equal(AiOperationRequestOutcome.KeyReusedForDifferentRequest, conflicting.Outcome);
        Assert.Null(conflicting.Operation);
        Assert.Equal(1, await CountAsync(WorkspaceA));
    }

    /// <summary>One key per workspace, so two creators choosing "retry-1" do not collide.</summary>
    [Fact]
    public async Task The_same_key_in_two_workspaces_creates_two_operations()
    {
        var a = await RequestAsync(WorkspaceA, "shared-key");
        var b = await RequestAsync(WorkspaceB, "shared-key");

        Assert.Equal(AiOperationRequestOutcome.Created, a.Outcome);
        Assert.Equal(AiOperationRequestOutcome.Created, b.Outcome);
        Assert.NotEqual(a.Operation!.Id, b.Operation!.Id);
    }

    // ---- competing workers -----------------------------------------------------------------------------

    /// <summary>
    /// Two workers reaching one queued operation: exactly one wins. The row version decides, and the loser
    /// gets nothing rather than overwriting the winner's claim.
    /// </summary>
    [Fact]
    public async Task Two_workers_cannot_claim_the_same_operation()
    {
        await RequestAsync(WorkspaceA, "key-1");

        using var firstScope = _provider.CreateScope();
        using var secondScope = _provider.CreateScope();

        var firstClaim = Claim(firstScope);
        var secondClaim = Claim(secondScope);

        var first = await firstClaim.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
        var second = await secondClaim.ClaimNextAsync(Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task A_claim_moves_the_operation_to_running_and_counts_the_attempt()
    {
        await RequestAsync(WorkspaceA, "key-1");

        var claim = await ClaimOneAsync();

        Assert.NotNull(claim);
        Assert.Equal(1, claim!.Attempts);

        var stored = await LoadAsync(claim.OperationId);
        Assert.Equal(AiOperationStatus.Running, stored.Status);
        Assert.Equal(claim.LeaseToken, stored.LeasedBy);
        Assert.NotNull(stored.LeaseExpiresAt);
        Assert.NotNull(stored.StartedAt);
    }

    /// <summary>
    /// The carve-out's constraint: a claim returns identifiers, never creator content. There is nowhere on the
    /// result to put a title or a snapshot, and that is what makes reading across workspaces safe.
    /// </summary>
    [Fact]
    public void A_claim_carries_no_creator_content()
    {
        var carried = typeof(AiOperationClaim).GetProperties().Select(property => property.Name).Order().ToArray();

        Assert.Equal(["Attempts", "LeaseToken", "OperationId", "WorkspaceId"], carried);
        Assert.All(
            typeof(AiOperationClaim).GetProperties(),
            property => Assert.True(
                property.PropertyType == typeof(Guid) || property.PropertyType == typeof(int),
                $"{property.Name} is {property.PropertyType.Name}; a claim carries only ids and counts"));
    }

    [Fact]
    public async Task An_operation_not_yet_available_is_not_claimed()
    {
        var request = await RequestAsync(WorkspaceA, "key-1");
        await SetAsync(request.Operation!.Id, operation => operation.AvailableAt = _clock.UtcNow.AddMinutes(5));

        Assert.Null(await ClaimOneAsync());
    }

    // ---- lease expiry ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_abandoned_lease_returns_the_operation_to_the_queue()
    {
        await RequestAsync(WorkspaceA, "key-1");
        var claim = await ClaimOneAsync();

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
        var (requeued, abandoned) = await RecoverAsync();

        Assert.Equal(1, requeued);
        Assert.Equal(0, abandoned);

        var stored = await LoadAsync(claim!.OperationId);
        Assert.Equal(AiOperationStatus.Requested, stored.Status);
        Assert.Null(stored.LeasedBy);
        Assert.Null(stored.LeaseExpiresAt);

        // Still set: the operation genuinely started once, and clearing it would erase that.
        Assert.NotNull(stored.StartedAt);

        // Backed off, so the next worker does not re-claim it instantly.
        Assert.True(stored.AvailableAt > _clock.UtcNow);
    }

    /// <summary>
    /// The bound. Without it a task that kills its worker every time would cycle between Running and Requested
    /// forever, spending a provider budget each pass.
    /// </summary>
    [Fact]
    public async Task An_operation_that_exhausts_its_attempts_is_abandoned_for_good()
    {
        await RequestAsync(WorkspaceA, "key-1");

        for (var pass = 1; pass <= AiPolicy.MaxAttempts; pass++)
        {
            var claim = await ClaimOneAsync();
            Assert.NotNull(claim);
            Assert.Equal(pass, claim!.Attempts);

            _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
            await RecoverAsync();
            _clock.Advance(TimeSpan.FromMinutes(20));
        }

        Assert.Null(await ClaimOneAsync());

        var stored = await SingleAsync(WorkspaceA);
        Assert.Equal(AiOperationStatus.Failed, stored.Status);
        Assert.Equal(AiFailureCategory.LeaseAbandoned, stored.FailureCategory);
        Assert.Equal(AiPolicy.MaxAttempts, stored.Attempts);
        Assert.NotNull(stored.CompletedAt);
    }

    [Fact]
    public async Task A_live_lease_is_not_recovered()
    {
        await RequestAsync(WorkspaceA, "key-1");
        await ClaimOneAsync();

        _clock.Advance(AiPolicy.LeaseDuration - TimeSpan.FromMinutes(1));

        Assert.Equal((0, 0), await RecoverAsync());
    }

    [Fact]
    public async Task Renewing_a_lease_extends_it()
    {
        await RequestAsync(WorkspaceA, "key-1");
        var claim = await ClaimOneAsync();
        var original = (await LoadAsync(claim!.OperationId)).LeaseExpiresAt;

        _clock.Advance(TimeSpan.FromMinutes(5));

        using var scope = _provider.CreateScope();
        var outcome = await DataLayer(scope, WorkspaceA).RenewLeaseAsync(
            claim.OperationId, claim.LeaseToken, TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationWriteOutcome.Applied, outcome);
        Assert.True((await LoadAsync(claim.OperationId)).LeaseExpiresAt > original);
    }

    // ---- partial failure and lease loss ----------------------------------------------------------------

    /// <summary>
    /// A worker that woke up late must not write over the claim that replaced it. Without the token check this
    /// would be a last-writer-wins race between two workers completing one operation.
    /// </summary>
    [Fact]
    public async Task A_worker_whose_lease_was_taken_cannot_store_a_proposal()
    {
        await RequestAsync(WorkspaceA, "key-1");
        var stale = await ClaimOneAsync();

        _clock.Advance(AiPolicy.LeaseDuration + TimeSpan.FromMinutes(1));
        await RecoverAsync();
        _clock.Advance(TimeSpan.FromMinutes(20));

        var fresh = await ClaimOneAsync();
        Assert.NotEqual(stale!.LeaseToken, fresh!.LeaseToken);

        using var scope = _provider.CreateScope();
        var outcome = await DataLayer(scope, WorkspaceA).StoreProposalAsync(
            stale.OperationId,
            stale.LeaseToken,
            Proposal(stale.OperationId),
            [Attempt()],
            TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationWriteOutcome.LeaseLost, outcome);
        Assert.Equal(AiOperationStatus.Running, (await LoadAsync(stale.OperationId)).Status);
        Assert.Equal(0, await CountProposalsAsync());
    }

    [Fact]
    public async Task Storing_a_proposal_writes_it_with_the_status_change_and_the_execution_rows()
    {
        await RequestAsync(WorkspaceA, "key-1");
        var claim = await ClaimOneAsync();

        using var scope = _provider.CreateScope();
        var outcome = await DataLayer(scope, WorkspaceA).StoreProposalAsync(
            claim!.OperationId,
            claim.LeaseToken,
            Proposal(claim.OperationId),
            [Attempt(), Attempt()],
            TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationWriteOutcome.Applied, outcome);

        var stored = await LoadAsync(claim.OperationId);
        Assert.Equal(AiOperationStatus.Proposed, stored.Status);
        Assert.Null(stored.LeasedBy);

        // Proposed is not terminal: the creator still has to act, so nothing is completed yet.
        Assert.Null(stored.CompletedAt);
        Assert.Equal(1, await CountProposalsAsync());
        Assert.Equal(2, await CountExecutionRowsAsync());
    }

    /// <summary>
    /// A requeued operation's second run must not collide with its first on the unique attempt index, so
    /// attempt numbers continue from what the operation already has.
    /// </summary>
    [Fact]
    public async Task Execution_attempts_keep_counting_across_a_requeue()
    {
        await RequestAsync(WorkspaceA, "key-1");
        var first = await ClaimOneAsync();

        using (var scope = _provider.CreateScope())
        {
            await DataLayer(scope, WorkspaceA).FailAsync(
                first!.OperationId,
                first.LeaseToken,
                AiFailureCategory.Provider,
                "unreachable",
                [Attempt()],
                TestContext.Current.CancellationToken);
        }

        // Reopen it the way recovery would, then run again.
        await SetAsync(first!.OperationId, operation =>
        {
            operation.Status = AiOperationStatus.Requested;
            operation.FailureCategory = null;
            operation.CompletedAt = null;
            operation.AvailableAt = _clock.UtcNow;
        });

        var second = await ClaimOneAsync();

        using (var scope = _provider.CreateScope())
        {
            await DataLayer(scope, WorkspaceA).StoreProposalAsync(
                second!.OperationId,
                second.LeaseToken,
                Proposal(second.OperationId),
                [Attempt()],
                TestContext.Current.CancellationToken);
        }

        using var read = _provider.CreateScope();
        var numbers = await Db(read, WorkspaceA).AiExecutionMetadata
            .Select(record => record.AttemptNumber)
            .OrderBy(number => number)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], numbers);
    }

    [Fact]
    public async Task Failing_an_operation_records_the_category_and_clears_the_lease()
    {
        await RequestAsync(WorkspaceA, "key-1");
        var claim = await ClaimOneAsync();

        using var scope = _provider.CreateScope();
        var outcome = await DataLayer(scope, WorkspaceA).FailAsync(
            claim!.OperationId,
            claim.LeaseToken,
            AiFailureCategory.SafetyBlocked,
            "refused by the safety filter",
            [Attempt()],
            TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationWriteOutcome.Applied, outcome);

        var stored = await LoadAsync(claim.OperationId);
        Assert.Equal(AiOperationStatus.Failed, stored.Status);
        Assert.Equal(AiFailureCategory.SafetyBlocked, stored.FailureCategory);
        Assert.Null(stored.LeasedBy);
    }

    // ---- expiry ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_nobody_claimed_expires()
    {
        await RequestAsync(WorkspaceA, "key-1");

        _clock.Advance(AiPolicy.RequestTimeToLive + TimeSpan.FromHours(1));

        Assert.Equal(1, await ExpireAsync());
        Assert.Equal(AiOperationStatus.Expired, (await SingleAsync(WorkspaceA)).Status);
    }

    [Fact]
    public async Task A_proposal_nobody_acted_on_expires()
    {
        await RequestAsync(WorkspaceA, "key-1");
        var claim = await ClaimOneAsync();

        using (var scope = _provider.CreateScope())
        {
            await DataLayer(scope, WorkspaceA).StoreProposalAsync(
                claim!.OperationId, claim.LeaseToken, Proposal(claim.OperationId), [Attempt()],
                TestContext.Current.CancellationToken);
        }

        _clock.Advance(AiPolicy.ProposalTimeToLive + TimeSpan.FromDays(1));

        Assert.Equal(1, await ExpireAsync());
        Assert.Equal(AiOperationStatus.Expired, (await LoadAsync(claim!.OperationId)).Status);
    }

    [Fact]
    public async Task A_fresh_request_does_not_expire()
    {
        await RequestAsync(WorkspaceA, "key-1");

        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, await ExpireAsync());
    }

    // ---- workspace isolation ---------------------------------------------------------------------------

    [Fact]
    public async Task An_operation_is_invisible_from_the_other_workspace()
    {
        var request = await RequestAsync(WorkspaceA, "key-1");

        using var scope = _provider.CreateScope();
        var fromB = await Repository(scope, WorkspaceB).GetAsync(
            request.Operation!.Id, TestContext.Current.CancellationToken);

        Assert.Null(fromB);
    }

    /// <summary>
    /// Completing an operation from the wrong workspace reports it missing rather than refusing it, because
    /// the query filter means it genuinely is not there — the same 404-not-403 reasoning tenancy.md applies to
    /// routes.
    /// </summary>
    [Fact]
    public async Task Another_workspace_cannot_complete_an_operation_it_cannot_see()
    {
        await RequestAsync(WorkspaceA, "key-1");
        var claim = await ClaimOneAsync();

        using var scope = _provider.CreateScope();
        var outcome = await DataLayer(scope, WorkspaceB).StoreProposalAsync(
            claim!.OperationId,
            claim.LeaseToken,
            Proposal(claim.OperationId),
            [Attempt()],
            TestContext.Current.CancellationToken);

        Assert.Equal(AiOperationWriteOutcome.NotFound, outcome);
        Assert.Equal(0, await CountProposalsAsync());
    }

    /// <summary>
    /// The claim reads across workspaces on purpose, so it must be the only thing that does. Everything the
    /// worker touches afterwards goes through the filtered context.
    /// </summary>
    [Fact]
    public async Task The_claim_finds_work_in_any_workspace()
    {
        await RequestAsync(WorkspaceB, "key-b");

        var claim = await ClaimOneAsync();

        Assert.NotNull(claim);
        Assert.Equal(WorkspaceB, claim!.WorkspaceId);
    }

    // ---- no provider call inside a transaction ---------------------------------------------------------

    /// <summary>
    /// The data layer cannot make a provider call, because it has no way to reach one. Structural rather than
    /// a convention someone has to respect when sequencing claim, call and store.
    /// </summary>
    [Fact]
    public void The_data_layer_cannot_reach_the_provider()
    {
        var dependencies = typeof(AiOperationDataLayer).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(IAiCompletionGateway), dependencies);
        Assert.DoesNotContain(typeof(Microsoft.Extensions.AI.IChatClient), dependencies);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private async Task<AiOperationRequest> RequestAsync(
        Guid workspaceId,
        string key,
        AiOperationScope scope = AiOperationScope.WholeRecipe)
    {
        using var serviceScope = _provider.CreateScope();

        return await DataLayer(serviceScope, workspaceId).RequestAsync(
            new AiOperation
            {
                WorkspaceId = workspaceId,
                TaskType = AiTaskType.Diagnostic,
                Scope = scope,
                Status = AiOperationStatus.Requested,
                IdempotencyKey = key,
                RequestedByMembershipId = Membership,
                RequestedAt = _clock.UtcNow,
                StatusChangedAt = _clock.UtcNow,
                AvailableAt = _clock.UtcNow,
            },
            TestContext.Current.CancellationToken);
    }

    private async Task<AiOperationClaim?> ClaimOneAsync()
    {
        using var scope = _provider.CreateScope();

        return await Claim(scope).ClaimNextAsync(
            Guid.NewGuid(), _clock.UtcNow, TestContext.Current.CancellationToken);
    }

    private async Task<(int Requeued, int Abandoned)> RecoverAsync()
    {
        using var scope = _provider.CreateScope();

        return await Claim(scope).RecoverAbandonedLeasesAsync(
            _clock.UtcNow, TestContext.Current.CancellationToken);
    }

    private async Task<int> ExpireAsync()
    {
        using var scope = _provider.CreateScope();

        return await Claim(scope).ExpireDueAsync(_clock.UtcNow, TestContext.Current.CancellationToken);
    }

    private static AiOperationClaimRepository Claim(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<AiOperationClaimRepository>();

    private static IAiOperationDataLayer DataLayer(IServiceScope scope, Guid workspaceId)
    {
        Resolve(scope, workspaceId);

        return scope.ServiceProvider.GetRequiredService<IAiOperationDataLayer>();
    }

    private static IAiOperationRepository Repository(IServiceScope scope, Guid workspaceId)
    {
        Resolve(scope, workspaceId);

        return scope.ServiceProvider.GetRequiredService<IAiOperationRepository>();
    }

    private static CreatorPantryDbContext Db(IServiceScope scope, Guid workspaceId)
    {
        Resolve(scope, workspaceId);

        return scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
    }

    /// <summary>
    /// What a worker does before touching anything: resolve and validate the workspace the claim named.
    /// </summary>
    private static void Resolve(IServiceScope scope, Guid workspaceId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "a" : "b",
            Membership,
            WorkspaceRole.Owner);

    private async Task<AiOperation> LoadAsync(Guid operationId)
    {
        using var scope = _provider.CreateScope();

        return await Db(scope, WorkspaceA).AiOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(operation => operation.Id == operationId, TestContext.Current.CancellationToken);
    }

    private async Task<AiOperation> SingleAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();

        return await Db(scope, workspaceId).AiOperations.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task SetAsync(Guid operationId, Action<AiOperation> change)
    {
        using var scope = _provider.CreateScope();
        var db = Db(scope, WorkspaceA);

        var operation = await db.AiOperations.IgnoreQueryFilters()
            .SingleAsync(candidate => candidate.Id == operationId, TestContext.Current.CancellationToken);

        change(operation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountAsync(Guid workspaceId)
    {
        using var scope = _provider.CreateScope();

        return await Db(scope, workspaceId).AiOperations.CountAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountProposalsAsync()
    {
        using var scope = _provider.CreateScope();

        return await Db(scope, WorkspaceA).AiProposals
            .IgnoreQueryFilters()
            .CountAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountExecutionRowsAsync()
    {
        using var scope = _provider.CreateScope();

        return await Db(scope, WorkspaceA).AiExecutionMetadata
            .IgnoreQueryFilters()
            .CountAsync(TestContext.Current.CancellationToken);
    }

    private AiProposal Proposal(Guid operationId) => new()
    {
        WorkspaceId = WorkspaceA,
        AiOperationId = operationId,
        OutputSchemaVersion = "fixture.v1",
        PromptTemplateId = "fixture.headnote",
        PromptTemplateVersion = "1.0.0",
        PromptTemplateBodyChecksum = "sha256:abc",
        ProviderName = "test-provider",
        ModelName = "test-model",
        CreatedAt = _clock.UtcNow,
    };

    private AiAttemptRecord Attempt() => new()
    {
        AttemptNumber = 1,
        ProviderName = "test-provider",
        ModelName = "test-model",
        PromptTemplateId = "fixture.headnote",
        PromptTemplateVersion = "1.0.0",
        StartedAt = _clock.UtcNow,
        CompletedAt = _clock.UtcNow,
        LatencyMilliseconds = 10,
        CorrelationId = Guid.NewGuid(),
    };

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
