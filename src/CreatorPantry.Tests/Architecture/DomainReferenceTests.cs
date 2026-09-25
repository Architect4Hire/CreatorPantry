using System.Reflection;

namespace CreatorPantry.Tests.Architecture;

public class DomainReferenceTests
{
    private static readonly string[] HostAssemblies =
    [
        "CreatorPantry.AppHost",
        "CreatorPantry.ServiceDefaults",
        "CreatorPantry.Gateway",
        "CreatorPantry.Web",
        "CreatorPantry.ApiService",
        "CreatorPantry.Worker",
        "CreatorPantry.MigrationService",
        "CreatorPantry.AiProvider",
    ];

    /// <summary>
    /// Assembly-name prefixes belonging to a model provider's SDK or an Aspire client integration over one.
    /// </summary>
    /// <remarks>
    /// <c>Microsoft.Extensions.AI.Abstractions</c> is deliberately absent: <c>IChatClient</c> and
    /// <c>IEmbeddingGenerator</c> are the provider-neutral abstractions the domain is required to depend on.
    /// What may not reach it is anything that names a provider — <c>Azure.AI.Inference</c> today, and any
    /// OpenAI, Semantic Kernel connector or bridge package a later phase might add. Those belong in
    /// CreatorPantry.AiProvider, which is the one assembly allowed to know which provider is in use.
    /// </remarks>
    private static readonly string[] ProviderSdkPrefixes =
    [
        "Azure.AI.",
        "Aspire.Azure.AI.",
        "Aspire.OpenAI",
        "OpenAI",
        "Microsoft.Extensions.AI.OpenAI",
        "Microsoft.Extensions.AI.AzureAIInference",
        "Microsoft.SemanticKernel.Connectors.",
    ];

    [Fact]
    public void Domain_does_not_reference_any_host_project()
    {
        var domain = Assembly.Load("CreatorPantry.Domain");

        var hostReferences = domain.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => HostAssemblies.Contains(name))
            .ToList();

        Assert.Empty(hostReferences);
    }

    [Fact]
    public void Domain_does_not_reference_a_model_provider_sdk()
    {
        var domain = Assembly.Load("CreatorPantry.Domain");

        var providerReferences = domain.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null
                && ProviderSdkPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(providerReferences);
    }
}
