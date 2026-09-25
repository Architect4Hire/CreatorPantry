using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CreatorPantry.Domain.Managers.Prompts;

public static class PromptServiceCollectionExtensions
{
    /// <summary>
    /// Loads and validates this assembly's prompt templates, and registers them as
    /// <see cref="IPromptTemplateStore"/>.
    /// </summary>
    /// <remarks>
    /// The store is built here, during registration, rather than from a factory resolved on first use. That is
    /// deliberate: it makes a malformed or duplicated template stop the host at startup, where a deployment
    /// notices, instead of surfacing as an exception inside a creator's generation.
    /// </remarks>
    /// <exception cref="PromptTemplateException">A template is invalid, or two share an id and version.</exception>
    public static IServiceCollection AddPromptTemplates(this IServiceCollection services)
    {
        var store = EmbeddedPromptTemplateStore.Load(typeof(PromptServiceCollectionExtensions).Assembly);

        services.TryAddSingleton<IPromptTemplateStore>(store);

        return services;
    }
}
