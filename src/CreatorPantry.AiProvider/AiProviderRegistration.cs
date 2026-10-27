using System.Data.Common;
using System.Net.Http.Headers;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.Ai.Gateways;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.AiProvider;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

/// <summary>
/// Wires <see cref="Microsoft.Extensions.AI.IChatClient"/>,
/// <see cref="Microsoft.Extensions.AI.IEmbeddingGenerator{TInput,TEmbedding}"/> and
/// <see cref="Microsoft.Extensions.AI.IImageGenerator"/> for a host, from whichever model deployments the
/// AppHost has supplied.
/// </summary>
public static class AiProviderRegistration
{
    /// <summary>
    /// Registers the model abstractions: the configured Microsoft Foundry deployment where the AppHost has
    /// supplied one, the unconfigured fallback in development where it has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each deployment is decided on its own, because they are independent resources. Configuring chat
    /// without embeddings is a real intermediate state — chat models are cheap to point at Foundry Local,
    /// embeddings are not useful until there is a vector column to fill — and one missing deployment must
    /// not take the other's client away.
    /// </para>
    /// <para>
    /// Outside development a missing deployment is a startup failure rather than a fallback. The fallback
    /// exists so hosts without a model account still start; a deployment that silently went missing in a
    /// deployed environment is a misconfiguration, and discovering it when a creator's generation throws is
    /// strictly worse than discovering it when the host refuses to start. This mirrors
    /// <c>AddDevelopmentAccountMessageSink</c>, where the development-only sink is also the only case in
    /// which the real provider may be absent.
    /// </para>
    /// <para>
    /// No credential for chat or embeddings is read here. In run mode the Foundry Local connection string
    /// carries its own key; in Azure mode the connection string carries no key at all and the client falls
    /// back to the ambient Azure credential. Neither path puts a secret in configuration this project owns.
    /// The images key is the exception: Venice has no ambient credential, so it is read out of the
    /// connection string the AppHost composed and set on that one HTTP client's headers.
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddCreatorPantryAi(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (IsConfigured(builder, AiModelConnections.Chat))
        {
            // Registers a non-keyed IChatClient over a ChatCompletionsClient, reading the endpoint,
            // credential and deployment name out of the connection string. Also registers the
            // Microsoft.Extensions.AI trace sources and meters, so AI calls correlate with the rest of the
            // request without a ServiceDefaults change.
            builder.AddAzureChatCompletionsClient(AiModelConnections.Chat).AddChatClient()
                .Use(inner => new MaxCompletionTokensChatClient(inner));
            RequireChatRate(builder);
            RequireEstimatesCoverCeilings(builder);
        }
        else
        {
            RequireDevelopment(builder, AiModelConnections.Chat);
            builder.Services.AddUnconfiguredChatClient();
        }

        if (IsConfigured(builder, AiModelConnections.Embeddings))
        {
            builder.AddAzureEmbeddingsClient(AiModelConnections.Embeddings).AddEmbeddingGenerator();
        }
        else
        {
            RequireDevelopment(builder, AiModelConnections.Embeddings);
            builder.Services.AddUnconfiguredEmbeddingGenerator();
        }

        // Images are decided on their own too, but a missing deployment is not a startup failure anywhere:
        // without chat or embeddings the product does not work, without images one feature reports
        // provider-not-configured, and refusing to start over that would take the other two down with it.
        // The unconfigured generator is what makes the Media module's gateway report NotConfigured, which
        // its job settles as a terminal failure with that category.
        if (IsConfigured(builder, AiModelConnections.Images))
        {
            var images = ImagesConnection(builder);
            var profiles = ImageModelProfiles(builder, images.Model);
            RequireImageRates(builder, profiles);
            RequireQualityClasses(builder, profiles);

            // Venice.ai, over plain HTTP (B-15 as amended for Venice): Azure.AI.Inference, which serves chat
            // and embeddings, has no image route, and Venice's own needs no SDK. A named client, so the key
            // is on this one client's headers and nowhere a different HttpClient could pick it up.
            //
            // The resilience defaults are removed, not tuned. The standard handler gives an attempt ten
            // seconds and then retries it, and an image takes longer than that: every generation would be
            // abandoned, repeated and charged for three times over. The gateway above already bounds the
            // call and the operation above that already decides whether to try again.
            // RemoveAllResilienceHandlers is marked experimental (EXTEXP0001); it is the supported opt-out
            // from what ServiceDefaults applies to every HttpClient.
#pragma warning disable EXTEXP0001
            builder.Services
                .AddHttpClient(AiModelConnections.Images, client =>
                {
                    client.BaseAddress = images.Endpoint;
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", images.Key);

                    // Longer than MediaPolicy.GenerationProviderTimeout on purpose, so the gateway's bound is
                    // the one that fires and this is only the backstop for a caller that brought none.
                    client.Timeout = TimeSpan.FromMinutes(5);
                })
                .RemoveAllResilienceHandlers()
                .AddTypedClient<IImageGenerator>((client, services) =>
                    new VeniceImageGenerator(
                            client, images.Model, profiles, services.GetRequiredService<ILogger<VeniceImageGenerator>>())
                        .AsBuilder()
                        .UseOpenTelemetry(services.GetRequiredService<ILoggerFactory>())
                        .Build(services));
#pragma warning restore EXTEXP0001
        }
        else
        {
            builder.Services.AddUnconfiguredImageGenerator();
        }

        return builder;
    }

    private static bool IsConfigured(IHostApplicationBuilder builder, string connectionName) =>
        !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(connectionName));

    /// <summary>
    /// The endpoint, key and model out of the images connection string.
    /// </summary>
    /// <remarks>
    /// All three or a refusal to start. A string missing one is a misconfiguration to stop on: the
    /// alternative is a host that starts and then fails every generation with a 401 or a 400 that reads like
    /// a provider outage. The message names the part that is missing and never quotes the string, which
    /// holds the key.
    /// </remarks>
    private static (Uri Endpoint, string Key, string Model) ImagesConnection(IHostApplicationBuilder builder)
    {
        var connection = new DbConnectionStringBuilder
        {
            ConnectionString = builder.Configuration.GetConnectionString(AiModelConnections.Images),
        };

        // A trailing slash, so the request path is appended to the endpoint's own path rather than replacing
        // its last segment — "https://api.venice.ai/api/v1" would otherwise lose the "v1".
        return Uri.TryCreate(Part("Endpoint").TrimEnd('/') + "/", UriKind.Absolute, out var endpoint)
            ? (endpoint, Part("Key"), Part("Model"))
            : throw new InvalidOperationException(
                $"ConnectionStrings:{AiModelConnections.Images} has an endpoint that is not an absolute URL.");

        string Part(string name) =>
            connection.TryGetValue(name, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : throw new InvalidOperationException(
                    $"ConnectionStrings:{AiModelConnections.Images} names no {name.ToLowerInvariant()}. It "
                    + "takes the form 'Endpoint=https://api.venice.ai/api/v1;Key=<key>;Model=<model id>'.");
    }

    /// <summary>
    /// Outside development, refuses to start when the chat model has no credit rate or no price (AP.2.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unrated model does not fail a call: it settles every call at the flat per-call estimate and reports
    /// its cost as null, which in a deployed environment means creators are charged a guess and nobody can see
    /// what the provider bill is. That is found here, not on an invoice.
    /// </para>
    /// <para>
    /// The model is the one the deployment declares — <c>Ai:Gateway:ModelName</c> when it is set, otherwise the
    /// chat deployment's own name, which Azure deployments are usually given — because a host knows no more
    /// than that before its first call. It is looked up the way settlement looks up a reported id
    /// (<see cref="AiModelRateKeys"/>), so a dated deployment name finds its model's rate. Embeddings are not
    /// checked: no embedding call is metered against the allowance yet, so a rule for them would guard
    /// nothing. Development is exempt so a new model can be tried before it is priced.
    /// </para>
    /// </remarks>
    private static void RequireChatRate(IHostApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment())
        {
            return;
        }

        var model = ChatModel(builder);

        if (model.Length == 0)
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{AiModelConnections.Chat} names no deployment (DeploymentId, Deployment or Model) "
                + "and Ai:Gateway:ModelName is not set, so the chat model's credit rate cannot be checked. Set "
                + "Ai:Gateway:ModelName to the model the chat deployment runs.");
        }

        var missing = new[]
            {
                $"{AiQuotaOptions.SectionName}:{nameof(AiQuotaOptions.ModelRates)}",
                $"{AiCostOptions.SectionName}:{nameof(AiCostOptions.Models)}",
            }
            .Where(section => !Rates(builder, section).Overlaps(AiModelRateKeys.Candidates(model)))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The chat model '{model}' has no entry under {string.Join(" or ", missing)}. Add one at the pinned "
            + "credit unit (credits = list $ x 1,250), or set Ai:Gateway:ModelName to the model the chat "
            + "deployment runs if its deployment name is not the model's.");
    }

    /// <summary>
    /// Refuses to start when a task's output ceiling could cost more than the estimate its call is admitted
    /// under (AP.2.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Admission holds a task's per-call estimate; settlement charges what the call used. A ceiling worth more
    /// credits than the estimate means a single call can settle past what was held for it, so the hold stops
    /// being a bound. The worst case is the ceiling's tokens at the chat model's output rate — the price of the
    /// most the model is permitted to write, reasoning included.
    /// </para>
    /// <para>
    /// It is an output-cost bound only. Input is not in it — the ceiling does not bound input, and a prompt's
    /// size depends on the creator's own material — and a corrective re-ask re-sends the prompt. The hold is
    /// the per-call estimate times <c>Ai:Quota:EstimatedCallsPerRun</c>, which therefore has to cover every
    /// call the gateway may make for one run (<c>Ai:Gateway:MaxSchemaCorrections</c> + 1); that is checked here
    /// too, because the two are configured in different modules and nothing else holds them together. Every
    /// environment runs it, development included, so a mismatch is met on the machine that made it; a chat model with no rate is skipped, because there is nothing to price the ceiling at, and
    /// outside development <see cref="RequireChatRate"/> has already refused that.
    /// </para>
    /// </remarks>
    private static void RequireEstimatesCoverCeilings(IHostApplicationBuilder builder)
    {
        var model = ChatModel(builder);
        var quota = builder.Configuration.GetSection(AiQuotaOptions.SectionName).Get<AiQuotaOptions>()
            ?? new AiQuotaOptions();

        if (!AiModelRateKeys.TryFind(quota.ModelRates, model, out var rate))
        {
            return;
        }

        var gateway = builder.Configuration.GetSection(AiGatewayOptions.SectionName).Get<AiGatewayOptions>()
            ?? new AiGatewayOptions();

        if (quota.EstimatedCallsPerRun < gateway.MaxSchemaCorrections + 1)
        {
            throw new InvalidOperationException(
                $"{AiQuotaOptions.SectionName}:{nameof(AiQuotaOptions.EstimatedCallsPerRun)} is "
                + $"{quota.EstimatedCallsPerRun}, but a run may call the model "
                + $"{gateway.MaxSchemaCorrections + 1} times ({AiGatewayOptions.SectionName}:"
                + $"{nameof(AiGatewayOptions.MaxSchemaCorrections)} + 1), so its hold would not cover a corrected run.");
        }

        var over = Enum.GetValues<AiTaskType>()
            .Where(task => task is not AiTaskType.Unspecified)
            .Select(task =>
            {
                var ceiling = gateway.MaxOutputTokensFor(task);
                var worst = ceiling / 1_000_000m * rate.CreditsPerMillionOutputTokens;
                var estimate = quota.TaskEstimates.TryGetValue(task, out var configured)
                    ? configured
                    : quota.DefaultTaskEstimate;

                return (Task: task, Ceiling: ceiling, Worst: worst, Estimate: estimate);
            })
            .Where(check => check.Worst > check.Estimate)
            .Select(check =>
                $"{check.Task}: {check.Ceiling:N0} output tokens is {check.Worst:0.##} credits at '{model}', over its "
                + $"per-call estimate of {check.Estimate:0.##}")
            .ToList();

        if (over.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "A task's output ceiling can cost more than the estimate its call is admitted under — "
            + string.Join("; ", over)
            + $". Raise {AiQuotaOptions.SectionName}:{nameof(AiQuotaOptions.TaskEstimates)}:<task>, or lower "
            + $"{AiGatewayOptions.SectionName}:{nameof(AiGatewayOptions.MaxOutputTokensByTask)}:<task>.");
    }

    /// <summary>
    /// The model the chat deployment runs, as declared: <c>Ai:Gateway:ModelName</c> when set, otherwise the
    /// deployment's own name. Empty when neither is known.
    /// </summary>
    private static string ChatModel(IHostApplicationBuilder builder)
    {
        var declared = builder.Configuration[$"{AiGatewayOptions.SectionName}:{nameof(AiGatewayOptions.ModelName)}"];

        return string.IsNullOrWhiteSpace(declared) || declared == new AiGatewayOptions().ModelName
            ? ChatDeployment(builder)
            : declared;
    }

    private static HashSet<string> Rates(IHostApplicationBuilder builder, string section) =>
        builder.Configuration.GetSection(section).GetChildren()
            .Select(child => child.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The deployment named in the chat connection string; empty when it names none.</summary>
    /// <remarks>Never quotes the string, which may hold a key.</remarks>
    private static string ChatDeployment(IHostApplicationBuilder builder)
    {
        var connection = new DbConnectionStringBuilder
        {
            ConnectionString = builder.Configuration.GetConnectionString(AiModelConnections.Chat),
        };

        foreach (var part in new[] { "DeploymentId", "Deployment", "Model" })
        {
            if (connection.TryGetValue(part, out var value) && value is string name && !string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Every configured Venice model's sizing profile, checked, and refused when the configured model has none
    /// (AP.2.3).
    /// </summary>
    /// <remarks>
    /// The same rule as a half-made images connection string: a host with no image provider starts, but one
    /// pointed at a model it cannot size correctly does not. Guessing a model's dialect would send a field it
    /// rejects — a 400 on every picture — or leave its dearest default quality in force. Every environment,
    /// because the model is configuration a developer changes too.
    /// </remarks>
    private static IReadOnlyDictionary<string, VeniceModelProfile> ImageModelProfiles(
        IHostApplicationBuilder builder, string configuredModel)
    {
        var profiles = new Dictionary<string, VeniceModelProfile>(StringComparer.OrdinalIgnoreCase);
        builder.Configuration.GetSection(VeniceModelProfile.SectionName).Bind(profiles);

        var problems = profiles
            .SelectMany(profile => profile.Value.Problems().Select(problem => $"{profile.Key}: {problem}"))
            .ToList();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{VeniceModelProfile.SectionName} has a profile that cannot be used — {string.Join("; ", problems)}.");
        }

        if (!profiles.ContainsKey(configuredModel))
        {
            throw new InvalidOperationException(
                $"The configured image model '{configuredModel}' has no sizing profile under "
                + $"{VeniceModelProfile.SectionName}. Add one from the model's constraints in Venice's "
                + "GET /models?type=image, so its size and quality are stated rather than left to its defaults.");
        }

        return profiles;
    }

    /// <summary>
    /// Refuses to start when a picture this deployment can request has no credit rate or no price (AP.2.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A picture with no rate is refused at admission rather than charged nothing (PRICE-001,
    /// <c>ai-pricing.md</c> B-49), so the cost of the gap is a creator told their image cannot be made, for a
    /// reason nothing in the product explains. That is worth finding here.
    /// </para>
    /// <para>
    /// <strong>The profiles are what it checks against, not the configured model.</strong> A profile is this
    /// deployment's statement that it may send to that model — <c>ImageGenerationOptions.ModelId</c> can name
    /// any profiled one, and AP.3.1's quality classes will — so a profile added without its rate is the mistake
    /// to stop on. Every environment, like the profile check above and for the same reason: the model table is
    /// configuration a developer changes too, and a refusal met on the machine that made it needs no
    /// explaining.
    /// </para>
    /// <para>
    /// Presence only. That each rate is its price × 1,250 is <c>ImageRateTableTests</c>', on the same division
    /// as the text tables: the margin is a figure the owner may tune in configuration, and a host that would
    /// not start over it could not be tuned at all. Rates for models with no profile are left alone — a
    /// deployment may carry a table wider than the models it points at, as <c>Ai:Cost:Models</c> already does —
    /// and a mistyped key is caught anyway, because the row it was meant to be goes missing.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Refuses to start when a quality class this host offers could not be sent or could not be priced (AP.3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Per class, and in every environment</strong>, for <see cref="RequireImageRates"/>' reasons. A
    /// class is checked the way a request at it would be: its model has a sizing profile, the profile settles its
    /// quality and resolution without refusing them, and the exact rate key it would be charged at is in both
    /// tables. That last part restates what the profile check already implies, deliberately — a class's price
    /// is that row (B-55), and the message should name the class a creator would have chosen.
    /// </para>
    /// <para>
    /// A host that configures no classes is not refused: it offers no choice, and every picture is the
    /// configured model's own default, as before AP.3.1.
    /// </para>
    /// </remarks>
    private static void RequireQualityClasses(
        IHostApplicationBuilder builder, IReadOnlyDictionary<string, VeniceModelProfile> profiles)
    {
        var classes = new GeneratedImageQualityClassOptions();
        builder.Configuration.GetSection(GeneratedImageQualityClassOptions.SectionName).Bind(classes);

        var quota = builder.Configuration.GetSection(AiQuotaOptions.SectionName).Get<AiQuotaOptions>()
            ?? new AiQuotaOptions();
        var cost = builder.Configuration.GetSection(AiCostOptions.SectionName).Get<AiCostOptions>()
            ?? new AiCostOptions();

        var faults = new List<string>();

        foreach (var settings in GeneratedImageQualityClassCatalogue.All(classes))
        {
            var name = settings.QualityClass;

            if (!profiles.TryGetValue(settings.ModelName, out var profile))
            {
                faults.Add($"{name} sends to '{settings.ModelName}', which has no sizing profile under {VeniceModelProfile.SectionName}");
                continue;
            }

            var options = new ImageGenerationOptions { AdditionalProperties = [] };

            if (settings.Quality is { } quality)
            {
                options.AdditionalProperties[ImageGenerationSettings.Quality] = quality;
            }

            if (settings.Resolution is { } resolution)
            {
                options.AdditionalProperties[ImageGenerationSettings.Resolution] = resolution;
            }

            if (profile.Settle(options).Refusal is { } refusal)
            {
                faults.Add($"{name} cannot be sent to '{settings.ModelName}': {refusal}");
                continue;
            }

            var key = AiImageRateKeys.Key(settings.ModelName, settings.Resolution, settings.Quality);

            if (AiImageRateKeys.Figure(quota.ImageRates, settings.ModelName, settings.Resolution, settings.Quality) is null)
            {
                faults.Add($"{name} would be charged at {key}, which has no rate above nought in "
                    + $"{AiQuotaOptions.SectionName}:{nameof(AiQuotaOptions.ImageRates)}");
            }

            if (AiImageRateKeys.Figure(cost.Images, settings.ModelName, settings.Resolution, settings.Quality) is null)
            {
                faults.Add($"{name} would be charged at {key}, which has no price above nought in "
                    + $"{AiCostOptions.SectionName}:{nameof(AiCostOptions.Images)}");
            }
        }

        if (faults.Count > 0)
        {
            throw new InvalidOperationException(
                $"A quality class under {GeneratedImageQualityClassOptions.SectionName}:Classes cannot be offered — "
                + string.Join("; ", faults)
                + ". A class costs exactly the rate of what it sends (B-55), so it must send something this host can "
                + "size and price.");
        }
    }

    private static void RequireImageRates(
        IHostApplicationBuilder builder, IReadOnlyDictionary<string, VeniceModelProfile> profiles)
    {
        var quota = builder.Configuration.GetSection(AiQuotaOptions.SectionName).Get<AiQuotaOptions>()
            ?? new AiQuotaOptions();
        var cost = builder.Configuration.GetSection(AiCostOptions.SectionName).Get<AiCostOptions>()
            ?? new AiCostOptions();

        // A model whose name holds the key separator cannot be rated at all: every key it spells runs into the
        // next part and would read another row's rate. Said on its own, because "missing" would be misleading.
        var unkeyable = profiles.Keys.Where(model => model.Contains(AiImageRateKeys.Separator)).ToList();

        if (unkeyable.Count > 0)
        {
            throw new InvalidOperationException(
                $"A sizing profile names a model that cannot carry an image rate — {string.Join(", ", unkeyable)} "
                + $"holds '{AiImageRateKeys.Separator}', which separates a rate key's parts, so its rate would be "
                + "read as another tier and quality's. Configure the model under a name without it.");
        }

        var faults = profiles
            .SelectMany(profile => profile.Value.RequestableSizes()
                .Select(size => (Model: profile.Key, size.Resolution, size.Quality)))
            .Select(request => (
                Key: AiImageRateKeys.Key(request.Model, request.Resolution, request.Quality),
                Faults: new[]
                    {
                        Fault($"{AiQuotaOptions.SectionName}:{nameof(AiQuotaOptions.ImageRates)}",
                            quota.ImageRates, request),
                        Fault($"{AiCostOptions.SectionName}:{nameof(AiCostOptions.Images)}", cost.Images, request),
                    }
                    .Where(fault => fault is not null)
                    .Select(fault => fault!.Value)
                    .ToList()))
            .Where(check => check.Faults.Count > 0)
            .Select(check => $"{check.Key} is {Joined(check.Faults)}")
            .ToList();

        if (faults.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "A picture this deployment can request has no credit rate or no price — "
            + string.Join("; ", faults)
            + ". Add each from the model's own model_spec.pricing in Venice's GET /models?type=image, the rate at "
            + "the pinned credit unit (credits = list $ per picture x 1,250) and the price at list. An image with "
            + "no rate is refused at admission, never made for nothing.");

        // A row of nought is as wrong as an absent one and worse to find later: it would reserve nothing and
        // charge nothing for a picture the provider bills for. A negative row would pay a creator to generate.
        static (string Wrong, string Section)? Fault(
            string section,
            Dictionary<string, decimal> table,
            (string Model, string? Resolution, string? Quality) request) =>
            AiImageRateKeys.TryFind(table, request.Model, request.Resolution, request.Quality, out var figure)
                ? figure > 0m ? null : ("not above nought in", section)
                : ("missing from", section);

        // "missing from A and B" where both tables are short the same way, "missing from A and not above nought
        // in B" where they are not.
        static string Joined(List<(string Wrong, string Section)> faults) =>
            faults.Count == 2 && faults[0].Wrong == faults[1].Wrong
                ? $"{faults[0].Wrong} {faults[0].Section} and {faults[1].Section}"
                : string.Join(" and ", faults.Select(fault => $"{fault.Wrong} {fault.Section}"));
    }

    private static void RequireDevelopment(IHostApplicationBuilder builder, string connectionName)
    {
        if (builder.Environment.IsDevelopment())
        {
            return;
        }

        throw new InvalidOperationException(
            $"No model deployment is configured for '{connectionName}'. Supply "
            + $"ConnectionStrings:{connectionName}, or run in Development, where the unconfigured client is "
            + "registered so a host without a model account can still start.");
    }
}
