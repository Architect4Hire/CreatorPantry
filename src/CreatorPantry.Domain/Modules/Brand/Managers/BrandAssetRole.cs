namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// What a linked asset is to the brand. Logos only: reference imagery, mood boards and colour direction are
/// visual direction, which Phase 11A's <c>BrandStyleGuideVersion</c> owns.
/// </summary>
public enum BrandAssetRole
{
    /// <summary>The single primary logo. At most one per profile, enforced by a filtered unique index.</summary>
    PrimaryLogo = 1,

    /// <summary>A variant of the logo: a mark, a monochrome version, a horizontal lockup.</summary>
    AlternateLogo = 2,
}
