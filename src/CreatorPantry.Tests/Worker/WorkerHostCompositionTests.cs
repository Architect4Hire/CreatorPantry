using CreatorPantry.Domain.Managers.Caching;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Tests.Worker;

/// <summary>
/// That <c>CreatorPantry.Worker</c> can actually start.
/// </summary>
/// <remarks>
/// <para>
/// These exist because both ways this host can fail to start are silent. It serves no request and exposes no
/// health check, so a container it cannot build, or an option that fails its startup validation, makes the
/// process exit while the rest of the application stays healthy. The only visible symptom is that every AI
/// operation stays <c>Requested</c> for ever — which from a creator's side is indistinguishable from a slow
/// model, and from the dashboard is one resource reading "Finished" among a dozen reading "Running".
/// </para>
/// <para>
/// Both failures had actually happened: the host registers the recipe, ingredient, measurement and vocabulary
/// modules so an AI operation can load its recipe through a facade, and those facades take
/// <see cref="IApplicationCache"/>, which nothing here registered; and it calls <c>AddIdempotency</c>, whose
/// options are <c>ValidateOnStart</c>, while the AppHost supplied the fingerprint key to the API alone.
/// </para>
/// <para>
/// The configuration below is the contract with <c>CreatorPantry.AppHost</c>: every key here is one the
/// AppHost must supply to the <c>worker</c> resource. A key added here without being added there passes this
/// test and still exits at run time, so the two are changed together.
/// </para>
/// </remarks>
public class WorkerHostCompositionTests
{
    /// <summary>Any base64 value of at least 32 bytes; only its shape is under test.</summary>
    private const string FingerprintKey = "dGhpcy1pcy1hLXRlc3Qta2V5LW9mLWF0LWxlYXN0LTMyLWJ5dGVz";

    [Fact]
    public void Every_registered_service_can_be_constructed()
    {
        using var provider = BuildProvider();

        // The assertion is BuildProvider itself: ValidateOnBuild walks every descriptor and throws on the
        // first one whose dependencies cannot be satisfied, which is exactly what the running host did.
        Assert.NotNull(provider);
    }

    [Fact]
    public void Startup_validation_passes_with_the_configuration_the_apphost_supplies()
    {
        using var provider = BuildProvider();

        // What ValidateOnStart runs at host start, without starting the hosted services: the whole point is
        // to reach this verdict without a database, a cache or a model deployment.
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Startup_validation_fails_when_the_fingerprint_key_is_missing()
    {
        // The negative case, so the test above cannot pass by validating nothing. Dropping the key is the
        // exact state the worker was in while the AppHost gave it to the API alone.
        using var provider = BuildProvider(configuration => configuration.Remove(
            $"{IdempotencyOptions.SectionName}:{nameof(IdempotencyOptions.FingerprintKey)}"));

        var failure = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("FingerprintKey", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_queue_and_outbox_services_are_hosted()
    {
        using var provider = BuildProvider();

        // Nothing drains a queue unless something hosts the loop that drains it: the outbox dispatcher, the
        // AI operation worker, and the maintenance sweep that recovers its abandoned leases. By type rather
        // than by count, because the framework hosts services of its own here — the options startup
        // validator among them — and a count would be asserting on those too.
        var hosted = provider.GetServices<IHostedService>().ToArray();

        Assert.Single(hosted.OfType<OutboxDispatcherHostedService>());
        Assert.Single(hosted.OfType<AiOperationWorkerHostedService>());
        Assert.Single(hosted.OfType<AiOperationMaintenanceHostedService>());
    }

    [Fact]
    public void The_cache_falls_back_to_an_in_process_store_without_redis()
    {
        // No ConnectionStrings:cache below, which is the clean-clone case. The host must still compose --
        // an unconditional Redis registration would make this whole file depend on a running container.
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IApplicationCache>());
    }

    private static ServiceProvider BuildProvider(Action<Dictionary<string, string?>>? customize = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            // A parseable connection string, never opened: registration does not connect, and every test
            // here is about composition rather than data.
            [$"ConnectionStrings:{CreatorPantryDbContext.ConnectionName}"] =
                "Server=(local);Database=creatorpantry;Trusted_Connection=True;TrustServerCertificate=True",
            [$"{IdempotencyOptions.SectionName}:{nameof(IdempotencyOptions.FingerprintKey)}"] = FingerprintKey,
        };

        customize?.Invoke(configuration);

        // Development, because that is what makes AddCreatorPantryAi register its unconfigured clients rather
        // than demand a model deployment -- the state a developer's machine is in before Foundry is wired up.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(WorkerHostRegistration).Assembly.GetName().Name,
        });

        builder.Configuration.AddInMemoryCollection(configuration);

        Assert.True(builder.Environment.IsDevelopment());

        builder.AddWorkerServices();

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
