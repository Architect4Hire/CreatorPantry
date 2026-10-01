namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What a brand source document is, as the creator classifies it. Not its file format: that is the version's
/// media type, which the upload seam establishes from the bytes.
/// </summary>
public enum BrandSourceDocumentType
{
    /// <summary>An existing written guide to how the brand writes or looks.</summary>
    StyleGuide = 1,

    /// <summary>A piece of the creator's own writing offered as an example.</summary>
    WritingSample = 2,

    PublishedPost = 3,

    Newsletter = 4,

    SocialSample = 5,

    /// <summary>Imagery or layout offered as a reference for how the brand looks.</summary>
    VisualReference = 6,

    Other = 7,
}
