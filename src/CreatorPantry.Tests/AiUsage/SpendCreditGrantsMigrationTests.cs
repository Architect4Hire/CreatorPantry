using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.AiUsage;

/// <summary>
/// The AP.5.2 migration over holds that already exist: every one was paid wholly from the allowance, so it is
/// backfilled as such before the new checks are added, and a hold left mid-flight finishes under the new code.
/// </summary>
/// <remarks>
/// A migration applied to an empty database runs its backfill over nothing, which is all the other SQL Server
/// fixtures prove. This one stops at the migration before, writes the rows a running deployment has, and goes on.
/// </remarks>
public sealed class SpendCreditGrantsMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20261028092026_AddAccountCreditGrants";

    private const string Account = "user-sam";

    private static readonly DateTimeOffset Now = new(2037, 3, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly SqlServerTestDatabase _container = new();

    private ServiceProvider? _provider;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        _provider = new ServiceCollection()
            .AddLogging()
            .AddApplicationTime()
            .AddBillingModule()
            .AddAiUsageModule()
            .AddSingleton<IClock>(new FixedAt(Now))
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()))
            .BuildServiceProvider(validateScopes: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    [Fact]
    public async Task Existing_holds_are_backfilled_as_paid_from_the_allowance_and_finish_under_the_new_code()
    {
        var period = Guid.NewGuid();
        var held = Guid.NewGuid();
        var posted = Guid.NewGuid();
        var unposted = Guid.NewGuid();

        await using (var scope = _provider!.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration, Ct);

            var start = Now.AddDays(-14);
            var end = Now.AddDays(17);
            var localStart = DateOnly.FromDateTime(start.UtcDateTime);
            var task = (int)AiTaskType.RecipeConcepts;

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO AccountAiQuotaPeriods
                    (Id, AccountId, StartsAt, EndsAt, LocalStartDate, Unit, TimeZoneId, Allowance, CarriedOver,
                     UpgradeGranted, Consumed, Reserved)
                VALUES ({period}, {Account}, {start}, {end}, {localStart}, 1, 'Etc/UTC', 1000, 0, 0, 25, 60)
                """, Ct);

            // Held, untouched: 40 out. Posted: 30 held, 25 charged and landed. Settled and not yet posted: 20
            // held, 15 decided — the worker died before applying it, and the sweep has it next.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO AccountAiQuotaReservations
                    (Id, AccountId, PeriodId, AiOperationId, LeaseToken, TaskType, Unit, ReservedAmount,
                     PerAttemptEstimate, SettledAmount, UsageReported, Status, HeldAt, ExpiresAt, SettledAt,
                     ReleasedAt, PostedAt)
                VALUES
                    ({held}, {Account}, {period}, NEWID(), NEWID(), {task}, 1, 40, 20, NULL, 0, 1,
                     {Now.AddMinutes(-5)}, {Now.AddMinutes(5)}, NULL, NULL, NULL),
                    ({posted}, {Account}, {period}, NEWID(), NEWID(), {task}, 1, 30, 15, 25, 1, 2,
                     {Now.AddDays(-2)}, {Now.AddDays(-2).AddMinutes(10)}, {Now.AddDays(-2)}, {Now.AddDays(-2)}, {Now.AddDays(-2)}),
                    ({unposted}, {Account}, {period}, NEWID(), NEWID(), {task}, 1, 20, 10, 15, 1, 2,
                     {Now.AddHours(-1)}, {Now.AddHours(1)}, {Now.AddHours(-1)}, NULL, NULL)
                """, Ct);

            await db.Database.MigrateAsync(Ct);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var holds = await db.AccountAiQuotaReservations.AsNoTracking().ToDictionaryAsync(hold => hold.Id, Ct);

            Assert.Equal(40m, holds[held].ReservedFromAllowance);
            Assert.Null(holds[held].ChargedToAllowance);
            Assert.Equal(30m, holds[posted].ReservedFromAllowance);
            Assert.Equal(25m, holds[posted].ChargedToAllowance);
            Assert.Equal(20m, holds[unposted].ReservedFromAllowance);
            Assert.Null(holds[unposted].ChargedToAllowance);

            Assert.Equal(0m, (await db.AccountAiQuotaPeriods.AsNoTracking().SingleAsync(row => row.Id == period, Ct)).GrantReserved);
        }

        // The hold the dead worker left is posted by the sweep, under the new code, wholly to the allowance.
        await using (var scope = _provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAiQuotaMaintenanceFacade>().SweepAsync(Ct);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var finished = await db.AccountAiQuotaReservations.AsNoTracking().SingleAsync(hold => hold.Id == unposted, Ct);
            var stored = await db.AccountAiQuotaPeriods.AsNoTracking().SingleAsync(row => row.Id == period, Ct);

            Assert.Equal(15m, finished.ChargedToAllowance);
            Assert.Equal(40m, stored.Reserved);
            Assert.Equal(40m, stored.Consumed);
            Assert.Empty(await db.AccountCreditGrantDraws.AsNoTracking().ToListAsync(Ct));
        }
    }

    private sealed class FixedAt(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
