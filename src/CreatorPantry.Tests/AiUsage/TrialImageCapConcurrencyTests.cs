using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// A trial's lifetime picture cap holds when one account asks from several places at once (AP.7.1, B-52): requests
/// racing for a trial's last picture cannot both be admitted.
/// </summary>
/// <remarks>
/// It holds for the reason <see cref="DailyImageCapConcurrencyTests"/> gives for the day: the count is read after the
/// account's period inside every admission attempt, and the hold's write moves the period's row version. SQL Server,
/// two instances, one of them retrying, and both picture paths — with the platform's spend ceiling, the count is read
/// inside the explicit transaction the ceiling runs in.
/// </remarks>
public sealed class TrialImageCapConcurrencyTests : IAsyncLifetime
{
    private const string Model = "test-image";

    private const decimal Rate = 50m;

    private const int Cap = 10;

    private readonly SqlServerTestDatabase _container = new();

    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    private readonly List<ServiceProvider> _instances = [];

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var setup = Instance(retrying: false, ceiling: false);
        await using var scope = setup.CreateAsyncScope();
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
    /// Nine of ten made, one asked for on each of two instances at once — exactly one is admitted, every time.
    /// </summary>
    /// <remarks>Twenty rounds, each a fresh account: a race lost once in twenty is the bug this exists to catch.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_instances_cannot_both_take_a_trials_last_picture(bool ceiling)
    {
        var first = Instance(retrying: false, ceiling);
        var second = Instance(retrying: true, ceiling);

        for (var round = 0; round < 20; round++)
        {
            var account = $"trial-{ceiling}-{round}";
            Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(first, account, Cap - 1)).Outcome);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racing = new[]
            {
                AdmitAsync(first, account, 1, gate.Task),
                AdmitAsync(second, account, 1, gate.Task),
            };

            gate.SetResult();
            var outcomes = await Task.WhenAll(racing);

            Assert.Single(outcomes, outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);
            Assert.Single(outcomes, outcome => outcome.Outcome is AiQuotaAdmissionOutcome.TrialImageCap);
            Assert.Equal(Cap, await HeldPicturesAsync(account));
        }
    }

    /// <summary>
    /// Five requests for the last picture at once, the scenario AP.7.1's rule names, never hold past the cap. Some may
    /// come back busy — three contended attempts is all admission makes — but none past the line.
    /// </summary>
    [Fact]
    public async Task A_burst_for_a_trials_last_picture_admits_at_most_one()
    {
        var first = Instance(retrying: false, ceiling: false);
        var second = Instance(retrying: true, ceiling: false);
        const string account = "trial-burst";

        Assert.Equal(AiQuotaAdmissionOutcome.Admitted, (await AdmitAsync(first, account, Cap - 1)).Outcome);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racing = Enumerable.Range(0, 5)
            .Select(index => AdmitAsync(index % 2 == 0 ? first : second, account, 1, gate.Task))
            .ToArray();

        gate.SetResult();
        var outcomes = await Task.WhenAll(racing);

        Assert.InRange(outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted), 0, 1);
        Assert.All(outcomes, outcome => Assert.Contains(
            outcome.Outcome,
            new[] { AiQuotaAdmissionOutcome.Admitted, AiQuotaAdmissionOutcome.TrialImageCap, AiQuotaAdmissionOutcome.Busy }));
        Assert.InRange(await HeldPicturesAsync(account), Cap - 1, Cap);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private ServiceProvider Instance(bool retrying, bool ceiling)
    {
        var instance = new ServiceCollection()
            .AddLogging()
            .AddApplicationTime()
            .AddBillingModule()
            .AddAiUsageModule()
            .AddSingleton<IClock>(_clock)
            .Configure<AiQuotaOptions>(options =>
            {
                options.DefaultAllowance = 1_000_000m;
                options.ImageRates[Model] = Rate;
                options.PlatformImageSpendPerDay = ceiling ? 1_000m : null;
            })
            .Configure<AiImageListPriceOptions>(options => options.Images[Model] = 0.07m)
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

    private async Task<AiQuotaAdmissionServiceModel> AdmitAsync(
        ServiceProvider instance, string account, int pictures, Task? gate = null)
    {
        if (gate is not null)
        {
            await gate;
        }

        await using var scope = instance.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IAiQuotaAdmissionFacade>().AdmitImageAsync(
            new AiImageQuotaAdmissionRequestServiceModel(
                account,
                Guid.NewGuid(),
                pictures,
                Model,
                null,
                null,
                _clock.UtcNow + TimeSpan.FromHours(6),
                DailyImageCap: null,
                TrialImageCap: Cap),
            Ct);
    }

    private async Task<int> HeldPicturesAsync(string account)
    {
        await using var scope = _instances[0].CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .AccountAiQuotaReservations.AsNoTracking()
            .Where(hold => hold.AccountId == account && hold.Status == AiQuotaReservationStatus.Held)
            .SumAsync(hold => hold.ImagesRequested ?? 0, Ct);
    }

    private sealed class MovableClock(DateTimeOffset start) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = start;
    }
}
