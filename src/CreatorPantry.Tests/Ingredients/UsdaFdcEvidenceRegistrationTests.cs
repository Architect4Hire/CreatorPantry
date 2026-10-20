using System.Net;
using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Integration.UsdaFdc;
using CreatorPantry.Tests.Reference;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Tests.Ingredients;

/// <summary>
/// How a host gets an evidence provider (14.1): only from a whole connection string, with the key on a header
/// and never in an address, whichever order the module and the provider are registered in.
/// </summary>
public sealed class UsdaFdcEvidenceRegistrationTests
{
    private const string ApiKey = "fdc_SECRET_KEY_do_not_log_7a02";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void With_no_connection_string_the_host_starts_with_the_unconfigured_gateway()
    {
        using var provider = Build(connectionString: null, moduleFirst: true, new CapturingHandler());
        using var scope = provider.CreateScope();

        Assert.IsType<UnconfiguredReferenceEvidenceGateway>(scope.ServiceProvider.GetRequiredService<IReferenceEvidenceGateway>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_a_key_fdc_is_the_gateway_and_the_key_goes_out_as_a_header_only(bool moduleFirst)
    {
        var handler = new CapturingHandler();
        using var provider = Build($"Endpoint=https://api.fdc.example/fdc/v1;Key={ApiKey}", moduleFirst, handler);
        using var scope = provider.CreateScope();

        var gateway = scope.ServiceProvider.GetRequiredService<IReferenceEvidenceGateway>();
        Assert.IsType<UsdaFdcEvidenceGateway>(gateway);

        await gateway.GetFoodAsync("169761", Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.fdc.example/fdc/v1/food/169761", request.Uri);
        Assert.Equal(ApiKey, request.ApiKeyHeader);
        Assert.Null(request.Authorization);
    }

    [Fact]
    public void Two_scopes_share_one_throttle()
    {
        using var provider = Build($"Endpoint=https://api.fdc.example/fdc/v1;Key={ApiKey}", moduleFirst: true, new CapturingHandler());

        using var one = provider.CreateScope();
        using var two = provider.CreateScope();

        Assert.Same(one.ServiceProvider.GetRequiredService<UsdaFdcThrottle>(), two.ServiceProvider.GetRequiredService<UsdaFdcThrottle>());
    }

    private static ServiceProvider Build(string? connectionString, bool moduleFirst, CapturingHandler handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{UsdaFdcEvidenceOptions.ConnectionName}"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationTime();
        services.AddSingleton<IApplicationCache, FakeApplicationCache>();

        if (moduleFirst)
        {
            services.AddIngredientModule();
            services.AddUsdaFdcEvidence(configuration);
        }
        else
        {
            services.AddUsdaFdcEvidence(configuration);
            services.AddIngredientModule();
        }

        // Only the transport is replaced; the client's headers are the registration's own.
        services.AddHttpClient(UsdaFdcEvidenceOptions.ConnectionName).ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed record Captured(string Uri, string? ApiKeyHeader, string? Authorization);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Captured> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Captured(
                request.RequestUri!.AbsoluteUri,
                request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null,
                request.Headers.Authorization?.ToString()));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
