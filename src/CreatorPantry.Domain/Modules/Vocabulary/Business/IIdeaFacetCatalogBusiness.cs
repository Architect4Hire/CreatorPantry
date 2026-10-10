using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Vocabulary.Managers;

namespace CreatorPantry.Domain.Modules.Vocabulary.Business;

public interface IIdeaFacetCatalogBusiness
{
    /// <summary>Every photography style, active and retired, in display order.</summary>
    IReadOnlyList<IdeaFacetEntryServiceModel> ListPhotographyStyles();

    /// <summary>Every occasion, active and retired, in display order.</summary>
    IReadOnlyList<IdeaFacetEntryServiceModel> ListOccasions();
}

/// <remarks>
/// No DataLayer beneath, for the reason <see cref="ContentChannelBusiness"/> has none: both catalogues are
/// configuration in code, so there is no persistence to compose.
/// </remarks>
internal sealed class IdeaFacetCatalogBusiness(
    IPhotographyStyleCatalog photographyStyles, IOccasionCatalog occasions) : IIdeaFacetCatalogBusiness
{
    public IReadOnlyList<IdeaFacetEntryServiceModel> ListPhotographyStyles() =>
        [.. photographyStyles.All.Select(style => new IdeaFacetEntryServiceModel(style.Key, style.DisplayName, style.IsActive))];

    public IReadOnlyList<IdeaFacetEntryServiceModel> ListOccasions() =>
        [.. occasions.All.Select(occasion => new IdeaFacetEntryServiceModel(occasion.Key, occasion.DisplayName, occasion.IsActive))];
}
