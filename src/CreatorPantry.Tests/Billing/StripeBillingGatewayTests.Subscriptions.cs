using System.Net;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Tests.Auth;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The adapter's half of a running subscription (AP.6.2): reading one again, the upgrade's confirmation page, and the
/// schedule that holds a downgrade to the end of the paid period.
/// </summary>
/// <remarks>
/// The subscription and schedule bodies are authored to Stripe's objects at the pinned version, using the price ids
/// the stub configures (<c>Fixtures/Stripe/README.md</c>); nothing here calls Stripe.
/// </remarks>
public sealed partial class StripeBillingGatewayTests
{
    private const string SubscriptionId = "sub_1MowQVLkdIwHu7ixeRlqHVzs";

    private const string ItemId = "si_Na6dzxczY5fwHx";

    private const string ScheduleId = "sub_sched_1Mr3YdLkdIwHu7ixjop3qtff";

    private const string UpgradeConfiguration = "bpc_1OtUpgradeConfig";

    // ---- reading a subscription ----------------------------------------------------------------------------

    [Fact]
    public async Task A_subscription_is_read_with_its_schedule_expanded_and_mapped_to_the_catalogue()
    {
        var stripe = new StubStripe(Ok("subscription-active"));

        var read = await stripe.Gateway().GetSubscriptionAsync(SubscriptionId, Ct);

        var sent = Assert.Single(stripe.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal($"https://api.stripe.example/v1/subscriptions/{SubscriptionId}?expand%5B%5D=schedule&expand%5B%5D=latest_invoice", sent.Uri);
        Assert.Null(sent.IdempotencyKey);
        Assert.Empty(sent.Form);

        Assert.Equal(BillingPaymentOutcome.Succeeded, read.Outcome);
        Assert.Equal(
            new BillingSubscriptionSnapshot(
                SubscriptionId,
                Customer,
                Account,
                IsLiveMode: false,
                BillingSubscriptionStatus.Active,
                ItemId,
                "creator",
                BillingCycle.Monthly,
                CurrentPeriodEnd: DateTimeOffset.FromUnixTimeSeconds(2107440000),
                CancelAt: null,
                EndedAt: null,
                ScheduleId: null,
                NextPhase: null,
                LatestInvoicePaid: true),
            read.Subscription);
    }

    /// <summary>An upgrade's proration still open, or an invoice not expanded, reads as not paid: an upgrade waits.</summary>
    [Theory]
    [InlineData("\"status\": \"paid\" }", "\"status\": \"open\" }")]
    [InlineData("\"latest_invoice\": { \"id\": \"in_1MowQWLkdIwHu7ixuzkSPfKd\", \"object\": \"invoice\", \"amount_due\": 1200, \"status\": \"paid\" }", "\"latest_invoice\": \"in_1MowQWLkdIwHu7ixuzkSPfKd\"")]
    public async Task A_latest_invoice_not_paid_is_read_as_not_paid(string from, string to)
    {
        var body = Fixture("subscription-active");
        Assert.Contains(from, body, StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body.Replace(from, to, StringComparison.Ordinal)));

        Assert.False((await stripe.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Subscription!.LatestInvoicePaid);
    }

    /// <summary>The period lives on the item at the pinned version; a downgrade waiting for its end is the schedule's next phase.</summary>
    [Fact]
    public async Task A_scheduled_downgrade_is_read_as_the_next_phase_at_the_end_of_the_period()
    {
        var stripe = new StubStripe(Ok("subscription-scheduled"));

        var subscription = (await stripe.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Subscription!;

        Assert.Equal("pro", subscription.PlanKey);
        Assert.Equal(ScheduleId, subscription.ScheduleId);
        Assert.Equal(
            new BillingScheduledPhase("creator", BillingCycle.Monthly, DateTimeOffset.FromUnixTimeSeconds(2107440000)),
            subscription.NextPhase);
    }

    [Fact]
    public async Task A_cancellation_at_period_end_is_read_as_ending_then()
    {
        var body = Fixture("subscription-active").Replace("\"cancel_at_period_end\": false", "\"cancel_at_period_end\": true", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        var subscription = (await stripe.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Subscription!;

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2107440000), subscription.CancelAt);
    }

    /// <summary>A price no plan sells is said as none, never guessed at; the webhook then asks for an operator.</summary>
    [Fact]
    public async Task A_price_this_host_does_not_sell_is_read_as_no_plan()
    {
        var body = Fixture("subscription-active").Replace("price_creator_monthly", "price_made_in_the_dashboard", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        var subscription = (await stripe.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Subscription!;

        Assert.Null(subscription.PlanKey);
        Assert.Equal(BillingCycle.Unspecified, subscription.Cycle);
    }

    [Theory]
    [InlineData("past_due", BillingSubscriptionStatus.PastDue)]
    [InlineData("canceled", BillingSubscriptionStatus.Canceled)]
    [InlineData("unpaid", BillingSubscriptionStatus.Unpaid)]
    [InlineData("incomplete", BillingSubscriptionStatus.Incomplete)]
    [InlineData("incomplete_expired", BillingSubscriptionStatus.IncompleteExpired)]
    [InlineData("paused", BillingSubscriptionStatus.Paused)]
    [InlineData("something_new", BillingSubscriptionStatus.Unspecified)]
    public async Task Each_status_is_read_into_the_applications_terms(string status, BillingSubscriptionStatus expected)
    {
        var body = Fixture("subscription-active").Replace("\"status\": \"active\"", $"\"status\": \"{status}\"", StringComparison.Ordinal);
        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        Assert.Equal(expected, (await stripe.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Subscription!.Status);
    }

    /// <summary>
    /// Better unread than half read: a schedule that came back as a bare id would leave what it has waiting unknown,
    /// and reading that as "nothing waiting" would withdraw a downgrade. So would a second item, or the other mode.
    /// </summary>
    [Theory]
    [InlineData("\"schedule\": null", "\"schedule\": \"sub_sched_1Mr3YdLkdIwHu7ixjop3qtff\"")]
    [InlineData("\"livemode\": false,\n  \"metadata\"", "\"livemode\": true,\n  \"metadata\"")]
    [InlineData("\"id\": \"sub_1MowQVLkdIwHu7ixeRlqHVzs\",\n  \"object\"", "\"id\": \"sub_someone_else\",\n  \"object\"")]
    [InlineData("\"current_period_end\": 2107440000,", "")]
    public async Task A_subscription_this_contract_cannot_read_whole_is_unreadable(string from, string to)
    {
        var body = Fixture("subscription-active").ReplaceLineEndings("\n");
        Assert.Contains(from, body, StringComparison.Ordinal);

        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body.Replace(from, to, StringComparison.Ordinal)));

        Assert.Equal(BillingPaymentOutcome.Unreadable, (await stripe.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Outcome);
    }

    /// <summary>
    /// Every subscription a CreatorPantry checkout makes has one item. One with two was made elsewhere, and neither its
    /// plan nor an upgrade's single-item confirmation page can be read off it.
    /// </summary>
    [Fact]
    public async Task A_subscription_with_more_than_one_item_is_unreadable()
    {
        var body = Fixture("subscription-active").ReplaceLineEndings("\n");
        const string end = "    ],\n    \"has_more\"";
        Assert.Contains(end, body, StringComparison.Ordinal);

        body = body.Replace(
            end,
            "      ,{ \"id\": \"si_second\", \"price\": { \"id\": \"price_pro_monthly\" }, \"current_period_end\": 2107440000 }\n" + end,
            StringComparison.Ordinal);

        var stripe = new StubStripe(new Scripted(HttpStatusCode.OK, body));

        Assert.Equal(BillingPaymentOutcome.Unreadable, (await stripe.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Outcome);
    }

    /// <summary>
    /// A read saves nothing against any key, so unlike a write it is repeated on a 5xx. A refusal is still final, and an
    /// outage that outlasts the attempts is Unavailable — which the webhook retries from the outbox.
    /// </summary>
    [Fact]
    public async Task A_read_is_repeated_on_a_provider_failure_and_a_refusal_is_not()
    {
        var recovered = new StubStripe(Error(HttpStatusCode.InternalServerError, "error-api"), Ok("subscription-active"));
        Assert.Equal(BillingPaymentOutcome.Succeeded, (await recovered.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Outcome);
        Assert.Equal(2, recovered.Requests.Count);

        var missing = new StubStripe(Error(HttpStatusCode.NotFound, "error-resource-missing"));
        Assert.Equal(BillingPaymentOutcome.Refused, (await missing.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Outcome);
        Assert.Single(missing.Requests);

        var down = new StubStripe(
            Error(HttpStatusCode.InternalServerError, "error-api"),
            Error(HttpStatusCode.InternalServerError, "error-api"),
            Error(HttpStatusCode.InternalServerError, "error-api"));
        Assert.Equal(BillingPaymentOutcome.Unavailable, (await down.Gateway().GetSubscriptionAsync(SubscriptionId, Ct)).Outcome);
        Assert.Equal(3, down.Requests.Count);
    }

    /// <summary>The id becomes part of a request path, so only an id shaped like Stripe's is ever sent.</summary>
    [Theory]
    [InlineData("sub_../../v1/customers")]
    [InlineData("cus_NffrFeUfNV2Hib")]
    [InlineData("sub_")]
    public async Task An_id_that_is_not_a_subscriptions_is_refused_before_anything_is_sent(string id)
    {
        var stripe = new StubStripe();

        await Assert.ThrowsAsync<ArgumentException>(() => stripe.Gateway().GetSubscriptionAsync(id, Ct));
        Assert.Empty(stripe.Requests);
    }

    // ---- the upgrade page -------------------------------------------------------------------------------

    [Fact]
    public async Task An_upgrade_is_a_confirmation_page_for_the_one_item_under_the_upgrade_configuration()
    {
        var stripe = new StubStripe(Ok("portal-session"));

        var redirect = await stripe.Gateway(upgradePortalConfiguration: UpgradeConfiguration).StartPlanUpgradeAsync(Upgrade(), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, redirect.Outcome);
        Assert.Equal("billing.stripe.com", redirect.Url!.Host);

        var sent = Assert.Single(stripe.Requests);
        Assert.Equal("https://api.stripe.example/v1/billing_portal/sessions", sent.Uri);
        Assert.Equal(PortalCustomer, Value(sent.Form, "customer"));
        Assert.Equal(UpgradeConfiguration, Value(sent.Form, "configuration"));
        Assert.Equal("subscription_update_confirm", Value(sent.Form, "flow_data[type]"));
        Assert.Equal(SubscriptionId, Value(sent.Form, "flow_data[subscription_update_confirm][subscription]"));
        Assert.Equal(ItemId, Value(sent.Form, "flow_data[subscription_update_confirm][items][0][id]"));
        Assert.Equal("price_pro_monthly", Value(sent.Form, "flow_data[subscription_update_confirm][items][0][price]"));
        Assert.Equal("1", Value(sent.Form, "flow_data[subscription_update_confirm][items][0][quantity]"));
        Assert.Equal("redirect", Value(sent.Form, "flow_data[after_completion][type]"));
        Assert.Equal("https://app.creatorpantry.example/billing/return", Value(sent.Form, "flow_data[after_completion][redirect][return_url]"));
        Assert.StartsWith("cp-upgrade-v1-", sent.IdempotencyKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without its own configuration the page would open under the portal's, where plan switching is off and the flow
    /// fails; with no price there is nothing to move to. Either way nothing is sent.
    /// </summary>
    [Fact]
    public async Task An_upgrade_with_no_configuration_or_no_price_sends_nothing()
    {
        var unconfigured = new StubStripe();
        Assert.Equal(
            BillingPaymentOutcome.NotConfigured,
            (await unconfigured.Gateway().StartPlanUpgradeAsync(Upgrade(), Ct)).Outcome);

        var unpriced = new StubStripe();
        Assert.Equal(
            BillingPaymentOutcome.NotOffered,
            (await unpriced.Gateway(upgradePortalConfiguration: UpgradeConfiguration).StartPlanUpgradeAsync(Upgrade(plan: "studio"), Ct)).Outcome);

        Assert.Empty(unconfigured.Requests);
        Assert.Empty(unpriced.Requests);
    }

    // ---- the schedule ---------------------------------------------------------------------------------------

    /// <summary>
    /// A schedule made from the subscription, then set to two phases: the current one exactly as Stripe reported it,
    /// and the lower price from its end. Nothing prorated, and released once the second has run.
    /// </summary>
    [Fact]
    public async Task A_downgrade_is_a_schedule_whose_next_phase_starts_when_the_paid_period_ends()
    {
        var stripe = new StubStripe(Ok("subscription-schedule"), Ok("subscription-schedule"));

        var outcome = await stripe.Gateway().SchedulePlanChangeAsync(
            new ScheduledPlanChangeRequest(SubscriptionId, null, "creator", BillingCycle.Monthly, "downgrade-1"), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, outcome);
        Assert.Equal(2, stripe.Requests.Count);

        var create = stripe.Requests[0];
        Assert.Equal("https://api.stripe.example/v1/subscription_schedules", create.Uri);
        Assert.Equal([new KeyValuePair<string, string>("from_subscription", SubscriptionId)], create.Form);
        Assert.StartsWith("cp-schedule-create-v1-", create.IdempotencyKey, StringComparison.Ordinal);

        var update = stripe.Requests[1];
        Assert.Equal($"https://api.stripe.example/v1/subscription_schedules/{ScheduleId}", update.Uri);
        Assert.Equal("release", Value(update.Form, "end_behavior"));
        Assert.Equal("none", Value(update.Form, "proration_behavior"));
        Assert.Equal("price_pro_monthly", Value(update.Form, "phases[0][items][0][price]"));
        Assert.Equal("2104761600", Value(update.Form, "phases[0][start_date]"));
        Assert.Equal("2107440000", Value(update.Form, "phases[0][end_date]"));
        Assert.Equal("price_creator_monthly", Value(update.Form, "phases[1][items][0][price]"));
        Assert.Equal("month", Value(update.Form, "phases[1][duration][interval]"));
        Assert.StartsWith("cp-schedule-update-v1-", update.IdempotencyKey, StringComparison.Ordinal);

        // The caller's key, derived per call: the same change asked again is the same schedule and the same update.
        Assert.NotEqual(create.IdempotencyKey, update.IdempotencyKey);
        Assert.DoesNotContain("downgrade-1", create.IdempotencyKey!, StringComparison.Ordinal);
    }

    /// <summary>A subscription a schedule already manages has that schedule changed, never a second one made.</summary>
    [Fact]
    public async Task A_subscription_already_scheduled_has_its_schedule_read_and_updated()
    {
        var stripe = new StubStripe(Ok("subscription-schedule"), Ok("subscription-schedule"));

        var outcome = await stripe.Gateway().SchedulePlanChangeAsync(
            new ScheduledPlanChangeRequest(SubscriptionId, ScheduleId, "creator", BillingCycle.Annual, "downgrade-2"), Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, outcome);
        Assert.Equal(HttpMethod.Get, stripe.Requests[0].Method);
        Assert.Equal($"https://api.stripe.example/v1/subscription_schedules/{ScheduleId}", stripe.Requests[0].Uri);
        Assert.Equal("price_creator_annual", Value(stripe.Requests[1].Form, "phases[1][items][0][price]"));
        Assert.Equal("year", Value(stripe.Requests[1].Form, "phases[1][duration][interval]"));
    }

    [Fact]
    public async Task A_schedule_that_could_not_be_made_stops_the_change_where_it_is()
    {
        var stripe = new StubStripe(Error(HttpStatusCode.BadRequest, "error-resource-missing"));

        var outcome = await stripe.Gateway().SchedulePlanChangeAsync(
            new ScheduledPlanChangeRequest(SubscriptionId, null, "creator", BillingCycle.Monthly, "downgrade-3"), Ct);

        Assert.Equal(BillingPaymentOutcome.Refused, outcome);
        Assert.Single(stripe.Requests);
    }

    [Fact]
    public async Task Releasing_a_schedule_leaves_the_subscription_as_it_is()
    {
        var stripe = new StubStripe(Ok("subscription-schedule-released"));

        var outcome = await stripe.Gateway().ReleaseScheduleAsync(ScheduleId, "release-1", Ct);

        Assert.Equal(BillingPaymentOutcome.Succeeded, outcome);
        var sent = Assert.Single(stripe.Requests);
        Assert.Equal($"https://api.stripe.example/v1/subscription_schedules/{ScheduleId}/release", sent.Uri);
        Assert.Empty(sent.Form);
        Assert.StartsWith("cp-schedule-release-v1-", sent.IdempotencyKey, StringComparison.Ordinal);
    }

    /// <summary>An answer saying the schedule is still active is not a release, whatever its status code.</summary>
    [Fact]
    public async Task A_release_answered_with_a_schedule_still_active_is_unreadable()
    {
        var stripe = new StubStripe(Ok("subscription-schedule"));

        Assert.Equal(BillingPaymentOutcome.Unreadable, await stripe.Gateway().ReleaseScheduleAsync(ScheduleId, "release-2", Ct));
    }

    [Fact]
    public async Task Nothing_about_a_subscription_reaches_a_log_but_its_shape()
    {
        var logs = new CapturingLoggerProvider();
        var stripe = new StubStripe(
            Ok("subscription-active"),
            Error(HttpStatusCode.NotFound, "error-resource-missing"),
            Ok("portal-session"),
            Ok("subscription-schedule"),
            Ok("subscription-schedule"));
        var gateway = stripe.Gateway(logs: logs, upgradePortalConfiguration: UpgradeConfiguration);

        await gateway.GetSubscriptionAsync(SubscriptionId, Ct);
        await gateway.GetSubscriptionAsync(SubscriptionId, Ct);
        await gateway.StartPlanUpgradeAsync(Upgrade(), Ct);
        await gateway.SchedulePlanChangeAsync(
            new ScheduledPlanChangeRequest(SubscriptionId, null, "creator", BillingCycle.Monthly, "downgrade-4"), Ct);

        var all = string.Join("\n", logs.Entries);
        Assert.Contains("resource_missing", all, StringComparison.Ordinal);

        foreach (var secret in new[]
        {
            SecretKey, Account, Customer, PortalCustomer, SubscriptionId, ItemId, ScheduleId, UpgradeConfiguration,
            "price_", "No such price", "billing.stripe.com", "creatorpantry.example", "downgrade-4",
        })
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);
        }
    }

    private static PlanUpgradeRequest Upgrade(string plan = "pro") =>
        new(PortalCustomer, SubscriptionId, ItemId, plan, BillingCycle.Monthly, Cancel, Success);
}
