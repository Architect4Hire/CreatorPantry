using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Tenancy;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// Refusing an over-quota AI request at admission (USAGE-006/007): before the operation exists, before a
/// provider is called, spending nothing to say nothing can be spent — and telling the caller what is exhausted,
/// what remains and when it resets.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <see cref="IAiConceptRequestFacade"/> and <see cref="IAiFirstDraftRequestFacade"/>, the two
/// request routes that name no recipe, over the real seam and a real database. The other six routes take the
/// same gate; that they all take it is held structurally by
/// <see cref="Every_request_seam_is_behind_the_quota_gate"/>, which is what a ninth capability will be caught
/// by. Said plainly rather than implied: these two are covered behaviourally, the rest by construction.
/// </para>
/// <para>
/// The HTTP half — which status each code maps to, and the <c>Retry-After</c> a 429 carries — is
/// <see cref="Api.QuotaProblemResultsTests"/>.
/// </para>
/// </remarks>
public sealed class AiRequestQuotaRefusalTests : IAsyncDisposable
{
    private static readonly Guid WorkspaceA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid WorkspaceB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid MembershipInA = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid MembershipInB = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    /// <summary>One person, two memberships. The allowance follows the person (USAGE-001).</summary>
    private const string Account = "user-sam";

    /// <summary>Ten credits a call, two calls a run: a request needs twenty to be admitted.</summary>
    private const decimal PerCall = 10m;

    private const decimal Required = 20m;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _provider;
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

    public AiRequestQuotaRefusalTests()
    {
        _connection.Open();

        var tasks = new AiTaskOptions();
        tasks.Enabled.Add(AiTaskCatalog.RecipeConcepts);
        tasks.Enabled.Add(AiTaskCatalog.RecipeFirstDraft);

        _provider = new ServiceCollection()
            .AddTenancy()
            .AddAudit()
            .AddApplicationTime()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .AddSingleton(tasks)
            .AddScoped<IAiOperationRepository, AiOperationRepository>()
            .AddScoped<IAiOperationDataLayer, AiOperationDataLayer>()
            .AddAiConceptRequestSeam()

            // The first-draft Business alone rather than its whole seam: AddAiFirstDraftRequestSeam also
            // registers the draft-acceptance seam, which needs the recipe module this file has no use for.
            // The gate lives in Business, so this is the layer worth reaching anyway.
            .AddScoped<IAiFirstDraftRequestBusiness, AiFirstDraftRequestBusiness>()
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultTaskEstimate = PerCall;
                options.EstimatedCallsPerRun = 2;
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
        db.SaveChanges();
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ---- the boundary ----------------------------------------------------------------------------------

    /// <summary>
    /// At the limit exactly is admitted. A balance that covers the hold to the penny is one that covers it —
    /// the same <c>&lt;=</c> the worker's own admission applies, so the two cannot disagree about where the
    /// line is and a request accepted here cannot be refused there for the same reason.
    /// </summary>
    [Fact]
    public async Task An_account_with_exactly_enough_left_is_admitted()
    {
        await SpendDownToAsync(Required);

        var outcome = await RequestConceptsAsync();

        Assert.True(outcome.Result.Succeeded);
        Assert.Single(await OperationsAsync());
    }

    [Fact]
    public async Task An_account_one_unit_short_is_refused()
    {
        await SpendDownToAsync(Required - 1m);

        var outcome = await RequestConceptsAsync();

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiProposalErrors.QuotaExhausted, outcome.Result.Error!.Code);
    }

    /// <summary>
    /// USAGE-007: what is exhausted, what remains, what this needed, and when it comes back — structured, so a
    /// client reads numbers rather than parsing an English sentence that may be reworded.
    /// </summary>
    [Fact]
    public async Task A_refusal_says_what_is_exhausted_what_remains_and_when_it_resets()
    {
        await SpendDownToAsync(5m);

        var error = (await RequestConceptsAsync()).Result.Error!;
        var extensions = error.Extensions!;

        Assert.Equal(nameof(AiQuotaUnit.Credits), extensions["unit"]);
        Assert.Equal(1000m, extensions["allowance"]);
        Assert.Equal(5m, extensions["remaining"]);
        Assert.Equal(Required, extensions["required"]);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), extensions["resetsAt"]);

        // Nothing about anybody else, and nothing about anywhere else.
        Assert.DoesNotContain("workspace", string.Join(" ", extensions.Keys), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A suspension is a different answer under a different code, and carries no balance and no reset — there
    /// is nothing to wait for, and a remaining figure would read as an invitation to try again.
    /// </summary>
    [Fact]
    public async Task A_suspended_account_is_refused_under_its_own_code_with_nothing_to_wait_for()
    {
        await GiveQuotaAsync(allowance: 1000m, suspended: true);

        var error = (await RequestConceptsAsync()).Result.Error!;

        Assert.Equal(AiProposalErrors.QuotaSuspended, error.Code);
        Assert.Equal(nameof(AiQuotaUnit.Credits), error.Extensions!["unit"]);
        Assert.DoesNotContain("resetsAt", error.Extensions.Keys);
        Assert.DoesNotContain("remaining", error.Extensions.Keys);
    }

    // ---- what a refusal costs --------------------------------------------------------------------------

    /// <summary>
    /// The restriction that gives this its shape: a refused request leaves no orphan operation and consumes no
    /// allowance. Not "cleans up after itself" — nothing is written in the first place, which is why the check
    /// sits before the write rather than after it.
    /// </summary>
    [Fact]
    public async Task A_refused_request_writes_no_operation_and_holds_nothing()
    {
        await SpendDownToAsync(0m);

        // What the setup left behind: one hold, already settled and given back. Counted rather than assumed
        // absent, so "the refusal added nothing" is what is actually being asserted.
        var before = await ReservationsAsync();
        Assert.All(before, hold => Assert.NotEqual(AiQuotaReservationStatus.Held, hold.Status));

        Assert.False((await RequestConceptsAsync()).Result.Succeeded);

        Assert.Empty(await OperationsAsync());
        Assert.Equal(before.Count, (await ReservationsAsync()).Count);
    }

    /// <summary>
    /// Refusing must not open a period either. An account that has never run anything and is suspended gets no
    /// row at all — a refusal that wrote one would have spent something to say nothing can be spent.
    /// </summary>
    [Fact]
    public async Task Refusing_a_request_opens_no_period()
    {
        await GiveQuotaAsync(allowance: 1000m, suspended: true);

        Assert.False((await RequestConceptsAsync()).Result.Succeeded);
        Assert.Empty(await PeriodsAsync());
    }

    // ---- the reset boundary ----------------------------------------------------------------------------

    /// <summary>
    /// The same request refused before the period ends is admitted after it. Nothing had to be cleaned up in
    /// between, because the refusal wrote nothing.
    /// </summary>
    [Fact]
    public async Task A_request_refused_before_the_reset_is_admitted_after_it()
    {
        await SpendDownToAsync(0m);

        Assert.False((await RequestConceptsAsync()).Result.Succeeded);

        // Into October, which opens a period this account has never had.
        _clock.Advance(TimeSpan.FromDays(20));

        Assert.True((await RequestConceptsAsync()).Result.Succeeded);
        Assert.Single(await OperationsAsync());
    }

    /// <summary>
    /// A key used by a refused request is not spent: the refusal recorded nothing against it, so the same key
    /// creates the operation once the allowance is back. A refusal that had reserved the key would leave a
    /// creator permanently unable to retry the request they made.
    /// </summary>
    [Fact]
    public async Task The_key_of_a_refused_request_still_works_after_the_reset()
    {
        await SpendDownToAsync(0m);

        var refused = await RequestConceptsAsync(key: "retry-1");
        Assert.False(refused.Result.Succeeded);
        Assert.False(refused.Replayed);

        _clock.Advance(TimeSpan.FromDays(20));

        var accepted = await RequestConceptsAsync(key: "retry-1");

        Assert.True(accepted.Result.Succeeded);

        // Created, not replayed: there was no earlier operation under this key to replay.
        Assert.False(accepted.Replayed);
        Assert.Single(await OperationsAsync());
    }

    /// <summary>A refusal is never a replay — nothing was created, so there is nothing to return again.</summary>
    [Fact]
    public async Task A_repeated_refusal_never_reports_itself_as_a_replay()
    {
        await SpendDownToAsync(0m);

        Assert.False((await RequestConceptsAsync(key: "retry-1")).Replayed);
        Assert.False((await RequestConceptsAsync(key: "retry-1")).Replayed);
        Assert.Empty(await OperationsAsync());
    }

    // ---- who the allowance belongs to ------------------------------------------------------------------

    /// <summary>
    /// The allowance follows the person, so spending it in one workspace refuses the next request in the
    /// other. Two memberships, one account, one balance (USAGE-001).
    /// </summary>
    [Fact]
    public async Task An_account_out_of_allowance_is_out_of_it_in_every_workspace()
    {
        await SpendDownToAsync(0m);

        var inA = await RequestConceptsAsync(WorkspaceA, MembershipInA);
        var inB = await RequestConceptsAsync(WorkspaceB, MembershipInB);

        Assert.Equal(AiProposalErrors.QuotaExhausted, inA.Result.Error!.Code);
        Assert.Equal(AiProposalErrors.QuotaExhausted, inB.Result.Error!.Code);
    }

    /// <summary>
    /// A second account in the same workspace is unaffected. The refusal is about who asked, never about where
    /// they asked from — so one creator's spending cannot exhaust their colleague.
    /// </summary>
    [Fact]
    public async Task One_account_running_out_does_not_refuse_another_in_the_same_workspace()
    {
        await SpendDownToAsync(0m);

        Assert.False((await RequestConceptsAsync()).Result.Succeeded);
        Assert.True((await RequestConceptsAsync(accountId: "user-alex")).Result.Succeeded);
    }

    /// <summary>
    /// A workspace role grants no exemption, and structurally cannot: the gate reads an account and never a
    /// role. An Owner is not a platform administrator.
    /// </summary>
    [Fact]
    public async Task A_workspace_owner_gets_no_exemption()
    {
        await SpendDownToAsync(0m);

        foreach (var role in (WorkspaceRole[])[WorkspaceRole.Contributor, WorkspaceRole.Editor, WorkspaceRole.Owner])
        {
            var outcome = await RequestConceptsAsync(role: role);

            Assert.Equal(AiProposalErrors.QuotaExhausted, outcome.Result.Error!.Code);
        }
    }

    // ---- every route -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_route_refuses_the_same_way()
    {
        await SpendDownToAsync(0m);

        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA, MembershipInA, WorkspaceRole.Owner, Account);

        var outcome = await scope.ServiceProvider.GetRequiredService<IAiFirstDraftRequestBusiness>()
            .RequestAsync(
                new RequestRecipeFirstDraftViewModel { Audience = "weeknight cooks" },
                CreatorPantry.Domain.Modules.Measurement.Managers.MeasurementSystem.UsCustomary,
                "key-1",
                TestContext.Current.CancellationToken);

        Assert.Equal(AiProposalErrors.QuotaExhausted, outcome.Result.Error!.Code);
        Assert.Empty(await OperationsAsync());
    }

    /// <summary>
    /// The structural guard the seven-plus call sites need, because the compiler cannot provide one: every
    /// request seam takes the gate. A fourteenth capability that forgets would queue work an account has no
    /// allowance for and only discover it at the worker, as a failed operation, twenty minutes later.
    /// </summary>
    [Fact]
    public void Every_request_seam_is_behind_the_quota_gate()
    {
        var seams = typeof(IAiProposalBusiness).Assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && type.Namespace == "CreatorPantry.Domain.Modules.Ai.Business"
                && type.Name.EndsWith("RequestBusiness", StringComparison.Ordinal))
            .ToList();

        // AiProposalBusiness is the generic route and does not carry the suffix, so it is named outright
        // rather than left to a pattern that would quietly stop covering it.
        seams.Add(typeof(IAiProposalBusiness).Assembly.GetTypes()
            .Single(type => type.Name == "AiProposalBusiness"));

        // Fifteen since the dish-facet reading added its own request seam. It is asked automatically as a
        // creator types a name rather than by a button they pressed, so an unmetered one would spend an
        // allowance faster than any other seam here.
        Assert.Equal(15, seams.Count);

        var missing = seams
            .Where(type => !type.GetConstructors().Single().GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IAiRequestQuotaGate)))
            .Select(type => type.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "a request seam can queue work without checking the account's allowance: "
                + string.Join(", ", missing));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>
    /// Leaves the account with exactly <paramref name="remaining"/> to spend, by admitting and settling real
    /// holds rather than by writing a period's totals directly — so what these tests refuse against is a
    /// balance the production seam actually produced.
    /// </summary>
    private async Task SpendDownToAsync(decimal remaining)
    {
        await GiveQuotaAsync(allowance: 1000m);

        using var scope = _provider.CreateScope();
        var quota = scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>();

        var admission = await quota.AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                Account,
                Guid.NewGuid(),
                Guid.NewGuid(),
                AiTaskType.RecipeConcepts,
                true,
                _clock.UtcNow + TimeSpan.FromMinutes(10)),
            TestContext.Current.CancellationToken);

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, admission.Outcome);

        await quota.StageSettlementAsync(
            admission.ReservationId!.Value,
            [
                new AiUsageAttemptServiceModel
                {
                    AttemptNumber = 1,
                    OccurredAt = _clock.UtcNow,
                    ProviderName = "test-provider",
                    ModelName = "test-model",

                    // Charged at the estimate, since no model rate is configured -- so the arithmetic below is
                    // "the whole allowance less what is meant to be left", with no conversion in the way.
                    Outcome = AiUsageOutcome.Succeeded,
                    IsBillable = true,
                },
            ],
            TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        await quota.PostAsync(admission.ReservationId.Value, TestContext.Current.CancellationToken);

        // The hold charged PerCall; the rest is written onto the period so the balance lands exactly.
        await ConsumeAsync(1000m - remaining - PerCall);
    }

    /// <summary>Adds settled spend to the open period, for setting up a balance a test needs exactly.</summary>
    private async Task ConsumeAsync(decimal amount)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        var period = await db.AccountAiQuotaPeriods.SingleAsync(
            row => row.AccountId == Account, TestContext.Current.CancellationToken);

        period.Consumed += amount;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task GiveQuotaAsync(decimal allowance, bool suspended = false)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        db.AccountAiQuotas.Add(new AccountAiQuota
        {
            Id = Guid.NewGuid(),
            AccountId = Account,
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

    private async Task<Domain.Managers.Idempotency.IdempotentOutcome<AiProposalStatusServiceModel>>
        RequestConceptsAsync(
            Guid? workspaceId = null,
            Guid? membershipId = null,
            string key = "key-1",
            WorkspaceRole role = WorkspaceRole.Contributor,
            string? accountId = null)
    {
        using var scope = _provider.CreateScope();
        Resolve(
            scope,
            workspaceId ?? WorkspaceA,
            membershipId ?? MembershipInA,
            role,
            accountId ?? Account);

        return await scope.ServiceProvider.GetRequiredService<IAiConceptRequestFacade>()
            .RequestAsync(
                new RequestRecipeConceptsViewModel { Audience = "weeknight cooks" },
                key,
                TestContext.Current.CancellationToken);
    }

    private static void Resolve(
        IServiceScope scope, Guid workspaceId, Guid membershipId, WorkspaceRole role, string accountId) =>
        scope.ServiceProvider.GetRequiredService<IWorkspaceContextResolver>().Resolve(
            workspaceId,
            workspaceId == WorkspaceA ? "a" : "b",
            membershipId,
            role,
            accountId);

    private async Task<List<Domain.Modules.Ai.Data.Entities.AiOperation>> OperationsAsync()
    {
        using var scope = _provider.CreateScope();
        Resolve(scope, WorkspaceA, MembershipInA, WorkspaceRole.Owner, Account);

        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        return await db.AiOperations.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The quota tables read with no resolved workspace and no <c>IgnoreQueryFilters</c>: nothing in the
    /// AiUsage module is workspace-owned, so there is no filter to opt out of.
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

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
