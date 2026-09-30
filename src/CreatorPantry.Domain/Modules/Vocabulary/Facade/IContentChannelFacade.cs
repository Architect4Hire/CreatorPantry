using CreatorPantry.Domain.Modules.Vocabulary.Business;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;

namespace CreatorPantry.Domain.Modules.Vocabulary.Facade;

/// <summary>The application boundary for the platform's content channels.</summary>
public interface IContentChannelFacade
{
    /// <summary>Every content channel, active and retired, in display order. Global: no workspace is involved.</summary>
    IReadOnlyList<ContentChannelServiceModel> List();
}

internal sealed class ContentChannelFacade(IContentChannelBusiness business) : IContentChannelFacade
{
    public IReadOnlyList<ContentChannelServiceModel> List() => business.List();
}
