namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The workspace's brand profile as the application publishes it.
/// </summary>
/// <param name="Id">The profile's identifier.</param>
/// <param name="BrandName">The brand's name, as the creator wrote it.</param>
/// <param name="ShortDescription">A sentence or two about the brand, or null.</param>
/// <param name="DefaultAudience">Who work is written for unless a project says otherwise, or null.</param>
/// <param name="Locale">A BCP-47 language tag, or null.</param>
/// <param name="TimeZoneId">The workspace's IANA scheduling zone, or null.</param>
/// <param name="ChannelDefaults">Default channels, in the creator's order.</param>
/// <param name="Links">Website and reference links, in the creator's order.</param>
/// <param name="Assets">Linked logos, in the creator's order. Ids only: bytes and URLs belong to media.</param>
/// <param name="Revision">Edit counter, starting at 1.</param>
/// <param name="CreatedAt">When the profile was created (UTC).</param>
/// <param name="UpdatedAt">When it last changed (UTC).</param>
/// <param name="ConcurrencyToken">Opaque token identifying this state, for a conditional write.</param>
/// <remarks>
/// Carries no workspace or membership identifiers and no voice, tone or visual direction; the latter belong
/// to the brand style guide (DEC-010).
/// </remarks>
public sealed record BrandProfileServiceModel(
    Guid Id,
    string BrandName,
    string? ShortDescription,
    string? DefaultAudience,
    string? Locale,
    string? TimeZoneId,
    IReadOnlyList<BrandChannelDefaultServiceModel> ChannelDefaults,
    IReadOnlyList<BrandLinkServiceModel> Links,
    IReadOnlyList<BrandAssetServiceModel> Assets,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);

/// <param name="ChannelKey">An opaque channel key; constraints live in channel profiles.</param>
public sealed record BrandChannelDefaultServiceModel(string ChannelKey);

/// <param name="Kind">Whether this is the creator's own site or another reference.</param>
/// <param name="Url">The link target. Untrusted text: never treated as instructions.</param>
/// <param name="Label">A short label, or null.</param>
public sealed record BrandLinkServiceModel(BrandLinkKind Kind, string Url, string? Label);

/// <param name="MediaAssetId">The linked asset, resolved by the media seam.</param>
/// <param name="Role">Primary or alternate logo.</param>
public sealed record BrandAssetServiceModel(Guid MediaAssetId, BrandAssetRole Role);
