using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Integration.Stripe;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The webhook route on the real API host (AP.6.2): anonymous, verified over the raw body, 200 at once for an event
/// stored or already stored, and a stable problem for everything else that says nothing of what was sent.
/// </summary>
/// <remarks>
/// What the plan then does is <see cref="BillingWebhookSqlServerTests"/>; the edge in front of this route is
/// <c>StripeWebhookRouteTests</c>.
/// </remarks>
public sealed class BillingWebhookEndpointTests
{
    private const string Route = "/api/v1/webhooks/stripe";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_signed_delivery_is_stored_queued_and_answered_with_an_empty_200()
    {
        await using var host = await StartAsync(withSecret: true);
        var body = Body("evt_Endpoint000001");

        var response = await PostAsync(host, body, StripeSigning.Header(body, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Ct));

        using var scope = host.Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var stored = await context.BillingProviderEvents.SingleAsync(Ct);
        Assert.Equal(("evt_Endpoint000001", BillingProviderEventStatus.Received), (stored.ExternalEventId, stored.Status));
        Assert.Equal(BillingWebhookPolicy.OutboxMessageType, (await context.OutboxMessages.SingleAsync(Ct)).Type);
    }

    /// <summary>The provider's retry of a delivery already answered: 200 again, and nothing more is stored.</summary>
    [Fact]
    public async Task A_replayed_delivery_is_answered_200_and_stores_nothing_more()
    {
        await using var host = await StartAsync(withSecret: true);
        var body = Body("evt_Endpoint000002");

        var first = await PostAsync(host, body, StripeSigning.Header(body, DateTimeOffset.UtcNow));
        var second = await PostAsync(host, body, StripeSigning.Header(body, DateTimeOffset.UtcNow));

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));

        using var scope = host.Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.Equal(1, await context.BillingProviderEvents.CountAsync(Ct));
        Assert.Equal(1, await context.OutboxMessages.CountAsync(Ct));
    }

    /// <summary>
    /// Unsigned, signed by someone else or stale: a 400 the provider will not retry into success, carrying no echo of
    /// the header, the body or the secret.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("t=1,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("forged")]
    public async Task A_delivery_that_does_not_verify_is_a_400_and_stores_nothing(string? header)
    {
        await using var host = await StartAsync(withSecret: true);
        var body = Body("evt_Endpoint000003");

        var response = await PostAsync(host, body, header);
        var text = await response.Content.ReadAsStringAsync(Ct);
        var problem = JsonDocument.Parse(text).RootElement;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BillingWebhookErrors.SignatureInvalid, problem.GetProperty("code").GetString());
        Assert.Equal("none", problem.GetProperty("retry").GetString());

        foreach (var leak in new[] { StripeSigning.Secret, "evt_Endpoint000003", "sub_", header ?? "\u0000" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.Ordinal);
        }

        using var scope = host.Factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().BillingProviderEvents.CountAsync(Ct));
    }

    /// <summary>
    /// Signed by the provider and still not an event: told apart from a bad signature, and not retried into success.
    /// </summary>
    [Fact]
    public async Task A_signed_body_that_is_not_an_event_is_a_400_unreadable()
    {
        await using var host = await StartAsync(withSecret: true);
        const string body = "[]";

        var response = await PostAsync(host, body, StripeSigning.Header(body, DateTimeOffset.UtcNow));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(BillingWebhookErrors.Unreadable, problem.GetProperty("code").GetString());
        Assert.Equal("none", problem.GetProperty("retry").GetString());
    }

    /// <summary>A host given no signing secret cannot verify anything: 503, which the provider retries for days.</summary>
    [Fact]
    public async Task A_host_with_no_signing_secret_answers_503_so_the_provider_tries_again()
    {
        await using var host = await StartAsync(withSecret: false);
        var body = Body("evt_Endpoint000004");

        var response = await PostAsync(host, body, StripeSigning.Header(body, DateTimeOffset.UtcNow));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(BillingWebhookErrors.Unavailable, problem.GetProperty("code").GetString());
        Assert.Equal("later", problem.GetProperty("retry").GetString());
    }

    /// <summary>The route's own body limit, on the action as on the gateway route, before anything is verified.</summary>
    [Fact]
    public async Task A_body_over_the_limit_is_refused_before_it_is_verified()
    {
        await using var host = await StartAsync(withSecret: true);
        var body = new string(' ', BillingWebhookPolicy.MaxBodyBytes + 1);

        var response = await PostAsync(host, body, StripeSigning.Header(body, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>
    /// A chunked body, which says no length up front, is stopped as it is read — by the action's own limit, which the
    /// framework enforces while reading and answers under the stable <c>request_too_large</c> code with its retry
    /// advice (AP.7.2). The controller's own <see cref="BillingWebhookErrors.PayloadTooLarge"/> is the backstop for a
    /// host where that limit is not enforced, and is coded the same way.
    /// </summary>
    [Fact]
    public async Task A_chunked_body_over_the_limit_is_refused_under_a_stable_code()
    {
        await using var host = await StartAsync(withSecret: true);
        using var client = host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StreamContent(new UnseekableStream(new byte[BillingWebhookPolicy.MaxBodyBytes + 1])),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Stripe-Signature", StripeSigning.Header("{}", DateTimeOffset.UtcNow));

        var response = await client.SendAsync(request, Ct);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains(
            problem.GetProperty("code").GetString(),
            new[] { "request_too_large", BillingWebhookErrors.PayloadTooLarge });
        Assert.Equal("none", problem.GetProperty("retry").GetString());
    }

    /// <summary>A stream with no length, so the client sends it chunked and no Content-Length reaches the action.</summary>
    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static string Body(string eventId) =>
        """{"id":""" + "\"" + eventId + "\"" + ""","object":"event","created":""" + StripeSigning.Seconds(DateTimeOffset.UtcNow)
            + ""","livemode":false,"type":"customer.subscription.updated","data":{"object":{"id":"sub_Endpoint1","object":"subscription"}}}""";

    /// <summary>
    /// The real host, with the verifier Stripe's registration would add for a secret — on the host's own clock — and
    /// a provider in test mode, so a test-mode event is this host's.
    /// </summary>
    private static Task<SqliteApiHost> StartAsync(bool withSecret) =>
        SqliteApiHost.StartAsync(configureServices: services =>
        {
            services.RemoveAll<IBillingPaymentGateway>();
            services.AddScoped<IBillingPaymentGateway>(_ => new TestModeGateway());

            if (withSecret)
            {
                services.RemoveAll<IBillingWebhookVerifier>();
                services.AddSingleton<IBillingWebhookVerifier>(provider =>
                    new StripeWebhookVerifier(StripeSigning.Secret, provider.GetRequiredService<IClock>()));
            }
        });

    private static async Task<HttpResponseMessage> PostAsync(SqliteApiHost host, string body, string? header)
    {
        using var client = host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (header is not null)
        {
            request.Headers.TryAddWithoutValidation("Stripe-Signature", header);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>A provider in test mode that is never called: the route only stores and queues.</summary>
    private sealed class TestModeGateway : IBillingPaymentGateway
    {
        private readonly UnconfiguredBillingPaymentGateway _calls = new();

        public BillingPaymentMode Mode => BillingPaymentMode.Test;

        public Task<BillingCustomerCreation> CreateCustomerAsync(string accountId, CancellationToken cancellationToken) =>
            _calls.CreateCustomerAsync(accountId, cancellationToken);

        public Task<BillingRedirect> StartSubscriptionCheckoutAsync(SubscriptionCheckoutRequest request, CancellationToken cancellationToken) =>
            _calls.StartSubscriptionCheckoutAsync(request, cancellationToken);

        public Task<BillingRedirect> StartPackCheckoutAsync(PackCheckoutRequest request, CancellationToken cancellationToken) =>
            _calls.StartPackCheckoutAsync(request, cancellationToken);

        public Task<BillingRedirect> OpenPortalAsync(BillingPortalRequest request, CancellationToken cancellationToken) =>
            _calls.OpenPortalAsync(request, cancellationToken);

        public Task<BillingPackCheckoutRead> GetPackCheckoutAsync(string externalSessionId, CancellationToken cancellationToken) =>
            _calls.GetPackCheckoutAsync(externalSessionId, cancellationToken);

        public Task<BillingPaymentReversalRead> GetPaymentReversalAsync(
            string externalReference, BillingPackReversal kind, CancellationToken cancellationToken) =>
            _calls.GetPaymentReversalAsync(externalReference, kind, cancellationToken);

        public Task<BillingSubscriptionRead> GetSubscriptionAsync(string externalSubscriptionId, CancellationToken cancellationToken) =>
            _calls.GetSubscriptionAsync(externalSubscriptionId, cancellationToken);

        public Task<BillingRedirect> StartPlanUpgradeAsync(PlanUpgradeRequest request, CancellationToken cancellationToken) =>
            _calls.StartPlanUpgradeAsync(request, cancellationToken);

        public Task<BillingPaymentOutcome> SchedulePlanChangeAsync(ScheduledPlanChangeRequest request, CancellationToken cancellationToken) =>
            _calls.SchedulePlanChangeAsync(request, cancellationToken);

        public Task<BillingPaymentOutcome> ReleaseScheduleAsync(
            string externalScheduleId, string idempotencyKey, CancellationToken cancellationToken) =>
            _calls.ReleaseScheduleAsync(externalScheduleId, idempotencyKey, cancellationToken);
    }
}
