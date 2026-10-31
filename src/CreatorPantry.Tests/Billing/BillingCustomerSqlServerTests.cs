using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Data;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The account ↔ Stripe customer record against the schema a deployment applies (AP.6.1): an account is made a
/// customer once and read from the record after, two requests racing to make it one get one row, accounts never
/// share a customer or see each other's, and test and live mode are separate customers.
/// </summary>
/// <remarks>
/// SQL Server rather than SQLite, because the race is decided by the unique indexes as SQL Server enforces them,
/// with two real connections. The provider is a fake: what it answers is the gateway's contract, which
/// <see cref="StripeBillingGatewayTests"/> holds Stripe to.
/// </remarks>
public sealed class BillingCustomerSqlServerTests : IAsyncLifetime
{
    private const string Sam = "user-sam";

    private const string Alex = "user-alex";

    private static readonly DateTimeOffset Now = new(2037, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerTestDatabase _container = new();

    private readonly FakeProvider _provider = new();

    private ServiceProvider? _services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IClock>(new FixedClock())
            .AddBillingModule(new ConfigurationBuilder().Build())
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlServer(_container.GetConnectionString()));

        // One provider for every scope, so two scopes racing meet at it.
        services.RemoveAll<IBillingPaymentGateway>();
        services.AddSingleton<IBillingPaymentGateway>(_provider);

        _services = services.BuildServiceProvider(validateScopes: true);

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.MigrateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    // ---- ensuring a customer -----------------------------------------------------------------------------

    [Fact]
    public async Task An_account_becomes_a_customer_once_and_is_read_from_the_record_after()
    {
        _provider.Answer = _ => Made("cus_SamTest1");

        var first = await EnsureAsync(Sam);
        var second = await EnsureAsync(Sam);

        Assert.Equal(BillingPaymentOutcome.Succeeded, first.Outcome);
        Assert.Equal("cus_SamTest1", first.Customer!.ExternalCustomerId);
        Assert.False(first.Customer.IsLiveMode);
        Assert.Equal(Now, first.Customer.CreatedAt);

        Assert.Equal(first.Customer.Id, second.Customer!.Id);
        Assert.Equal(1, _provider.Calls);
        Assert.Equal(1, await CountAsync());
    }

    /// <summary>
    /// Both requests read nothing and both ask the provider — with the same derived key, so Stripe answers both with
    /// one customer — and the index lets one row in. The loser returns the winner's row rather than a refusal.
    /// </summary>
    [Fact]
    public async Task Two_requests_racing_to_make_an_account_a_customer_store_one_row_and_both_get_it()
    {
        _provider.Answer = _ => Made("cus_SamTest1");
        _provider.HoldUntil(callers: 2);

        var results = await Task.WhenAll(EnsureAsync(Sam), EnsureAsync(Sam));

        Assert.All(results, result => Assert.Equal(BillingPaymentOutcome.Succeeded, result.Outcome));
        Assert.Equal(results[0].Customer!.Id, results[1].Customer!.Id);
        Assert.Equal(2, _provider.Calls);
        Assert.Equal(1, await CountAsync());
    }

    /// <summary>
    /// Only possible once Stripe has pruned the key, a day on: two customers at Stripe. The first recorded is the
    /// account's, both callers are given it, and the other customer is referred to by nothing.
    /// </summary>
    [Fact]
    public async Task Two_customers_for_one_account_resolve_to_the_first_recorded()
    {
        var next = 0;
        _provider.Answer = _ => Made(Interlocked.Increment(ref next) == 1 ? "cus_SamFirst" : "cus_SamSecond");
        _provider.HoldUntil(callers: 2);

        var results = await Task.WhenAll(EnsureAsync(Sam), EnsureAsync(Sam));

        var stored = Assert.Single(await AllAsync());
        Assert.All(results, result => Assert.Equal(stored.ExternalCustomerId, result.Customer!.ExternalCustomerId));
    }

    [Fact]
    public async Task Two_accounts_are_two_customers_and_neither_finds_the_others()
    {
        _provider.Answer = account => Made(account == Sam ? "cus_SamTest1" : "cus_AlexTest1");

        await EnsureAsync(Sam);

        Assert.Null(await FindAsync(Alex));

        await EnsureAsync(Alex);

        Assert.Equal("cus_SamTest1", (await FindAsync(Sam))!.ExternalCustomerId);
        Assert.Equal("cus_AlexTest1", (await FindAsync(Alex))!.ExternalCustomerId);

        // The reverse lookup a webhook makes names the one account.
        using var scope = _services!.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IBillingCustomerRepository>();
        Assert.Equal(Alex, (await repository.FindByExternalCustomerAsync("cus_AlexTest1", Ct))!.AccountId);
        Assert.Null(await repository.FindByExternalCustomerAsync("cus_Nobody", Ct));
    }

    /// <summary>A customer id Stripe gave another account cannot become this one's: one payer is never two accounts.</summary>
    [Fact]
    public async Task A_customer_already_mapped_to_another_account_is_not_recorded_for_this_one()
    {
        _provider.Answer = _ => Made("cus_SamTest1");
        await EnsureAsync(Sam);

        var alex = await EnsureAsync(Alex);

        Assert.Equal(BillingPaymentOutcome.Unreadable, alex.Outcome);
        Assert.Null(alex.Customer);
        Assert.Null(await FindAsync(Alex));
        Assert.Equal(Sam, Assert.Single(await AllAsync()).AccountId);
    }

    /// <summary>Stripe's test and live modes are separate worlds; a test customer is never handed to the live API.</summary>
    [Fact]
    public async Task Test_and_live_mode_are_separate_customers_for_one_account()
    {
        _provider.Answer = _ => Made("cus_SamTest1");
        var test = await EnsureAsync(Sam);

        _provider.Mode = BillingPaymentMode.Live;
        Assert.Null(await FindAsync(Sam));

        _provider.Answer = _ => Made("cus_SamLive1", live: true);
        var live = await EnsureAsync(Sam);

        Assert.NotEqual(test.Customer!.Id, live.Customer!.Id);
        Assert.True(live.Customer.IsLiveMode);
        Assert.Equal(2, await CountAsync());
    }

    [Fact]
    public async Task With_no_provider_nothing_is_asked_or_stored()
    {
        _provider.Mode = BillingPaymentMode.NotConfigured;

        var result = await EnsureAsync(Sam);

        Assert.Equal(BillingPaymentOutcome.NotConfigured, result.Outcome);
        Assert.Null(await FindAsync(Sam));
        Assert.Equal(0, _provider.Calls);
        Assert.Equal(0, await CountAsync());
    }

    [Theory]
    [InlineData(BillingPaymentOutcome.Unavailable)]
    [InlineData(BillingPaymentOutcome.CredentialRejected)]
    [InlineData(BillingPaymentOutcome.Throttled)]
    public async Task A_provider_that_did_not_make_a_customer_leaves_nothing_stored(BillingPaymentOutcome outcome)
    {
        _provider.Answer = _ => BillingCustomerCreation.Of(outcome);

        var result = await EnsureAsync(Sam);

        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Customer);
        Assert.Equal(0, await CountAsync());
    }

    // ---- the schema --------------------------------------------------------------------------------------

    public static TheoryData<string, BillingCustomer, BillingCustomer?> Refused() => new()
    {
        { "a second customer for one account in one mode", Row(Sam, "cus_Two"), Row(Sam, "cus_One") },
        { "one customer for two accounts", Row(Alex, "cus_One"), Row(Sam, "cus_One") },
        { "one customer in both modes", Row(Sam, "cus_One", live: true), Row(Sam, "cus_One") },
        { "a blank account", Row("  ", "cus_One"), null },
        { "a blank customer", Row(Sam, " "), null },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task The_schema_refuses_a_row_that_would_make_a_payer_ambiguous(string reason, BillingCustomer row, BillingCustomer? already)
    {
        Assert.NotEmpty(reason);
        if (already is not null)
        {
            await InsertAsync(already);
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(row));
    }

    [Fact]
    public async Task A_stored_row_is_never_edited()
    {
        await InsertAsync(Row(Sam, "cus_One"));

        using var scope = _services!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var stored = await db.BillingCustomers.SingleAsync(Ct);
        stored.ExternalCustomerId = "cus_Other";

        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static BillingCustomerCreation Made(string customer, bool live = false) =>
        new(BillingPaymentOutcome.Succeeded, customer, live);

    private static BillingCustomer Row(string account, string customer, bool live = false) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = account,
        ExternalCustomerId = customer,
        IsLiveMode = live,
        CreatedAt = Now,
    };

    private async Task<BillingCustomerEnsured> EnsureAsync(string account)
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBillingCustomerDataLayer>().EnsureAsync(account, Ct);
    }

    private async Task<BillingCustomer?> FindAsync(string account)
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBillingCustomerDataLayer>().FindAsync(account, Ct);
    }

    private async Task InsertAsync(BillingCustomer row)
    {
        using var scope = _services!.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        db.BillingCustomers.Add(row);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<BillingCustomer>> AllAsync()
    {
        using var scope = _services!.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BillingCustomers.AsNoTracking().ToListAsync(Ct);
    }

    private async Task<int> CountAsync() => (await AllAsync()).Count;

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    /// <summary>
    /// The provider as the gateway contract describes it. <see cref="HoldUntil"/> keeps every caller waiting at the
    /// provider until that many have arrived — after each has read the record and found nothing — which is the race.
    /// </summary>
    private sealed class FakeProvider : IBillingPaymentGateway
    {
        private readonly object _gate = new();

        private TaskCompletionSource? _release;

        private int _waiting;

        private int _hold;

        private int _calls;

        public BillingPaymentMode Mode { get; set; } = BillingPaymentMode.Test;

        public Func<string, BillingCustomerCreation> Answer { get; set; } = _ => BillingCustomerCreation.Of(BillingPaymentOutcome.Unavailable);

        public int Calls => Volatile.Read(ref _calls);

        public void HoldUntil(int callers)
        {
            _hold = callers;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task<BillingCustomerCreation> CreateCustomerAsync(string accountId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            if (_release is { } release)
            {
                lock (_gate)
                {
                    if (++_waiting >= _hold)
                    {
                        release.TrySetResult();
                    }
                }

                await release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return Answer(accountId);
        }

        public Task<BillingRedirect> StartSubscriptionCheckoutAsync(SubscriptionCheckoutRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingPackCheckoutRead> GetPackCheckoutAsync(string externalSessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingPaymentReversalRead> GetPaymentReversalAsync(
            string externalReference, BillingPackReversal kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingSubscriptionRead> GetSubscriptionAsync(string externalSubscriptionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingPaymentOutcome> SchedulePlanChangeAsync(ScheduledPlanChangeRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BillingPaymentOutcome> ReleaseScheduleAsync(
            string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
