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
/// A plan's daily picture cap holds when one account asks from two places at once (AP.4.2): two instances racing
/// for the account's last picture of the day cannot both be admitted.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this holds at all.</strong> The day's count is read after the account's period inside every
/// admission attempt, and the hold's write moves the period's row version, so an admission that commits between
/// another's read and its save makes that save lose and judge again against a count that includes it. Two
/// instances over one SQL Server database are the case that has to hold; SQLite serialises every writer and
/// proves nothing about it, which is why <see cref="DailyImageCapAdmissionTests"/> leaves this here.
/// </para>
/// <para>
/// One instance retries on failure, as every Aspire-hosted context does, and both picture paths are exercised:
/// with the platform's spend ceiling configured, the count is read inside the explicit transaction the ceiling
/// runs in.
/// </para>
/// </remarks>
public sealed class DailyImageCapConcurrencyTests : IAsyncLifetime
{
    private const string Model = "test-image";

    private const decimal Rate = 50m;

    private const int Cap = 3;

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
    /// Two of three used, one asked for on each of two instances at once — exactly one is admitted, every time.
    /// </summary>
    /// <remarks>
    /// Twenty rounds, each a fresh UTC day for a fresh account. A race lost once in twenty is the bug this exists
    /// to catch, and one round could pass by luck of scheduling.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_instances_cannot_both_take_the_last_picture_of_the_day(bool ceiling)
    {
        var first = Instance(retrying: false, ceiling);
        var second = Instance(retrying: true, ceiling);

        for (var round = 0; round < 20; round++)
        {
            var account = $"account-{ceiling}-{round}";
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
            Assert.Single(outcomes, outcome => outcome.Outcome is AiQuotaAdmissionOutcome.DailyImageCap);
            Assert.Equal(Cap, await HeldPicturesAsync(account));

            _clock.Advance(TimeSpan.FromDays(1));
        }
    }

    /// <summary>
    /// A burst from two instances for one account never holds more than the cap. Some may come back busy — three
    /// contended attempts is all admission makes — but none past the line.
    /// </summary>
    [Fact]
    public async Task A_burst_from_two_instances_never_holds_past_the_cap()
    {
        var first = Instance(retrying: false, ceiling: false);
        var second = Instance(retrying: true, ceiling: false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var racing = Enumerable.Range(0, 10)
            .Select(index => AdmitAsync(index % 2 == 0 ? first : second, "account-burst", 1, gate.Task))
            .ToArray();

        gate.SetResult();
        var outcomes = await Task.WhenAll(racing);

        var admitted = outcomes.Count(outcome => outcome.Outcome is AiQuotaAdmissionOutcome.Admitted);

        Assert.InRange(admitted, 1, Cap);
        Assert.All(outcomes, outcome => Assert.Contains(
            outcome.Outcome,
            new[] { AiQuotaAdmissionOutcome.Admitted, AiQuotaAdmissionOutcome.DailyImageCap, AiQuotaAdmissionOutcome.Busy }));
        Assert.Equal(admitted, await HeldPicturesAsync("account-burst"));
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
                account, Guid.NewGuid(), pictures, Model, null, null, _clock.UtcNow + TimeSpan.FromHours(6), Cap),
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

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
