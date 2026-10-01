using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Domain.Modules.Brand.Data.Entities;

/// <summary>
/// One prose section of a <see cref="BrandStyleGuideVersion"/>, in the creator's words. Interior to the
/// version and write-once with it.
/// </summary>
/// <remarks>
/// A version holds at most one section per <see cref="SectionKey"/>, except
/// <see cref="BrandStyleGuideSectionKey.ChannelVariant"/>, which it holds once per channel. A section the
/// creator left blank has no row.
/// </remarks>
public class BrandStyleGuideSection : IWorkspaceOwned, IImmutableRecord
{
    public Guid Id { get; set; }

    public Guid WorkspaceId { get; set; }

    public Guid BrandStyleGuideVersionId { get; set; }

    public BrandStyleGuideSectionKey SectionKey { get; set; }

    /// <summary>
    /// The channel a variant is for; empty on every other section. Empty rather than null so that one unique
    /// index states the cardinality on every database. Opaque, as on <see cref="BrandChannelDefault"/>.
    /// </summary>
    public string ChannelKey { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;
}
