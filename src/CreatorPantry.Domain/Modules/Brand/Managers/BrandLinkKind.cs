namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What a brand link points at. Deliberately provider-neutral: a social profile is a publishing-target
/// concern (publishing.md), not a brand-profile one.
/// </summary>
public enum BrandLinkKind
{
    /// <summary>The creator's own site.</summary>
    Website = 1,

    /// <summary>Any other page the creator wants on record as a reference for the brand.</summary>
    Reference = 2,
}
