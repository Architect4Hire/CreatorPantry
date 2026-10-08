namespace CreatorPantry.Domain.Modules.Media.Managers;

/// <summary>
/// What resolving a would-be link target found (12.10i): whether another module may store a reference to an
/// asset, optionally pinned to one of its versions.
/// </summary>
/// <remarks>
/// A published answer rather than a boolean, because the two refusals have different remedies — pick another
/// picture, or pick another version of this one — and a caller that could not tell them apart could only say
/// "that did not work". It still discloses nothing: <see cref="AssetNotFound"/> is one answer for an unknown
/// asset, a neighbour's and a removed one, and <see cref="VersionNotFound"/> is only ever said about an asset
/// the caller's own workspace holds.
/// </remarks>
public enum MediaAssetLinkTarget
{
    /// <summary>The asset is in this workspace's library, and the pinned version, if any, is one of its own.</summary>
    Linkable = 0,

    /// <summary>No live asset with that id in this workspace: unknown, a neighbour's, or removed.</summary>
    AssetNotFound = 1,

    /// <summary>The asset is there and has no version with that number.</summary>
    VersionNotFound = 2,
}
