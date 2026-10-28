using System.Net.Http.Headers;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace CreatorPantry.Domain.Modules.Billing.Integration.Stripe;

/// <summary>
/// Wires Stripe as this host's payment provider, when it has been given a key (PRICE-010, AP.6.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The credential arrives as one connection string</strong>, <c>ConnectionStrings:billing-stripe</c> —
/// <c>Endpoint=…;Key=…;WebhookSecret=…</c> — which the AppHost composes from two secret parameters. The key is read
/// once, here, put on one named client's <c>Authorization</c> header, and held nowhere a URL, a log line or an API
/// response could pick it up.
/// </para>
/// <para>
/// <strong>Absent or unusable, nothing is registered</strong> and the module's own "not configured" gateway stands:
/// the host starts, and every purchase reads a stable degraded answer. The one exception is a live key on a host
/// that has not allowed live mode, which stops the host — that is not a missing provider but real money nobody
/// decided to take (AP.6.1: test mode until the owner says otherwise).
/// </para>
/// <para>
/// <strong>The price map is checked against the catalogue at startup</strong>, only when Stripe is configured: every
/// paid plan priced on both cycles, every pack priced, no trial priced (B-52), no price for a key the catalogue does
/// not have, and no price id used twice.
/// </para>
/// <para>
/// <strong>The resilience defaults are replaced by a breaker alone.</strong> The standard handler retries POSTs it
/// cannot tell apart, and a 5xx from Stripe is saved against its idempotency key, so a blind retry only replays it.
/// The gateway retries what Stripe says is safe to, with the same key, within bounds it can state; the client keeps
/// only a circuit breaker, which never sends anything twice.
/// </para>
/// <para>The API alone registers it today: checkout and the portal are browser actions. AP.6.4's job would add the Worker.</para>
/// </remarks>
public static class StripeBillingRegistration
{
    /// <summary>The named client's one resilience handler: its circuit breaker.</summary>
    internal const string BreakerName = "billing-stripe-breaker";

    /// <param name="configuration">
    /// Read for <c>ConnectionStrings:billing-stripe</c> and <c>Billing:Stripe</c>. Taking the configuration rather
    /// than the host builder is what lets this live beside the module.
    /// </param>
    public static IServiceCollection AddStripeBilling(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = StripeBillingOptions.Parse(configuration.GetConnectionString(StripeBillingOptions.ConnectionName));

        if (options is null)
        {
            return services;
        }

        var section = configuration.GetSection(StripeBillingSettings.SectionName);

        if (options.Mode == BillingPaymentMode.Live && !section.GetValue(nameof(StripeBillingSettings.AllowLiveMode), false))
        {
            // Says which setting, never which key.
            throw new InvalidOperationException(
                $"ConnectionStrings:{StripeBillingOptions.ConnectionName} holds a live Stripe key, and "
                + $"{StripeBillingSettings.SectionName}:{nameof(StripeBillingSettings.AllowLiveMode)} is not true. "
                + "Billing runs in test mode until the owner allows live mode.");
        }

        services.AddSingleton(options);

        services.AddOptions<StripeBillingSettings>()
            .Bind(section)
            .Validate<IOptions<BillingOptions>>(
                (stripe, billing) => PaidPlans(billing.Value).All(plan =>
                    stripe.PriceFor(plan.Key, BillingCycle.Monthly) is not null
                    && stripe.PriceFor(plan.Key, BillingCycle.Annual) is not null),
                $"A paid plan under {BillingOptions.SectionName}:Plans has no Monthly or no Annual price under "
                    + $"{StripeBillingSettings.SectionName}:Prices:Plans; a plan with no price is one nobody can buy.")
            .Validate<IOptions<BillingOptions>>(
                // A trial is not bought (B-52: no card), so a price for one would be a checkout for something free.
                (stripe, billing) => billing.Value.Plans
                    .Where(plan => plan.TrialDays is not null)
                    .All(plan => !stripe.Prices.Plans.ContainsKey(plan.Key)),
                $"A trial plan has a price under {StripeBillingSettings.SectionName}:Prices:Plans; a trial is not bought.")
            .Validate<IOptions<BillingOptions>>(
                (stripe, billing) => billing.Value.Packs.All(pack => stripe.PriceFor(pack.Key) is not null),
                $"A pack under {BillingOptions.SectionName}:Packs has no price under {StripeBillingSettings.SectionName}:Prices:Packs.")
            .Validate<IOptions<BillingOptions>>(
                // A price for a key nobody can ask for is a typo, and the plan it was meant for is unpriced.
                (stripe, billing) =>
                    stripe.Prices.Plans.Keys.All(key => billing.Value.Plans.Any(plan => Same(plan.Key, key)))
                    && stripe.Prices.Packs.Keys.All(key => billing.Value.Packs.Any(pack => Same(pack.Key, key))),
                $"{StripeBillingSettings.SectionName}:Prices names a plan or pack {BillingOptions.SectionName} does not have.")
            .Validate(
                stripe => stripe.AllPriceIds().All(id => id is null || IsPriceId(id)),
                $"A price under {StripeBillingSettings.SectionName}:Prices is not a Stripe price id (price_…).")
            .Validate(
                // Two plans on one price would charge one and grant the other; a webhook could not tell them apart.
                stripe =>
                {
                    var ids = stripe.AllPriceIds().OfType<string>().ToList();
                    return ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;
                },
                $"One price id is used twice under {StripeBillingSettings.SectionName}:Prices.")
            .Validate(
                stripe => stripe.PortalConfiguration is null
                    || (stripe.PortalConfiguration.StartsWith("bpc_", StringComparison.Ordinal)
                        && stripe.PortalConfiguration.Length <= BillingPolicy.ExternalReferenceMaxLength),
                $"{StripeBillingSettings.SectionName}:PortalConfiguration is not a portal configuration id (bpc_…).")
            .ValidateOnStart();

        // RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the supported opt-out from
        // what ServiceDefaults applies to every HttpClient.
#pragma warning disable EXTEXP0001
        services
            .AddHttpClient(StripeBillingOptions.ConnectionName, client =>
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
                client.DefaultRequestHeaders.Add("Stripe-Version", StripeBillingOptions.ApiVersion);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                // The backstop for a caller that brought no bound of its own. The gateway bounds each attempt
                // well inside this.
                client.Timeout = TimeSpan.FromSeconds(60);
            })
            // No redirects: the key is a header on every request, and nothing should carry it to an address other
            // than the configured one. A 3xx is read as an answer this contract does not understand.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllResilienceHandlers()

            // A breaker and nothing else: it counts failures and stops sending while Stripe keeps failing
            // (external.md), so an outage costs each click no time at all rather than the gateway's deadline. It
            // never retries — what is safe to repeat is the gateway's to decide, with the same idempotency key.
            .AddResilienceHandler(BreakerName, pipeline => pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(30),
            }));
#pragma warning restore EXTEXP0001

        // Registered after the module's TryAdd or before it, this is the gateway that resolves.
        services.AddScoped<IBillingPaymentGateway>(provider => new StripeBillingGateway(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(StripeBillingOptions.ConnectionName),
            options,
            provider.GetRequiredService<IOptions<StripeBillingSettings>>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ILogger<StripeBillingGateway>>()));

        return services;
    }

    /// <summary>The plans that are bought: every plan but a trial (B-52).</summary>
    private static IEnumerable<PlanOption> PaidPlans(BillingOptions billing) =>
        billing.Plans.Where(plan => plan.TrialDays is null);

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsPriceId(string id) =>
        id.StartsWith("price_", StringComparison.Ordinal)
        && id.Length > "price_".Length
        && id.Length <= BillingPolicy.ExternalReferenceMaxLength
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
