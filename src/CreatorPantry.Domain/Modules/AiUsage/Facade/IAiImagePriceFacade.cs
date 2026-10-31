using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.AiUsage.Facade;

/// <summary>
/// What one picture of a given model, tier and quality is charged, in credits (PRICE-004, B-55).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The same figure <see cref="IAiQuotaAdmissionFacade"/> holds, through a seam light enough to publish
/// from.</strong> <c>ImageCredits</c> on that facade answers this already, but its graph reaches a repository
/// and a <c>DbContext</c> to resolve, which is more than a price list should need — the public plans route is
/// anonymous, cacheable, and touches no account. This reads <c>Ai:Quota:ImageRates</c> and nothing else, so it
/// is a singleton and resolvable from the root provider.
/// </para>
/// <para>
/// <strong>One rate source.</strong> Both go through <see cref="AiImageQuotaConverter"/> and round the same
/// way, so a figure published for a class is the figure a hold for it takes.
/// <c>PictureClassRateTests</c> holds the two equal against a running host.
/// </para>
/// </remarks>
public interface IAiImagePriceFacade
{
    /// <summary>
    /// Credits per picture, or null when this deployment has no rate for exactly that model, tier and quality.
    /// </summary>
    /// <remarks>
    /// Null means refused, not free, which is the reading this module keeps throughout: a row of nought reads
    /// as no rate too. Startup refuses a host that can request a picture it cannot price, so a null here is a
    /// combination no profile offers, or a table edited while the host ran.
    /// </remarks>
    decimal? PerPicture(string? modelName, string? resolution, string? quality);
}

/// <inheritdoc cref="IAiImagePriceFacade"/>
internal sealed class AiImagePriceFacade(IOptions<AiQuotaOptions> options) : IAiImagePriceFacade
{
    public decimal? PerPicture(string? modelName, string? resolution, string? quality) =>
        AiImageQuotaConverter.Charge(AiQuotaUnit.Credits, modelName, resolution, quality, options.Value)
            is > 0m and var credits
            ? AiQuotaUnitConverter.Round(credits)
            : null;
}
