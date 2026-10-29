using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Tests.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// <c>/api/v1/me/billing</c> through the real gateway and API, signed in as two different accounts (AP.6.5): the
/// billing page's read, and the Checkout, portal and plan-change commands behind its buttons, with the payment
/// provider replaced by a recording fake.
/// </summary>
/// <remarks>
/// What the provider does with a Checkout — and the webhook that then moves a plan or grants a pack — is AP.6.1–6.3's
/// and their tests'. Here the question is what this host asks of the provider, for whom, and what it tells the page.
/// </remarks>
public sealed class MeBillingRouteTests : IAsyncLifetime
{
    private const string Route = "/api/v1/me/billing";

    private const string Origin = "https://app.example.test";

    private readonly RecordingGateway _gateway = new();

    private TwoWorkspaceGatewayFixture _fixture = null!;

    public async ValueTask InitializeAsync() =>
        _fixture = await TwoWorkspaceGatewayFixture.CreateAsync(services =>
        {
            services.RemoveAll<IBillingPaymentGateway>();
            services.AddSingleton<IBillingPaymentGateway>(_gateway);
            services.PostConfigure<BillingOptions>(billing =>
            {
                billing.Currency = "USD";
                billing.DefaultPlan = "trial";
                billing.Plans =
                [
                    Plan("trial", 0, 1000, trial: true),
                    Plan("creator", 12, 4500),
                    Plan("pro", 49, 18000),
                ];
                billing.Packs = [new CreditPackOption { Key = "small", Credits = 2500, Price = 10 }];
                billing.Rates = new CreditRatesOption { TextDraft = 5, LongFormSection = 10 };
                billing.ReturnOrigins = [Origin, "https://dev.example.test:4200"];
            });
        });

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlanOption Plan(string key, decimal monthly, int credits, bool trial = false) => new()
    {
        Key = key,
        Name = char.ToUpperInvariant(key[0]) + key[1..],
        MonthlyPrice = monthly,
        AnnualMonthlyPrice = monthly,
        Credits = credits,
        ExampleCounts = new PlanExampleOption { RecipeDrafts = 1, SocialPosts = 1, Pictures = 1 },
        DailyImageCap = 5,
        Period = trial ? PlanAllowancePeriod.Once : PlanAllowancePeriod.Monthly,
        RunningImages = 1,
        QualityClasses = ["draft", "standard"],
        TrialDays = trial ? 14 : null,
        TrialImageCap = trial ? 10 : null,
    };

    // ---- the read ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_session_there_is_no_billing_to_read()
    {
        using var anonymous = _fixture.Api.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_new_account_reads_its_trial_and_may_subscribe_and_buy_packs()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var billing = await BodyAsync(await owner.GetAsync(Route, Ct));

        Assert.True(billing.GetProperty("paymentsAvailable").GetBoolean());
        Assert.True(billing.GetProperty("isTestMode").GetBoolean());
        Assert.Equal("trial", billing.GetProperty("plan").GetProperty("key").GetString());
        Assert.True(billing.GetProperty("plan").GetProperty("isTrial").GetBoolean());
        Assert.False(billing.GetProperty("plan").GetProperty("trialEnded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, billing.GetProperty("subscription").ValueKind);
        Assert.True(billing.GetProperty("canSubscribe").GetBoolean());
        Assert.True(billing.GetProperty("canBuyPacks").GetBoolean());
    }

    [Fact]
    public async Task A_host_that_takes_no_payments_reads_as_such_and_starts_nothing()
    {
        _gateway.Mode = BillingPaymentMode.NotConfigured;
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var billing = await BodyAsync(await owner.GetAsync(Route, Ct));
        Assert.False(billing.GetProperty("paymentsAvailable").GetBoolean());
        Assert.False(billing.GetProperty("canSubscribe").GetBoolean());

        var refused = await owner.PostAsJsonAsync(Route + "/checkout/pack", new { packKey = "small" }, "key-1", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(BillingAccountErrors.PaymentsUnavailable, await CodeAsync(refused));
        Assert.Empty(_gateway.PackCheckouts);
    }

    // ---- Checkout ----------------------------------------------------------------------------------------

    /// <summary>
    /// The provider is asked for this account, on this account's customer, with return URLs on the page's own listed
    /// origin, and a provider key bound to the account rather than the client's raw key.
    /// </summary>
    [Fact]
    public async Task A_first_subscription_goes_to_checkout_for_the_signed_in_account()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsJsonAsync(
            Route + "/checkout/subscription",
            new { planKey = "creator", cycle = "Annual", returnOrigin = "https://dev.example.test:4200" },
            "key-1",
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://checkout.example/session-1", (await BodyAsync(response)).GetProperty("url").GetString());

        var asked = Assert.Single(_gateway.SubscriptionCheckouts);
        Assert.Equal(await AccountIdAsync(_fixture.WorkspaceA.OwnerEmail), asked.AccountId);
        Assert.Equal("creator", asked.PlanKey);
        Assert.Equal(BillingCycle.Annual, asked.Cycle);
        Assert.Equal("https://dev.example.test:4200/app/billing?checkout=plan", asked.SuccessUrl.ToString());
        Assert.Equal("https://dev.example.test:4200/app/billing?checkout=cancelled", asked.CancelUrl.ToString());
        Assert.NotEqual("key-1", asked.IdempotencyKey);
    }

    /// <summary>A retried click answers what the first did, and the provider is asked once.</summary>
    [Fact]
    public async Task A_retried_checkout_with_the_same_key_is_answered_once()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        var body = new { planKey = "creator", cycle = "Monthly" };

        var first = await owner.PostAsJsonAsync(Route + "/checkout/subscription", body, "key-1", Ct);
        var again = await owner.PostAsJsonAsync(Route + "/checkout/subscription", body, "key-1", Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await BodyAsync(first)).GetProperty("url").GetString(), (await BodyAsync(again)).GetProperty("url").GetString());
        Assert.Single(_gateway.SubscriptionCheckouts);
    }

    [Fact]
    public async Task A_return_origin_this_host_does_not_list_is_refused_and_nothing_is_started()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var refused = await owner.PostAsJsonAsync(
            Route + "/checkout/pack", new { packKey = "small", returnOrigin = "https://evil.example" }, "key-1", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(BillingAccountErrors.Invalid, await CodeAsync(refused));
        Assert.Empty(_gateway.PackCheckouts);
    }

    [Theory]
    [InlineData("trial")]
    [InlineData("platinum")]
    public async Task Only_a_paid_plan_the_catalogue_offers_can_be_bought(string planKey)
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var refused = await owner.PostAsJsonAsync(
            Route + "/checkout/subscription", new { planKey, cycle = "Monthly" }, "key-1", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(BillingAccountErrors.UnknownPlan, await CodeAsync(refused));
    }

    /// <summary>A second Checkout would make a second subscription (B-57): an account already paying changes plan.</summary>
    [Fact]
    public async Task An_account_already_subscribed_is_sent_to_change_plan_instead()
    {
        await SubscribeAsync(_fixture.WorkspaceA.OwnerEmail, "creator");
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var refused = await owner.PostAsJsonAsync(
            Route + "/checkout/subscription", new { planKey = "pro", cycle = "Monthly" }, "key-1", Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await BodyAsync(refused);
        Assert.Equal(BillingAccountErrors.AlreadySubscribed, problem.GetProperty("code").GetString());
        Assert.Equal("reread", problem.GetProperty("retry").GetString());
        Assert.Empty(_gateway.SubscriptionCheckouts);
    }

    /// <summary>
    /// An ended trial cannot buy credits it could not spend (B-61) — and that is the account's own fact: the other
    /// account, still in its trial, buys the same pack.
    /// </summary>
    [Fact]
    public async Task An_ended_trial_cannot_buy_a_pack_and_another_accounts_trial_can()
    {
        await AgeAsync(_fixture.WorkspaceA.OwnerEmail, TimeSpan.FromDays(20));
        using var ended = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        using var fresh = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);

        var billing = await BodyAsync(await ended.GetAsync(Route, Ct));
        Assert.True(billing.GetProperty("plan").GetProperty("trialEnded").GetBoolean());
        Assert.False(billing.GetProperty("canBuyPacks").GetBoolean());

        var refused = await ended.PostAsJsonAsync(Route + "/checkout/pack", new { packKey = "small" }, "key-1", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(BillingAccountErrors.PlanRequired, await CodeAsync(refused));

        var bought = await fresh.PostAsJsonAsync(Route + "/checkout/pack", new { packKey = "small" }, "key-1", Ct);
        Assert.Equal(HttpStatusCode.OK, bought.StatusCode);

        var asked = Assert.Single(_gateway.PackCheckouts);
        Assert.Equal(await AccountIdAsync(_fixture.WorkspaceB.OwnerEmail), asked.AccountId);
        Assert.Equal($"{Origin}/app/billing?checkout=pack", asked.SuccessUrl.ToString());
    }

    /// <summary>Two accounts using the same client key are two commands: a key never crosses accounts.</summary>
    [Fact]
    public async Task The_same_key_from_two_accounts_starts_two_checkouts()
    {
        using var a = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        using var b = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);

        await a.PostAsJsonAsync(Route + "/checkout/pack", new { packKey = "small" }, "shared-key", Ct);
        await b.PostAsJsonAsync(Route + "/checkout/pack", new { packKey = "small" }, "shared-key", Ct);

        Assert.Equal(2, _gateway.PackCheckouts.Count);
        Assert.NotEqual(_gateway.PackCheckouts[0].IdempotencyKey, _gateway.PackCheckouts[1].IdempotencyKey);
        Assert.NotEqual(_gateway.PackCheckouts[0].AccountId, _gateway.PackCheckouts[1].AccountId);
    }

    // ---- plan changes ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_plan_change_needs_a_subscription_to_change()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var refused = await owner.PostAsJsonAsync(Route + "/plan-change", new { planKey = "pro", cycle = "Monthly" }, "key-1", Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BillingAccountErrors.SubscriptionRequired, await CodeAsync(refused));
    }

    /// <summary>Up lands at once on the provider's page to pay the difference (B-56); a waiting schedule is let go first.</summary>
    [Fact]
    public async Task An_upgrade_goes_to_the_providers_page_for_the_accounts_own_subscription()
    {
        await SubscribeAsync(_fixture.WorkspaceA.OwnerEmail, "creator", scheduleId: "sub_sched_1");
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        // The read sends the rule the command follows, so the page names the button by what it will do.
        var read = await BodyAsync(await owner.GetAsync(Route, Ct));
        Assert.Equal(["pro"], read.GetProperty("upgradePlanKeys").EnumerateArray().Select(key => key.GetString()));

        var response = await owner.PostAsJsonAsync(Route + "/plan-change", new { planKey = "pro", cycle = "Monthly" }, "key-1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("https://portal.example/upgrade", body.GetProperty("url").GetString());
        Assert.False(body.GetProperty("scheduled").GetBoolean());

        Assert.Equal(["sub_sched_1"], _gateway.Released);
        var upgrade = Assert.Single(_gateway.Upgrades);
        Assert.Equal(SubscriptionIdOf(_fixture.WorkspaceA.OwnerEmail), upgrade.ExternalSubscriptionId);
        Assert.Equal("pro", upgrade.PlanKey);
        Assert.Equal($"{Origin}/app/billing?checkout=upgrade", upgrade.CompletedUrl.ToString());
    }

    /// <summary>Down — or the other cycle — waits for the end of the paid period (B-54): scheduled, nothing to pay.</summary>
    [Theory]
    [InlineData("pro", "creator", "Monthly")]
    [InlineData("creator", "creator", "Annual")]
    public async Task Anything_but_an_upgrade_is_scheduled_for_period_end(string from, string to, string cycle)
    {
        await SubscribeAsync(_fixture.WorkspaceA.OwnerEmail, from);
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.PostAsJsonAsync(Route + "/plan-change", new { planKey = to, cycle }, "key-1", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.True(body.GetProperty("scheduled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("url").ValueKind);

        var scheduled = Assert.Single(_gateway.Scheduled);
        Assert.Equal(to, scheduled.PlanKey);
        Assert.Empty(_gateway.Upgrades);
    }

    [Fact]
    public async Task The_plan_already_in_force_is_not_a_change()
    {
        await SubscribeAsync(_fixture.WorkspaceA.OwnerEmail, "creator");
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var refused = await owner.PostAsJsonAsync(Route + "/plan-change", new { planKey = "creator", cycle = "Monthly" }, "key-1", Ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(BillingAccountErrors.Unchanged, await CodeAsync(refused));
    }

    [Fact]
    public async Task A_scheduled_change_can_be_withdrawn_and_nothing_scheduled_is_a_404()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var nothing = await owner.SendAsync(HttpMethod.Delete, Route + "/plan-change", null, Key("key-1"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);
        Assert.Equal(BillingAccountErrors.NothingScheduled, await CodeAsync(nothing));

        await SubscribeAsync(_fixture.WorkspaceA.OwnerEmail, "pro", scheduleId: "sub_sched_2");

        var withdrawn = await owner.SendAsync(HttpMethod.Delete, Route + "/plan-change", null, Key("key-2"), Ct);
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        Assert.Equal(["sub_sched_2"], _gateway.Released);
    }

    // ---- the portal --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_portal_opens_only_for_an_account_with_payments_to_manage()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var none = await owner.PostAsJsonAsync(Route + "/portal", new { }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal(BillingAccountErrors.NoBillingAccount, await CodeAsync(none));

        // A Checkout makes the account's customer at the provider; then there is something to manage.
        await owner.PostAsJsonAsync(Route + "/checkout/pack", new { packKey = "small" }, "key-1", Ct);

        var opened = await owner.PostAsJsonAsync(Route + "/portal", new { }, Ct);
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal("https://portal.example/session", (await BodyAsync(opened)).GetProperty("url").GetString());
        Assert.Equal($"{Origin}/app/billing", Assert.Single(_gateway.Portals).ReturnUrl.ToString());
    }

    // ---- coming back from Checkout ------------------------------------------------------------------------

    /// <summary>
    /// Pending until the webhook has recorded the purchase, then complete with its credits — and another account
    /// naming the same session reads pending, so nothing is said about someone else's purchase.
    /// </summary>
    [Fact]
    public async Task A_pack_reads_pending_until_its_purchase_is_recorded_and_only_for_its_own_account()
    {
        using var a = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        using var b = await _fixture.SignInAsync(_fixture.WorkspaceB.OwnerEmail, cancellationToken: Ct);
        const string session = Route + "/checkout/cs_test_pack1?kind=Pack";

        Assert.Equal("Pending", (await BodyAsync(await a.GetAsync(session, Ct))).GetProperty("state").GetString());

        await RecordPackAsync(_fixture.WorkspaceA.OwnerEmail, "cs_test_pack1", 2500m);

        var complete = await BodyAsync(await a.GetAsync(session, Ct));
        Assert.Equal("Complete", complete.GetProperty("state").GetString());
        Assert.Equal(2500m, complete.GetProperty("credits").GetDecimal());

        Assert.Equal("Pending", (await BodyAsync(await b.GetAsync(session, Ct))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_plan_reads_complete_once_its_paid_subscription_is_recorded()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);
        const string session = Route + "/checkout/cs_test_plan1?kind=Plan";

        Assert.Equal("Pending", (await BodyAsync(await owner.GetAsync(session, Ct))).GetProperty("state").GetString());

        await SubscribeAsync(_fixture.WorkspaceA.OwnerEmail, "creator");

        Assert.Equal("Complete", (await BodyAsync(await owner.GetAsync(session, Ct))).GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_checkout_that_says_neither_what_it_was_for_nor_its_session_is_a_400()
    {
        using var owner = await _fixture.SignInAsync(_fixture.WorkspaceA.OwnerEmail, cancellationToken: Ct);

        var response = await owner.GetAsync(Route + "/checkout/cs_test_1", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BillingAccountErrors.Invalid, await CodeAsync(response));
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static Dictionary<string, string> Key(string key) => new() { ["Idempotency-Key"] = key };

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await BodyAsync(response)).GetProperty("code").GetString();

    private static string SubscriptionIdOf(string email) => "sub_" + email.Split('@')[0].Replace("-", string.Empty);

    private async Task<string> AccountIdAsync(string email)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Users
            .Where(user => user.Email == email)
            .Select(user => user.Id)
            .SingleAsync(Ct);
    }

    /// <summary>Makes the account older, so its trial has ended.</summary>
    private async Task AgeAsync(string email, TimeSpan by)
    {
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var user = await db.Users.SingleAsync(candidate => candidate.Email == email, Ct);
        user.CreatedAt -= by;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>What the webhook writes for a paid subscription (AP.6.2), written directly.</summary>
    private async Task SubscribeAsync(string email, string planKey, string? scheduleId = null)
    {
        var accountId = await AccountIdAsync(email);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.BillingSubscriptions.Add(new BillingSubscription
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExternalSubscriptionId = SubscriptionIdOf(email),
            ExternalCustomerId = "cus_" + SubscriptionIdOf(email),
            IsLiveMode = false,
            Status = BillingSubscriptionStatus.Active,
            ItemId = "si_1",
            PlanKey = planKey,
            Cycle = BillingCycle.Monthly,
            CurrentPeriodEnd = now.AddDays(20),
            ScheduleId = scheduleId,
            LatestInvoicePaid = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>What the webhook writes for a paid pack (AP.6.3), written directly.</summary>
    private async Task RecordPackAsync(string email, string sessionId, decimal credits)
    {
        var accountId = await AccountIdAsync(email);
        await using var scope = _fixture.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var grant = new AccountCreditGrant
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Source = CreditGrantSource.Purchase,
            CreditsGranted = credits,
            CreditsRemaining = credits,
            ExternalReferenceId = sessionId,
            GrantedAt = now,
        };

        db.AccountCreditGrants.Add(grant);
        db.BillingPackPurchases.Add(new BillingPackPurchase
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            ExternalSessionId = sessionId,
            ExternalPaymentReference = "pi_" + sessionId,
            IsLiveMode = false,
            PackKey = "small",
            CreditsGranted = credits,
            GrantId = grant.Id,
            PurchasedAt = now,
        });

        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A provider in test mode that records what it was asked and answers with fixed pages.</summary>
    private sealed class RecordingGateway : IBillingPaymentGateway
    {
        private int _sessions;

        public BillingPaymentMode Mode { get; set; } = BillingPaymentMode.Test;

        public List<SubscriptionCheckoutRequest> SubscriptionCheckouts { get; } = [];

        public List<PackCheckoutRequest> PackCheckouts { get; } = [];

        public List<BillingPortalRequest> Portals { get; } = [];

        public List<PlanUpgradeRequest> Upgrades { get; } = [];

        public List<ScheduledPlanChangeRequest> Scheduled { get; } = [];

        public List<string> Released { get; } = [];

        public Task<BillingCustomerCreation> CreateCustomerAsync(string accountId, CancellationToken cancellationToken) =>
            Task.FromResult(new BillingCustomerCreation(BillingPaymentOutcome.Succeeded, "cus_" + Guid.NewGuid().ToString("N")));

        public Task<BillingRedirect> StartSubscriptionCheckoutAsync(SubscriptionCheckoutRequest request, CancellationToken cancellationToken)
        {
            lock (SubscriptionCheckouts)
            {
                SubscriptionCheckouts.Add(request);
            }

            return Page($"https://checkout.example/session-{Interlocked.Increment(ref _sessions)}");
        }

        public Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken)
        {
            lock (PackCheckouts)
            {
                PackCheckouts.Add(request);
            }

            return Page($"https://checkout.example/pack-{Interlocked.Increment(ref _sessions)}");
        }

        public Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken)
        {
            Portals.Add(request);

            return Page("https://portal.example/session");
        }

        public Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken)
        {
            Upgrades.Add(request);

            return Page("https://portal.example/upgrade");
        }

        public Task<BillingPaymentOutcome> SchedulePlanChangeAsync(ScheduledPlanChangeRequest request, CancellationToken cancellationToken)
        {
            Scheduled.Add(request);

            return Task.FromResult(BillingPaymentOutcome.Succeeded);
        }

        public Task<BillingPaymentOutcome> ReleaseScheduleAsync(
            string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken)
        {
            Released.Add(externalScheduleId);

            return Task.FromResult(BillingPaymentOutcome.Succeeded);
        }

        public Task<BillingSubscriptionRead> GetSubscriptionAsync(string externalSubscriptionId, CancellationToken cancellationToken) =>
            Task.FromResult(BillingSubscriptionRead.Of(BillingPaymentOutcome.Unavailable));

        public Task<BillingPackCheckoutRead> GetPackCheckoutAsync(string externalSessionId, CancellationToken cancellationToken) =>
            Task.FromResult(BillingPackCheckoutRead.Of(BillingPaymentOutcome.Unavailable));

        public Task<BillingPaymentReversalRead> GetPaymentReversalAsync(
            string externalReference, BillingPackReversal kind, CancellationToken cancellationToken) =>
            Task.FromResult(BillingPaymentReversalRead.Of(BillingPaymentOutcome.Unavailable));

        private static Task<BillingRedirect> Page(string url) =>
            Task.FromResult(new BillingRedirect(BillingPaymentOutcome.Succeeded, new Uri(url)));
    }
}
