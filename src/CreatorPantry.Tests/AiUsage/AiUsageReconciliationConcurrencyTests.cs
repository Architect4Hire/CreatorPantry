using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The one property of the reconciliation backfill that only a real database can demonstrate: two passes
/// reading the same unposted attempt before either saves post it exactly once between them (USAGE-002).
/// </summary>
/// <remarks>
/// <para>
/// <strong>SQL Server rather than SQLite, and not as a preference.</strong> Two <c>RunPassAsync</c> calls
/// racing over one open <c>SqliteConnection</c> serialize (or throw "database is locked") at the connection
/// rather than the row, which would make this test pass or fail for a reason that has nothing to do with
/// <c>UX_AccountAiUsageEntries_Operation_Attempt</c>. <see cref="AccountAiQuotaConcurrencyTests"/> gives the
/// same reasoning for the same reason; this file is the reconciliation seam's equivalent.
/// </para>
/// <para>
/// <c>Migrate()</c> rather than <c>EnsureCreated()</c>, for the reason <c>SqlServerRecipeFixture</c> gives: the
/// schema under test is the one a deployment actually applies.
/// </para>
/// </remarks>
public sealed class AiUsageReconciliationConcurrencyTests : IAsyncLifetime
{
    private static readonly Guid WorkspaceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MembershipId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private const string Account = "user-sam";

    /// <summary>Everything seeded is older than this; the backfill's range is everything below it.</summary>
    private static readonly DateTimeOffset Cutoff = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-CU9-ubuntu-22.04").Build();

    private ServiceProvider? _provider;

    private readonly StoppedClock _clock = new();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = BuildProvider();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        await db.Database.MigrateAsync();

        db.Workspaces.Add(new Workspace
        {
            Id = WorkspaceId, Name = "Cozy Fall", Slug = "cozy-fall", CreatedAt = _clock.UtcNow,
        });

        db.Users.Add(new ApplicationUser
        {
            Id = Account,
            UserName = $"{Account}@example.com",
            NormalizedUserName = $"{Account}@EXAMPLE.COM",
            Email = $"{Account}@example.com",
            NormalizedEmail = $"{Account}@EXAMPLE.COM",
            DisplayName = "Sam",
            CreatedAt = _clock.UtcNow,
        });

        db.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            Id = MembershipId,
            WorkspaceId = WorkspaceId,
            UserId = Account,
            Role = WorkspaceRole.Contributor,
            Status = WorkspaceMembershipStatus.Active,
            JoinedAt = _clock.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// Two passes started at once, over one attempt neither has posted yet. Both read it as unposted before
    /// either saves -- the race <see cref="IAiUsageRecordingFacade.StageAttemptsAsync"/>'s own pre-check cannot
    /// close by itself, because nothing here holds a lock across that read. One of them then loses
    /// <c>UX_AccountAiUsageEntries_Operation_Attempt</c> at <c>SaveChangesAsync</c>; what this asserts is that
    /// the loser restages against a fresh read and finds the row already there, rather than surfacing the
    /// database's exception and posting nothing for an attempt that was, in fact, backfilled successfully.
    /// </summary>
    [Fact]
    public async Task Two_passes_racing_for_one_unposted_attempt_post_it_once()
    {
        var operationId = await SeedOperationAsync();
        await SeedAttemptAsync(operationId, attemptNumber: 1, inputTokens: 42);

        var summaries = await Task.WhenAll(RunPassAsync(), RunPassAsync());

        Assert.Equal(1, summaries.Sum(summary => summary.Posted));
        Assert.All(summaries, summary => Assert.Equal(0, summary.Unattributable));

        var entry = Assert.Single(await LedgerAsync());
        Assert.Equal(Account, entry.AccountId);
        Assert.Equal(operationId, entry.AiOperationId);
        Assert.Equal(1, entry.AttemptNumber);
        Assert.Equal(42, entry.InputTokens);
    }

    /// <summary>
    /// The same race, but a batch of several attempts on one operation, so a loser has something uncontested
    /// to lose alongside the row it collided on. What this guards is the earlier shape of the bug: one save
    /// covering the whole batch meant a losing pass's <em>entire</em> batch rolled back on the collision, not
    /// only the row it raced on.
    /// </summary>
    [Fact]
    public async Task A_losing_pass_still_posts_the_rest_of_its_batch()
    {
        var operationId = await SeedOperationAsync();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await SeedAttemptAsync(operationId, attempt, inputTokens: attempt * 10);
        }

        var summaries = await Task.WhenAll(RunPassAsync(), RunPassAsync());

        Assert.Equal(5, summaries.Sum(summary => summary.Posted));

        var ledger = await LedgerAsync();
        Assert.Equal(5, ledger.Count);
        Assert.Equal(Enumerable.Range(1, 5), ledger.Select(entry => entry.AttemptNumber).Order());
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddAiUsageReconciliation()
            .AddSingleton<IClock>(_clock)
            .Configure<AiUsageReconciliationOptions>(options =>
            {
                options.Before = Cutoff;
                options.BatchSize = 200;
            })
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);

    private async Task<AiUsageReconciliationSummary> RunPassAsync()
    {
        await using var scope = _provider!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiUsageReconciler>()
            .RunPassAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedOperationAsync()
    {
        using var scope = _provider!.CreateScope();
        Resolve(scope);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var operation = new AiOperation
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            TaskType = AiTaskType.RecipeConcepts,
            Scope = AiOperationScope.WholeRecipe,
            Status = AiOperationStatus.Proposed,
            IdempotencyKey = Guid.NewGuid().ToString(),
            RequestedByMembershipId = MembershipId,
            RequestedAt = Cutoff.AddDays(-2),
            StatusChangedAt = Cutoff.AddDays(-1),
            AvailableAt = Cutoff.AddDays(-2),
        };

        db.AiOperations.Add(operation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return operation.Id;
    }

    /// <summary>
    /// An execution record with no ledger entry beside it — exactly the state every attempt written before the
    /// recording seam existed is in, and the state both racing passes find.
    /// </summary>
    private async Task SeedAttemptAsync(Guid operationId, int attemptNumber, int inputTokens)
    {
        using var scope = _provider!.CreateScope();
        Resolve(scope);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var ended = Cutoff.AddDays(-1);

        db.AiExecutionMetadata.Add(new AiExecutionMetadata
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
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
            CorrelationId = Guid.NewGuid(),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<Domain.Modules.AiUsage.Data.Entities.AccountAiUsageEntry>> LedgerAsync()
    {
        using var scope = _provider!.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiUsageEntries.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static void Resolve(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            WorkspaceId, "cozy-fall", MembershipId, WorkspaceRole.Owner, Account);

    private sealed class StoppedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    }
}
