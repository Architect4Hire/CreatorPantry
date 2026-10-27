using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Business;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// An upgrade's difference is granted once however many admissions reach the period together (AP.4.2): the
/// top-up is a write to the period under its row version, and every racer re-judges it against a fresh read.
/// </summary>
/// <remarks>
/// SQL Server, two instances over one database, one retrying on failure as Aspire-hosted contexts do — SQLite never
/// regenerates a row version, so a race there would pass whatever the code did. <see cref="PlanAllowanceTests"/>
/// has the single-writer rules.
/// </remarks>
public sealed class PlanAllowanceConcurrencyTests : IAsyncLifetime
{
    private const decimal Hold = 10m;

    private static readonly DateTimeOffset BeforeMarch = new(2037, 2, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset MarchStart = new(2037, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SqlServerTestDatabase _container = new();

    private readonly MovableClock _clock = new(BeforeMarch);

    private readonly List<ServiceProvider> _instances = [];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        await using var scope = Instance(retrying: false).CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var instance in _instances)
        {
            await instance.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Creator's period, then Pro, then eight admissions from two instances at once: the period gains Pro's
    /// difference exactly once, and every admission that was let through is held against it.
    /// </summary>
    [Fact]
    public async Task Admissions_racing_after_an_upgrade_grant_the_difference_once()
    {
        var first = Instance(retrying: false);
        var second = Instance(retrying: true);

        for (var round = 0; round < 10; round++)
        {
            var account = $"account-{round}";
            _clock.Set(BeforeMarch);
            await PutOnAsync(first, account, "creator");

            _clock.Set(MarchStart.AddDays(2));
            Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(first, account)).Outcome);
            await PutOnAsync(first, account, "pro");

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racing = Enumerable.Range(0, 8)
                .Select(index => AdmitAsync(index % 2 == 0 ? first : second, account, gate.Task))
                .ToArray();

            gate.SetResult();
            var outcomes = await Task.WhenAll(racing);

            await using var scope = first.CreateAsyncScope();
            var period = await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
                .AccountAiQuotaPeriods.AsNoTracking()
                .SingleAsync(row => row.AccountId == account, Ct);

            Assert.Equal(6000m, period.Allowance);
            Assert.Equal(12000m, period.UpgradeGranted);

            // Contention may send some back busy; none is refused for the balance, and every hold landed.
            Assert.DoesNotContain(outcomes, outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Exhausted);
            var admitted = outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);
            Assert.Equal((admitted + 1) * Hold, period.Reserved);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private ServiceProvider Instance(bool retrying)
    {
        var values = new Dictionary<string, string?>
        {
            ["Billing:DefaultPlan"] = "creator",
        };

        foreach (var (index, key, credits) in new[] { (0, "creator", 6000), (1, "pro", 18000) })
        {
            var prefix = $"Billing:Plans:{index}:";
            values[prefix + "Key"] = key;
            values[prefix + "Name"] = key;
            values[prefix + "Credits"] = credits.ToString();
            values[prefix + "DailyImageCap"] = "5";
            values[prefix + "RunningImages"] = "1";
            values[prefix + "Period"] = "Monthly";
            values[prefix + "QualityClasses:0"] = "standard";
        }

        var instance = new ServiceCollection()
            .AddLogging()
            .AddApplicationTime()
            .AddBillingModule(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultTaskEstimate = Hold;
                options.EstimatedCallsPerRun = 1;
            })
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(
                _container.GetConnectionString(),
                sql =>
                {
                    if (retrying)
                    {
                        sql.EnableRetryOnFailure();
                    }
                }))
            .BuildServiceProvider(validateScopes: true);

        _instances.Add(instance);

        return instance;
    }

    private async Task PutOnAsync(ServiceProvider instance, string account, string planKey)
    {
        await using var scope = instance.CreateAsyncScope();

        Assert.Equal(
            AccountPlanChangeOutcome.Changed,
            await scope.ServiceProvider.GetRequiredService<IAccountPlanBusiness>()
                .ChangeAsync(account, planKey, AccountPlanSource.Ops, null, Ct));
    }

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(ServiceProvider instance, string account, Task? gate = null)
    {
        if (gate is not null)
        {
            await gate;
        }

        await using var scope = instance.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitAsync(
            new AiQuotaAdmissionRequestServiceModel(
                account, Guid.NewGuid(), Guid.NewGuid(), AiTaskType.RecipeConcepts, true,
                _clock.UtcNow + AiPolicy.LeaseDuration),
            Ct);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;

        public void Set(DateTimeOffset instant) => UtcNow = instant;
    }
}
