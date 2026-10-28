using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.AiUsage.Facade;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Media.Facade;

/// <summary>
/// The picture qualities this host offers and what one picture of each costs, for anything outside Media that
/// needs to say so (PRICE-006, AP.4.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists beside <see cref="IGeneratedImageGenerationFacade.ListQualityClassesAsync"/>.</strong>
/// That one answers a creator in a workspace: it checks the Contributor role and says which classes the asking
/// account's plan allows. This one answers the host about itself — no workspace, no account, no plan — which is
/// what the public plan catalogue needs, and what lets this be a singleton over configuration rather than a
/// scoped graph with a database in it.
/// </para>
/// <para>
/// <strong>It publishes names, not types.</strong> A caller gets the class's canonical name from
/// <see cref="PictureQualityVocabulary"/>, so nothing outside Media names <c>GeneratedImageQualityClass</c>, and
/// nothing outside Media learns which model a class sends to — that is this deployment's business and no part
/// of a price list.
/// </para>
/// </remarks>
public interface IGeneratedImageQualityOfferFacade
{
    /// <summary>
    /// Every class this host offers and can price, cheapest first, with the one a request naming none is made
    /// at.
    /// </summary>
    GeneratedImageQualityOffersServiceModel Offered();
}

/// <inheritdoc cref="IGeneratedImageQualityOfferFacade"/>
internal sealed class GeneratedImageQualityOfferFacade(
    IOptions<GeneratedImageQualityClassOptions> options,
    IAiImagePriceFacade prices) : IGeneratedImageQualityOfferFacade
{
    public GeneratedImageQualityOffersServiceModel Offered()
    {
        var configured = options.Value;

        var offers = GeneratedImageQualityClassCatalogue.All(configured)
            .Select(settings => (
                Name: settings.QualityClass!.Value.ToString(),
                Price: prices.PerPicture(settings.ModelName, settings.Resolution, settings.Quality)))
            // A class with no rate is left off rather than offered at nothing: startup refuses such a host, so
            // this guards a table edited while it ran.
            .Where(offer => offer.Price is not null)
            .Select(offer => new GeneratedImageQualityOfferServiceModel(offer.Name, offer.Price!.Value))
            .ToList();

        // The default only when it is one of the classes above, so a caller never starts from a quality it has
        // no price for.
        var fallback = configured.DefaultClass?.ToString();

        return new GeneratedImageQualityOffersServiceModel(
            offers.Any(offer => offer.QualityClass == fallback) ? fallback : null, offers);
    }
}
