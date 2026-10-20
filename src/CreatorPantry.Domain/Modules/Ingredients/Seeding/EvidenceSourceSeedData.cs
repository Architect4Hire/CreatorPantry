using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Ingredients.Seeding;

/// <summary>
/// The approved evidence sources as <see cref="ReferenceSource"/> rows (14.1): the catalogue entries that facts
/// drawn from them will cite, with the licence each was approved under.
/// </summary>
/// <remarks>
/// <para>
/// Written from <see cref="EvidenceSourceCatalog"/> rather than restated, so the row a fact cites and the source
/// its adapter reads cannot disagree about code, citation or licence.
/// </para>
/// <para>
/// <strong>A citation, not data.</strong> Seeding a source imports nothing from it. That is why these rows belong
/// to the catalogue tier that runs in every environment, unlike the development sample set.
/// </para>
/// </remarks>
internal static class EvidenceSourceSeedData
{
    private const string SourceTable = "ReferenceSources";

    public static IReadOnlyList<ReferenceSource> Sources() =>
    [
        .. EvidenceSourceCatalog.All.Select(source => new ReferenceSource
        {
            Id = SeedId.For(SourceTable, source.Code),
            Code = source.Code,
            Name = source.Name,
            Kind = source.Kind,
            Url = source.Url,
            Citation = source.Citation,
            LicenseCode = source.LicenseCode,
            LicenseUrl = source.LicenseUrl,
            IsActive = true,
        }),
    ];
}
