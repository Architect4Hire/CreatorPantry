using System.Net;
using CreatorPantry.Domain.Managers.Limits;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.AiUsage;
using CreatorPantry.Domain.Modules.Billing;
using CreatorPantry.Domain.Modules.Billing.Gateways;
using CreatorPantry.Domain.Modules.Billing.Integration.Stripe;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// How a host gets a payment provider (AP.6.1): only from a whole connection string, with the key on a header and
/// never in an address, in test mode unless live mode was allowed, and with every paid plan and pack priced.
/// </summary>
public sealed class StripeBillingRegistrationTests
{
    private const string TestKey = "rk_test_SECRET_do_not_log_77d1";

    private const string LiveKey = "rk_live_SECRET_do_not_log_77d1";

    private const string SigningSecret = "whsec_SECRET_do_not_log_09ab";

    private static string Connection(string key = TestKey) =>
        $"Endpoint=https://api.stripe.example;Key={key};WebhookSecret={SigningSecret}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A trial, one paid plan and one pack, at development's figures, each priced as startup requires.</summary>
    private static (string, string?)[] Catalogue() =>
    [
        ("Billing:DefaultPlan", "trial"),
        ("Billing:Plans:0:Key", "trial"),
        ("Billing:Plans:0:Name", "Trial"),
        ("Billing:Plans:0:Credits", "1000"),
        ("Billing:Plans:0:DailyImageCap", "5"),
        ("Billing:Plans:0:Period", "Once"),
        ("Billing:Plans:0:RunningImages", "1"),
        ("Billing:Plans:0:QualityClasses:0", "standard"),
        ("Billing:Plans:0:TrialDays", "14"),
        ("Billing:Plans:0:TrialImageCap", "10"),
        ("Billing:Plans:1:Key", "creator"),
        ("Billing:Plans:1:Name", "Creator"),
        ("Billing:Plans:1:MonthlyPrice", "12"),
        ("Billing:Plans:1:AnnualMonthlyPrice", "9"),
        ("Billing:Plans:1:Credits", "4500"),
        ("Billing:Plans:1:DailyImageCap", "25"),
        ("Billing:Plans:1:Period", "Monthly"),
        ("Billing:Plans:1:RunningImages", "1"),
        ("Billing:Plans:1:QualityClasses:0", "standard"),
        ("Billing:Packs:0:Key", "small"),
        ("Billing:Packs:0:Credits", "2500"),
        ("Billing:Packs:0:Price", "10"),
        ("Billing:Stripe:Prices:Plans:creator:Monthly", "price_creator_monthly"),
        ("Billing:Stripe:Prices:Plans:creator:Annual", "price_creator_annual"),
        ("Billing:Stripe:Prices:Packs:small", "price_pack_small"),
    ];

    // ---- which gateway resolves --------------------------------------------------------------------------

    [Fact]
    public void With_no_connection_string_the_host_starts_with_the_unconfigured_gateway()
    {
        using var provider = Build(connection: null, Catalogue());
        using var scope = provider.CreateScope();

        var gateway = scope.ServiceProvider.GetRequiredService<IBillingPaymentGateway>();
        Assert.IsType<UnconfiguredBillingPaymentGateway>(gateway);
        Assert.Equal(BillingPaymentMode.NotConfigured, gateway.Mode);
    }

    /// <summary>Unpriced plans are no reason to refuse a host that has no Stripe to sell them through.</summary>
    [Fact]
    public void Without_stripe_the_price_map_is_not_checked()
    {
        using var provider = Build(connection: null, [.. Catalogue(), ("Billing:Stripe:Prices:Packs:small", "not-a-price")]);

        Assert.Null(provider.GetService<IOptions<StripeBillingSettings>>()?.Value.PriceFor("nothing"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_a_test_key_stripe_is_the_gateway_and_the_key_goes_out_as_a_header_only(bool moduleFirst)
    {
        var handler = new CapturingHandler();
        using var provider = Build(Connection(), Catalogue(), handler, moduleFirst);
        using var scope = provider.CreateScope();

        var gateway = scope.ServiceProvider.GetRequiredService<IBillingPaymentGateway>();
        Assert.IsType<StripeBillingGateway>(gateway);
        Assert.Equal(BillingPaymentMode.Test, gateway.Mode);

        await gateway.CreateCustomerAsync("user-sam", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.stripe.example/v1/customers", request.Uri);
        Assert.Equal($"Bearer {TestKey}", request.Authorization);
        Assert.Equal("2026-09-30.endive", request.Version);
        Assert.DoesNotContain(TestKey, request.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(SigningSecret, request.Body, StringComparison.Ordinal);
    }

    /// <summary>A half-made provider is no provider, never a host that will not start.</summary>
    [Theory]
    [InlineData("Endpoint=https://api.stripe.example")]
    [InlineData("Key=rk_test_abcdefgh123")]
    [InlineData("Endpoint=https://api.stripe.example;Key=")]
    [InlineData("Endpoint=https://api.stripe.example;Key=pk_test_abcdefgh123")]
    [InlineData("Endpoint=https://api.stripe.example;Key=sk_test_")]
    [InlineData("Endpoint=https://api.stripe.example;Key=whatever_abcdefgh123")]
    [InlineData("Endpoint=http://api.stripe.example;Key=rk_test_abcdefgh123")]
    [InlineData("Endpoint=https://api.stripe.example?x=1;Key=rk_test_abcdefgh123")]
    [InlineData("Endpoint=not a url;Key=rk_test_abcdefgh123")]
    public void An_unusable_connection_string_leaves_the_unconfigured_gateway(string connection)
    {
        using var provider = Build(connection, Catalogue());
        using var scope = provider.CreateScope();

        Assert.IsType<UnconfiguredBillingPaymentGateway>(scope.ServiceProvider.GetRequiredService<IBillingPaymentGateway>());
    }

    /// <summary>Test mode until the owner says otherwise (AP.6.1): a live key alone is a host that does not start.</summary>
    [Fact]
    public void A_live_key_stops_the_host_unless_live_mode_was_allowed_and_says_which_setting_but_not_which_key()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Build(Connection(LiveKey), Catalogue()));

        Assert.Contains("Billing:Stripe:AllowLiveMode", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LiveKey, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_live_key_with_live_mode_allowed_is_a_live_gateway()
    {
        using var provider = Build(Connection(LiveKey), [.. Catalogue(), ("Billing:Stripe:AllowLiveMode", "true")]);
        using var scope = provider.CreateScope();

        Assert.Equal(BillingPaymentMode.Live, scope.ServiceProvider.GetRequiredService<IBillingPaymentGateway>().Mode);
    }

    // ---- the options -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("sk_test_abcdefgh123", BillingPaymentMode.Test)]
    [InlineData("rk_test_abcdefgh123", BillingPaymentMode.Test)]
    [InlineData("sk_live_abcdefgh123", BillingPaymentMode.Live)]
    [InlineData("rk_live_abcdefgh123", BillingPaymentMode.Live)]
    public void The_mode_is_read_from_the_keys_own_prefix(string key, BillingPaymentMode mode)
    {
        Assert.Equal(mode, StripeBillingOptions.Parse($"Endpoint=https://api.stripe.example;Key={key}")!.Mode);
    }

    [Fact]
    public void Neither_secret_is_in_the_options_text_and_a_signing_secret_that_is_not_one_is_dropped()
    {
        var options = StripeBillingOptions.Parse(Connection())!;

        Assert.Equal(SigningSecret, options.WebhookSecret);
        Assert.DoesNotContain(TestKey, options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SigningSecret, options.ToString(), StringComparison.Ordinal);
        Assert.Equal(new Uri("https://api.stripe.example/"), options.Endpoint);

        Assert.Null(StripeBillingOptions.Parse($"Endpoint=https://api.stripe.example;Key={TestKey};WebhookSecret=not-one")!.WebhookSecret);
        Assert.Null(StripeBillingOptions.Parse($"Endpoint=https://api.stripe.example;Key={TestKey}")!.WebhookSecret);
    }

    // ---- the price map -----------------------------------------------------------------------------------

    [Fact]
    public void A_catalogue_priced_on_every_paid_plan_and_pack_starts()
    {
        using var provider = Build(Connection(), [.. Catalogue(), ("Billing:Stripe:PortalConfiguration", "bpc_1MAhNDLkdIwHu7ixckACO1Jq")]);

        var settings = provider.GetRequiredService<IOptions<StripeBillingSettings>>().Value;
        Assert.Equal("price_creator_annual", settings.PriceFor("creator", BillingCycle.Annual));
        Assert.Equal(("creator", BillingCycle.Annual), settings.PlanFor("price_creator_annual"));
        Assert.Null(settings.PlanFor("price_pack_small"));

        // Keys are matched as the catalogue matches them, without regard to case.
        Assert.Equal("price_pack_small", settings.PriceFor("SMALL"));
    }

    public static TheoryData<string, (string, string?)[]> RefusedPriceMaps() => new()
    {
        { "a paid plan with no annual price", [.. Catalogue(), ("Billing:Stripe:Prices:Plans:creator:Annual", null)] },
        { "a paid plan with no prices at all", [.. Catalogue().Where(setting => !setting.Item1.Contains(":creator:", StringComparison.Ordinal))] },
        { "a pack with no price", [.. Catalogue(), ("Billing:Stripe:Prices:Packs:small", null)] },
        { "a price for the trial, which is not bought", [.. Catalogue(), ("Billing:Stripe:Prices:Plans:trial:Monthly", "price_trial")] },
        { "a price for a plan the catalogue does not have", [.. Catalogue(), ("Billing:Stripe:Prices:Plans:enterprise:Monthly", "price_enterprise")] },
        { "a price for a pack the catalogue does not have", [.. Catalogue(), ("Billing:Stripe:Prices:Packs:huge", "price_huge")] },
        { "an id that is not a price", [.. Catalogue(), ("Billing:Stripe:Prices:Packs:small", "prod_NWjs8kKbJWmuuc")] },
        { "an id with something after it", [.. Catalogue(), ("Billing:Stripe:Prices:Packs:small", "price_pack_small;x")] },
        { "one price for two things", [.. Catalogue(), ("Billing:Stripe:Prices:Packs:small", "price_creator_monthly")] },
        { "a portal configuration that is not one", [.. Catalogue(), ("Billing:Stripe:PortalConfiguration", "conf_1")] },
        { "an upgrade configuration that is not one", [.. Catalogue(), ("Billing:Stripe:UpgradePortalConfiguration", "conf_1")] },
        {
            // The upgrade configuration has plan switching on; shared with the portal, every creator would see it.
            "the upgrade configuration the portal's own",
            [.. Catalogue(), ("Billing:Stripe:PortalConfiguration", "bpc_1Same"), ("Billing:Stripe:UpgradePortalConfiguration", "bpc_1Same")]
        },
    };

    [Theory]
    [MemberData(nameof(RefusedPriceMaps))]
    public void A_price_map_that_would_sell_the_wrong_thing_or_nothing_does_not_start(string reason, (string, string?)[] settings)
    {
        Assert.NotEmpty(reason);
        using var provider = Build(Connection(), settings);

        var refused = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StripeBillingSettings>>().Value);
        Assert.NotEmpty(refused.Failures);
    }

    [Fact]
    public void A_distinct_upgrade_configuration_starts()
    {
        using var provider = Build(Connection(), [
            .. Catalogue(),
            ("Billing:Stripe:PortalConfiguration", "bpc_1MAhNDLkdIwHu7ixckACO1Jq"),
            ("Billing:Stripe:UpgradePortalConfiguration", "bpc_1UpgradeOnly"),
        ]);

        Assert.Equal("bpc_1UpgradeOnly", provider.GetRequiredService<IOptions<StripeBillingSettings>>().Value.UpgradePortalConfiguration);
    }

    // ---- the webhook verifier (AP.6.2) -------------------------------------------------------------------

    /// <summary>A signing secret on the connection string is what makes this host verify deliveries, in either order.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_signing_secret_registers_the_verifier_in_either_order(bool moduleFirst)
    {
        using var provider = Build(Connection(), Catalogue(), moduleFirst: moduleFirst);

        Assert.IsType<StripeWebhookVerifier>(provider.GetRequiredService<IBillingWebhookVerifier>());
    }

    [Fact]
    public void No_signing_secret_leaves_the_host_verifying_nothing()
    {
        using var provider = Build($"Endpoint=https://api.stripe.example;Key={TestKey}", Catalogue());

        Assert.IsType<UnconfiguredBillingWebhookVerifier>(provider.GetRequiredService<IBillingWebhookVerifier>());
    }

    /// <summary>The Worker's registration: handed a string with a secret in it, it still builds no verifier.</summary>
    [Fact]
    public void A_host_that_takes_no_webhooks_builds_no_verifier_even_with_a_secret()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{StripeBillingOptions.ConnectionName}"] = Connection(),
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationTime();
        services.AddLimits(configuration);
        services.AddAiUsageModule(configuration);
        services.AddBillingModule(configuration);
        services.AddStripeBilling(configuration, verifyWebhooks: false);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<UnconfiguredBillingWebhookVerifier>(provider.GetRequiredService<IBillingWebhookVerifier>());
        using var scope = provider.CreateScope();
        Assert.Equal(BillingPaymentMode.Test, scope.ServiceProvider.GetRequiredService<IBillingPaymentGateway>().Mode);
    }

    // ---- the client --------------------------------------------------------------------------------------

    /// <summary>
    /// The production transport, not the test's: no redirects, because the key is a header on every request and a
    /// followed redirect would carry it to another host; and one resilience handler, the breaker, where the
    /// ServiceDefaults standard handler — which retries POSTs — would otherwise be.
    /// </summary>
    [Fact]
    public void The_named_client_follows_no_redirect_and_carries_only_its_breaker()
    {
        using var provider = Build(Connection(), Catalogue(), replaceTransport: false);

        var handlers = new List<HttpMessageHandler>();
        for (HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(StripeBillingOptions.ConnectionName);
            handler is not null;
            handler = (handler as DelegatingHandler)?.InnerHandler)
        {
            handlers.Add(handler);
        }

        var primary = Assert.IsType<SocketsHttpHandler>(handlers[^1]);
        Assert.False(primary.AllowAutoRedirect);
        Assert.Single(handlers, handler => handler.GetType().Name.Contains("Resilience", StringComparison.Ordinal));
    }

    /// <summary>
    /// While Stripe keeps failing the breaker stops sending: the call after the fifth failure reaches nobody and
    /// reads as unavailable, at no cost to the creator who made it.
    /// </summary>
    [Fact]
    public async Task Repeated_failures_open_the_breaker_and_then_nothing_is_sent()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError);
        using var provider = Build(Connection(), Catalogue(), handler);
        using var scope = provider.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IBillingPaymentGateway>();

        for (var call = 0; call < 5; call++)
        {
            Assert.Equal(BillingPaymentOutcome.Unavailable, (await gateway.CreateCustomerAsync($"user-{call}", Ct)).Outcome);
        }

        Assert.Equal(BillingPaymentOutcome.Unavailable, (await gateway.CreateCustomerAsync("user-next", Ct)).Outcome);
        Assert.Equal(5, handler.Requests.Count);
    }

    // ---- the host --------------------------------------------------------------------------------------

    private static ServiceProvider Build(
        string? connection,
        (string Key, string? Value)[] settings,
        CapturingHandler? handler = null,
        bool moduleFirst = true,
        bool replaceTransport = true)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [$"ConnectionStrings:{StripeBillingOptions.ConnectionName}"] = connection,
        };

        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationTime();
        services.AddLimits(configuration);
        services.AddAiUsageModule(configuration);

        if (moduleFirst)
        {
            services.AddBillingModule(configuration);
            services.AddStripeBilling(configuration);
        }
        else
        {
            services.AddStripeBilling(configuration);
            services.AddBillingModule(configuration);
        }

        // Only the transport is replaced; the client's headers and its breaker are the registration's own.
        if (replaceTransport)
        {
            services.AddHttpClient(StripeBillingOptions.ConnectionName).ConfigurePrimaryHttpMessageHandler(() => handler ?? new CapturingHandler());
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed record Captured(string Uri, string? Authorization, string? Version, string Body);

    private sealed class CapturingHandler(HttpStatusCode answer = HttpStatusCode.Unauthorized) : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Captured(
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Stripe-Version", out var versions) ? versions.Single() : null,
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            return new HttpResponseMessage(answer);
        }
    }
}
