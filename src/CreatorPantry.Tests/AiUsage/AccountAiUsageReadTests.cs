extern alias ApiService;

using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The account's own read of its AI allowance (USAGE-008): what it has left, what it went on, and which
/// workspaces it may still see the names of.
/// </summary>
public sealed class AccountAiUsageReadTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid WorkspaceC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private const string Account = "user-sam";
    private const string OtherAccount = "user-alex";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

    /// <summary>The period every seeded figure below belongs to: September, in the platform default zone.</summary>
    private static readonly DateTimeOffset PeriodStart = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public AccountAiUsageReadTests()
    {
        _connection.Open();

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .AddDbContext<CreatorPantryDbContext>(options => options
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .BuildServiceProvider(validateScopes: true);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.Database.EnsureCreated();

        db.Workspaces.AddRange(
            Workspace(WorkspaceA, "Cozy Fall", "cozy-fall"),
            Workspace(WorkspaceB, "Bakery Notes", "bakery-notes"),
            Workspace(WorkspaceC, "Old Studio", "old-studio"));

        db.Users.AddRange(User(Account, "Sam"), User(OtherAccount, "Alex"));

        db.WorkspaceMemberships.AddRange(
            Membership(WorkspaceA, Account, WorkspaceMembershipStatus.Active),
            Membership(WorkspaceB, Account, WorkspaceMembershipStatus.Active),

            // Sam worked here and has since been removed. The rule this file is mostly about.
            Membership(WorkspaceC, Account, WorkspaceMembershipStatus.Removed),
            Membership(WorkspaceA, OtherAccount, WorkspaceMembershipStatus.Active));

        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- the running period ----------------------------------------------------------------------------

    [Fact]
    public async Task An_account_that_has_never_run_anything_sees_a_full_allowance()
    {
        var usage = await CurrentAsync();

        Assert.Equal(AiQuotaUnit.Credits, usage.Period.Unit);
        Assert.Equal(1000m, usage.Period.Allowance);
        Assert.Equal(0m, usage.Period.Consumed);
        Assert.Equal(0m, usage.Period.Reserved);
        Assert.Equal(1000m, usage.Period.Remaining);
        Assert.Equal(PeriodStart, usage.Period.StartsAt);
        Assert.Equal(PeriodEnd, usage.Period.ResetsAt);
        Assert.False(usage.IsSuspended);
        Assert.Empty(usage.ByTask);
        Assert.Empty(usage.ByWorkspace);
    }

    /// <summary>
    /// A read writes nothing. An account looking at its allowance must not be the thing that opens the period
    /// it is looking at — a GET that wrote a row would be a surprising thing for a GET to do.
    /// </summary>
    [Fact]
    public async Task Reading_the_current_period_opens_no_period()
    {
        await CurrentAsync();
        await CurrentAsync();

        Assert.Empty(await PeriodsAsync());
    }

    [Fact]
    public async Task The_running_period_reports_what_is_settled_and_what_is_still_held()
    {
        var periodId = await SeedPeriodAsync(consumed: 240m, reserved: 20m, carriedOver: 30m);

        var usage = await CurrentAsync();

        // The spendable total, not the bare allowance: the period's own 1000 plus the 30 that rolled in.
        Assert.Equal(1030m, usage.Period.Allowance);
        Assert.Equal(30m, usage.Period.CarriedOver);
        Assert.Equal(240m, usage.Period.Consumed);
        Assert.Equal(20m, usage.Period.Reserved);
        Assert.Equal(770m, usage.Period.Remaining);
        Assert.NotEqual(Guid.Empty, periodId);
    }

    /// <summary>
    /// One word, one number, across every payload about the same period. A quota refusal publishes
    /// <c>allowance</c> as the spendable total; a usage panel and a refusal toast open at the same moment must
    /// not disagree about what the creator's allowance is.
    /// </summary>
    [Fact]
    public async Task Allowance_means_the_same_here_as_it_does_in_a_refusal()
    {
        await SeedPeriodAsync(consumed: 0m, carriedOver: 30m);

        var usage = await CurrentAsync();

        Assert.Equal(usage.Period.Allowance, usage.Period.Remaining);
        Assert.Equal(
            usage.Period.Allowance - usage.Period.Consumed - usage.Period.Reserved, usage.Period.Remaining);
    }

    /// <summary>
    /// The boundaries are local midnights in the account's own quota zone, so the zone travels with them —
    /// api-contract.md asks for an explicit local-zone field wherever a schedule depends on one. Without it a
    /// browser renders the reset on the wrong calendar day for anyone whose zone is not the quota's.
    /// </summary>
    [Fact]
    public async Task The_period_carries_the_zone_its_boundaries_were_computed_in()
    {
        var usage = await CurrentAsync();

        Assert.Equal("Etc/UTC", usage.Period.TimeZoneId);
        Assert.Equal(AiQuotaPeriodLength.Monthly, usage.Period.PeriodLength);
    }

    [Fact]
    public async Task A_suspended_account_is_told_so()
    {
        await SeedQuotaAsync(suspended: true);

        Assert.True((await CurrentAsync()).IsSuspended);
    }

    // ---- the breakdowns --------------------------------------------------------------------------------

    /// <summary>
    /// Both breakdowns are in the period's unit and both sum to what it consumed. A breakdown in a different
    /// unit from the total it explains is one nobody can check.
    /// </summary>
    [Fact]
    public async Task Both_breakdowns_sum_to_what_the_period_consumed()
    {
        var periodId = await SeedPeriodAsync(consumed: 90m);

        await SeedRunAsync(periodId, WorkspaceA, AiTaskType.RecipeConcepts, amount: 50m, attempts: 2);
        await SeedRunAsync(periodId, WorkspaceB, AiTaskType.RecipeRevision, amount: 30m, attempts: 1);
        await SeedRunAsync(periodId, WorkspaceA, AiTaskType.RecipeRevision, amount: 10m, attempts: 1);

        var usage = await CurrentAsync();

        Assert.Equal(90m, usage.Period.Consumed);
        Assert.Equal(90m, usage.ByTask.Sum(row => row.Amount));
        Assert.Equal(90m, usage.ByWorkspace.Sum(row => row.Amount));

        Assert.Equal(
            [(AiTaskType.RecipeConcepts, 50m, 2), (AiTaskType.RecipeRevision, 40m, 2)],
            usage.ByTask.Select(row => (row.TaskType, row.Amount, row.Requests)).OrderBy(row => row.TaskType));
    }

    [Fact]
    public async Task The_breakdowns_are_ordered_by_what_cost_the_most()
    {
        var periodId = await SeedPeriodAsync(consumed: 90m);

        await SeedRunAsync(periodId, WorkspaceA, AiTaskType.RecipeConcepts, amount: 10m, attempts: 1);
        await SeedRunAsync(periodId, WorkspaceB, AiTaskType.RecipeRevision, amount: 80m, attempts: 1);

        var usage = await CurrentAsync();

        Assert.Equal(AiTaskType.RecipeRevision, usage.ByTask[0].TaskType);
        Assert.Equal(WorkspaceB, usage.ByWorkspace[0].WorkspaceId);
    }

    /// <summary>
    /// The consequence of measuring the breakdown in credits, stated as a test so it is a decision rather than
    /// a surprise: attempts from before allowances existed were never charged against one, so they show real
    /// request counts against a zero amount. That is accurate — those runs cost the account nothing.
    /// </summary>
    [Fact]
    public async Task Attempts_that_were_never_charged_show_their_requests_against_no_amount()
    {
        await SeedPeriodAsync(consumed: 0m);
        await SeedLedgerEntryAsync(Guid.NewGuid(), WorkspaceA, AiTaskType.RecipeConcepts, attemptNumber: 1);

        var usage = await CurrentAsync();

        var task = Assert.Single(usage.ByTask);
        Assert.Equal(1, task.Requests);
        Assert.Equal(0m, task.Amount);

        var workspace = Assert.Single(usage.ByWorkspace);
        Assert.Equal(1, workspace.Requests);
        Assert.Equal(0m, workspace.Amount);
        Assert.Equal("Cozy Fall", workspace.WorkspaceName);
    }

    // ---- one account, many workspaces ------------------------------------------------------------------

    /// <summary>
    /// The question a workspace-scoped route structurally cannot answer, and the reason this one is not nested
    /// under a workspace: one balance, spent in several places, shown in one answer.
    /// </summary>
    [Fact]
    public async Task One_account_sees_every_workspace_it_spent_in()
    {
        var periodId = await SeedPeriodAsync(consumed: 60m);

        await SeedRunAsync(periodId, WorkspaceA, AiTaskType.RecipeConcepts, amount: 40m, attempts: 3);
        await SeedRunAsync(periodId, WorkspaceB, AiTaskType.RecipeConcepts, amount: 20m, attempts: 1);

        var usage = await CurrentAsync();

        Assert.Equal(
            [("Bakery Notes", 20m), ("Cozy Fall", 40m)],
            usage.ByWorkspace
                .Select(row => (row.WorkspaceName, row.Amount))
                .OrderBy(row => row.WorkspaceName));
    }

    /// <summary>
    /// Another account's spend in the same workspace is not this account's. The read is keyed by account and
    /// the workspace is only a label on it.
    /// </summary>
    [Fact]
    public async Task Another_accounts_spend_in_the_same_workspace_is_not_included()
    {
        var mine = await SeedPeriodAsync(consumed: 40m);
        await SeedRunAsync(mine, WorkspaceA, AiTaskType.RecipeConcepts, amount: 40m, attempts: 1);

        var theirs = await SeedPeriodAsync(consumed: 500m, accountId: OtherAccount);
        await SeedRunAsync(
            theirs, WorkspaceA, AiTaskType.RecipeConcepts, amount: 500m, attempts: 9, accountId: OtherAccount);

        var usage = await CurrentAsync();

        Assert.Equal(40m, usage.Period.Consumed);
        Assert.Equal(40m, Assert.Single(usage.ByWorkspace).Amount);
        Assert.Equal(1, Assert.Single(usage.ByTask).Requests);
    }

    // ---- the withheld name -----------------------------------------------------------------------------

    /// <summary>
    /// USAGE-008's rule. The spend is the creator's own history and stays; the name is a fact about a
    /// workspace they no longer belong to, and goes.
    /// </summary>
    [Fact]
    public async Task A_revoked_membership_keeps_the_total_and_withholds_the_name()
    {
        var periodId = await SeedPeriodAsync(consumed: 70m);

        await SeedRunAsync(periodId, WorkspaceA, AiTaskType.RecipeConcepts, amount: 40m, attempts: 2);
        await SeedRunAsync(periodId, WorkspaceC, AiTaskType.RecipeConcepts, amount: 30m, attempts: 5);

        var usage = await CurrentAsync();

        var current = usage.ByWorkspace.Single(row => row.WorkspaceId == WorkspaceA);
        Assert.Equal("Cozy Fall", current.WorkspaceName);

        var left = usage.ByWorkspace.Single(row => row.WorkspaceId == WorkspaceC);
        Assert.Null(left.WorkspaceName);

        // The total survives intact, which is the half of the rule that is easy to get wrong by dropping it.
        Assert.Equal(30m, left.Amount);
        Assert.Equal(5, left.Requests);
        Assert.Equal(70m, usage.ByWorkspace.Sum(row => row.Amount));
    }

    /// <summary>An invitation nobody accepted is not current access either, so it discloses no name.</summary>
    [Fact]
    public async Task An_unaccepted_invitation_discloses_no_name()
    {
        await SetMembershipStatusAsync(WorkspaceB, WorkspaceMembershipStatus.Invited);

        var periodId = await SeedPeriodAsync(consumed: 10m);
        await SeedRunAsync(periodId, WorkspaceB, AiTaskType.RecipeConcepts, amount: 10m, attempts: 1);

        Assert.Null(Assert.Single((await CurrentAsync()).ByWorkspace).WorkspaceName);
    }

    /// <summary>
    /// A workspace the account never belonged to cannot appear at all — it would have to be in the account's
    /// own ledger to, and it is not.
    /// </summary>
    [Fact]
    public async Task A_workspace_the_account_never_worked_in_does_not_appear()
    {
        var mine = await SeedPeriodAsync(consumed: 10m);
        await SeedRunAsync(mine, WorkspaceA, AiTaskType.RecipeConcepts, amount: 10m, attempts: 1);

        var theirs = await SeedPeriodAsync(consumed: 99m, accountId: OtherAccount);
        await SeedRunAsync(
            theirs, WorkspaceB, AiTaskType.RecipeConcepts, amount: 99m, attempts: 1, accountId: OtherAccount);

        Assert.Equal(WorkspaceA, Assert.Single((await CurrentAsync()).ByWorkspace).WorkspaceId);
    }

    // ---- history ---------------------------------------------------------------------------------------

    [Fact]
    public async Task History_returns_closed_periods_newest_first()
    {
        await SeedClosedPeriodAsync(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), consumed: 700m);
        await SeedClosedPeriodAsync(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), consumed: 800m);

        var history = await HistoryAsync();

        Assert.Equal([800m, 700m], history.Select(period => period.Consumed));
        Assert.Equal(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), history[0].StartsAt);
    }

    /// <summary>
    /// The running period belongs to the other route. Returning it here as well would show a creator the same
    /// period twice, with two totals a moment apart.
    /// </summary>
    [Fact]
    public async Task History_excludes_the_running_period()
    {
        await SeedPeriodAsync(consumed: 240m);
        await SeedClosedPeriodAsync(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), consumed: 800m);

        var history = await HistoryAsync();

        Assert.Equal(800m, Assert.Single(history).Consumed);
    }

    [Fact]
    public async Task History_is_bounded_however_much_is_asked_for()
    {
        for (var month = 1; month <= 8; month++)
        {
            await SeedClosedPeriodAsync(
                new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero), consumed: month * 10m);
        }

        Assert.Equal(3, (await HistoryAsync(limit: 3)).Count);
        Assert.Equal(8, (await HistoryAsync(limit: int.MaxValue)).Count);

        // Clamped up rather than refused: a paging control that briefly sends zero is not a caller error.
        Assert.Single(await HistoryAsync(limit: 0));
    }

    /// <summary>
    /// The limit buys closed periods, not rows. Excluding the running one in memory would let it occupy a
    /// slot, so a caller asking for three would get two — and with no cursor and no total they could not tell
    /// that from having reached the end of their history.
    /// </summary>
    [Fact]
    public async Task The_running_period_does_not_eat_one_of_the_requested_slots()
    {
        await SeedPeriodAsync(consumed: 240m);

        for (var month = 5; month <= 8; month++)
        {
            await SeedClosedPeriodAsync(
                new DateTimeOffset(2026, month, 1, 0, 0, 0, TimeSpan.Zero), consumed: month * 10m);
        }

        var history = await HistoryAsync(limit: 3);

        Assert.Equal(3, history.Count);
        Assert.All(history, period => Assert.True(period.ResetsAt <= _clock.UtcNow));
    }

    [Fact]
    public async Task History_is_the_callers_own_periods_only()
    {
        await SeedClosedPeriodAsync(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), consumed: 10m);
        await SeedClosedPeriodAsync(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), consumed: 999m, accountId: OtherAccount);

        Assert.Equal(10m, Assert.Single(await HistoryAsync()).Consumed);
    }

    // ---- the account can only ever be the caller's own -------------------------------------------------

    /// <summary>
    /// USAGE-008 forbids accepting an account id from a route, body, query or header. The proof is structural:
    /// neither action binds anything that could carry one, so there is nothing to ignore and nothing to
    /// validate. A future parameter that could would fail here rather than in review.
    /// </summary>
    [Theory]
    [InlineData("GetAiUsage")]
    [InlineData("GetAiUsageHistory")]
    public void Neither_route_can_be_told_whose_usage_to_return(string action)
    {
        var parameters = typeof(ApiService::CreatorPantry.ApiService.Controllers.MeController)
            .GetMethod(action)!
            .GetParameters();

        Assert.All(parameters, parameter => Assert.True(
            parameter.ParameterType == typeof(CancellationToken) || parameter.Name == "limit",
            $"{action} binds '{parameter.Name}', which is not the paging limit or a cancellation token"));

        Assert.All(parameters, parameter => Assert.DoesNotContain(
            "account", parameter.Name!, StringComparison.OrdinalIgnoreCase));

        Assert.All(parameters, parameter => Assert.DoesNotContain(
            "user", parameter.Name!, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The routes name no account either — nothing in the template could carry one.</summary>
    [Fact]
    public void Neither_route_template_names_an_account()
    {
        var routes = typeof(ApiService::CreatorPantry.ApiService.Controllers.MeController)
            .GetMethods()
            .SelectMany(method => method.GetCustomAttributes(
                typeof(Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute), false))
            .Cast<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>()
            .Select(attribute => attribute.Template ?? string.Empty)
            .ToList();

        Assert.NotEmpty(routes);
        Assert.All(routes, template =>
        {
            Assert.DoesNotContain("account", template, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("user", template, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspace", template, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private async Task<AccountAiUsageServiceModel> CurrentAsync(string? accountId = null)
    {
        await using var scope = _provider.CreateAsyncScope();

        // No resolved workspace: the answer spans workspaces, and nothing this reads carries a filter.
        return await scope.ServiceProvider.GetRequiredService<IAiUsageReadFacade>()
            .GetCurrentAsync(accountId ?? Account, TestContext.Current.CancellationToken);
    }

    private async Task<IReadOnlyList<AccountAiUsagePeriodServiceModel>> HistoryAsync(
        int limit = AiUsagePolicy.HistoryDefaultPeriods)
    {
        await using var scope = _provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiUsageReadFacade>()
            .GetHistoryAsync(Account, limit, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedPeriodAsync(
        decimal consumed, decimal reserved = 0m, decimal carriedOver = 0m, string accountId = Account)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var period = Period(accountId, PeriodStart, PeriodEnd, consumed, reserved, carriedOver);
        db.AccountAiQuotaPeriods.Add(period);

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return period.Id;
    }

    private async Task<Guid> SeedClosedPeriodAsync(
        DateTimeOffset startsAt, decimal consumed, string accountId = Account)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var period = Period(accountId, startsAt, startsAt.AddMonths(1), consumed, 0m, 0m);
        period.SettledAt = period.EndsAt;

        db.AccountAiQuotaPeriods.Add(period);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return period.Id;
    }

    /// <summary>
    /// One settled, posted run: the reservation that carries what it was charged, and the ledger entries that
    /// carry where it happened and how many provider calls it took.
    /// </summary>
    private async Task SeedRunAsync(
        Guid periodId,
        Guid workspaceId,
        AiTaskType taskType,
        decimal amount,
        int attempts,
        string accountId = Account)
    {
        var operationId = Guid.NewGuid();

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotaReservations.Add(new AccountAiQuotaReservation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            PeriodId = periodId,
            AiOperationId = operationId,
            LeaseToken = Guid.NewGuid(),
            TaskType = taskType,
            Unit = AiQuotaUnit.Credits,
            ReservedAmount = Math.Max(amount, 1m),
            PerAttemptEstimate = Math.Max(amount, 1m),
            SettledAmount = amount,
            UsageReported = true,
            Status = AiQuotaReservationStatus.Settled,
            HeldAt = PeriodStart.AddDays(1),
            ExpiresAt = PeriodStart.AddDays(1).AddMinutes(10),
            SettledAt = PeriodStart.AddDays(1),
            ReleasedAt = PeriodStart.AddDays(1),
            PostedAt = PeriodStart.AddDays(1),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await SeedLedgerEntryAsync(operationId, workspaceId, taskType, attempt, accountId);
        }
    }

    private async Task SeedLedgerEntryAsync(
        Guid operationId,
        Guid workspaceId,
        AiTaskType taskType,
        int attemptNumber,
        string accountId = Account)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiUsageEntries.Add(new AccountAiUsageEntry
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            OccurredAt = PeriodStart.AddDays(1),
            AiOperationId = operationId,
            AttemptNumber = attemptNumber,
            TaskType = taskType,
            WorkspaceId = workspaceId,
            ProviderName = "test-provider",
            ModelName = "test-model",
            InputTokens = 100,
            IsBillable = true,
            UsageReported = true,
            Outcome = AiUsageOutcome.Succeeded,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedQuotaAsync(bool suspended)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = Account,
            Unit = AiQuotaUnit.Credits,
            Allowance = 1000m,
            PeriodLength = AiQuotaPeriodLength.Monthly,
            PeriodAnchor = 1,
            TimeZoneId = "Etc/UTC",
            CarryOver = AiQuotaCarryOver.None,
            IsSuspended = suspended,
            EffectiveFrom = PeriodStart.AddDays(-30),
            LastChangedAt = PeriodStart.AddDays(-30),
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SetMembershipStatusAsync(Guid workspaceId, WorkspaceMembershipStatus status)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var membership = await db.WorkspaceMemberships.SingleAsync(
            row => row.WorkspaceId == workspaceId && row.UserId == Account,
            TestContext.Current.CancellationToken);

        membership.Status = status;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<AccountAiQuotaPeriod>> PeriodsAsync()
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaPeriods.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    private static AccountAiQuotaPeriod Period(
        string accountId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        decimal consumed,
        decimal reserved,
        decimal carriedOver) => new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            TimeZoneId = "Etc/UTC",
            LocalStartDate = DateOnly.FromDateTime(startsAt.UtcDateTime),
            Unit = AiQuotaUnit.Credits,
            Allowance = 1000m,
            CarriedOver = carriedOver,
            Consumed = consumed,
            Reserved = reserved,
        };

    private Workspace Workspace(Guid id, string name, string slug) =>
        new() { Id = id, Name = name, Slug = slug, CreatedAt = _clock.UtcNow };

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

    private WorkspaceMembership Membership(Guid workspaceId, string userId, WorkspaceMembershipStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspaceId,
            UserId = userId,
            Role = WorkspaceRole.Contributor,
            Status = status,
            JoinedAt = _clock.UtcNow,
        };

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
