using CreatorPantry.Domain.Modules.Vocabulary.Business;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;

namespace CreatorPantry.Domain.Modules.Vocabulary.Facade;

/// <summary>
/// The application boundary for the two idea facets that are catalogues in code rather than vocabulary
/// tables: photography styles and occasions.
/// </summary>
public interface IIdeaFacetCatalogFacade
{
    /// <summary>Every photography style, active and retired, in display order. Global: no workspace is involved.</summary>
    IReadOnlyList<IdeaFacetEntryServiceModel> ListPhotographyStyles();

    /// <summary>Every occasion, active and retired, in display order. Global: no workspace is involved.</summary>
    IReadOnlyList<IdeaFacetEntryServiceModel> ListOccasions();
}

internal sealed class IdeaFacetCatalogFacade(IIdeaFacetCatalogBusiness business) : IIdeaFacetCatalogFacade
{
    public IReadOnlyList<IdeaFacetEntryServiceModel> ListPhotographyStyles() => business.ListPhotographyStyles();

    public IReadOnlyList<IdeaFacetEntryServiceModel> ListOccasions() => business.ListOccasions();
}
