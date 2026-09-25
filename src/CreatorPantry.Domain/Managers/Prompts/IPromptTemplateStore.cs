namespace CreatorPantry.Domain.Managers.Prompts;

/// <summary>Every validated prompt template the host loaded, looked up by identity.</summary>
/// <remarks>
/// The store holds more than one version of an id at once. New work takes <see cref="Get(string)"/> and gets
/// the highest; anything reproducing or explaining a stored generation takes
/// <see cref="Get(string, PromptTemplateVersion)"/> and gets the exact body that produced it. Without that,
/// the template version recorded against a proposal would name a prompt nothing could produce again as soon
/// as the template was bumped.
/// </remarks>
public interface IPromptTemplateStore
{
    /// <summary>Every template, every version.</summary>
    IReadOnlyCollection<PromptTemplate> All { get; }

    /// <summary>The highest version of <paramref name="id"/>.</summary>
    /// <exception cref="PromptTemplateException">No template has that id.</exception>
    PromptTemplate Get(string id);

    /// <summary>One exact version of <paramref name="id"/>.</summary>
    /// <exception cref="PromptTemplateException">That id and version are not loaded.</exception>
    PromptTemplate Get(string id, PromptTemplateVersion version);
}
