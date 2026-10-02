namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// A library document that could serve as a visual reference example — what another module needs to offer it, and
/// nothing about the file.
/// </summary>
/// <param name="VersionNumber">The current version, which is the one a generation would read.</param>
/// <param name="ExtractionState">Whether text has been read from that version. A reference with none is never used.</param>
public sealed record BrandSourceVisualReferenceServiceModel(
    Guid DocumentId, string Title, int VersionNumber, BrandSourceExtractionState ExtractionState);

/// <param name="Truncated">True when the library held more references than are listed.</param>
public sealed record BrandSourceVisualReferenceListServiceModel(
    IReadOnlyList<BrandSourceVisualReferenceServiceModel> Items, bool Truncated);
