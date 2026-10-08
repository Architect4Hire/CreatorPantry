using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// A minimal <see cref="MediaAsset"/> and its first version, for tests that need something real for a
/// link to point at.
/// </summary>
/// <remarks>
/// <para>
/// 12.9 gave <c>RecipeAssetLink</c>, <c>BrandAssetLink</c> and <c>TestAttachmentLink</c> the composite
/// foreign key their configurations had promised since Phase 2. Before that, every one of those tests
/// seeded <c>MediaAssetId = Guid.NewGuid()</c> and the database accepted it — which is precisely the
/// cross-workspace hole the key closes, and precisely why those seeds now have to name a real asset.
/// </para>
/// <para>
/// Deliberately the smallest asset the constraints allow. A test about recipe links should say nothing
/// about media metadata, so this fills in what the schema requires and no more.
/// </para>
/// </remarks>
internal static class SeededMediaAsset
{
    /// <summary>An asset owned by <paramref name="workspaceId"/>, with no version of its own.</summary>
    /// <remarks>
    /// Versions are left out because nothing a link test does reads one, and a version would drag in an
    /// object key, a checksum and dimensions that would all be fiction.
    /// </remarks>
    public static MediaAsset For(Guid workspaceId, Guid? id = null, DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var actor = Guid.NewGuid();

        return new MediaAsset
        {
            Id = id ?? Guid.NewGuid(),
            WorkspaceId = workspaceId,
            Title = "Seeded asset",
            Kind = MediaAssetKind.Original,
            CurrentVersionNumber = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByMembershipId = actor,
            UpdatedByMembershipId = actor,
        };
    }

    /// <summary>The first version of a seeded asset, for a test that pins a link to one (12.10i).</summary>
    /// <remarks>
    /// Separate from <see cref="For"/>, which most link tests want as it is. The object key, checksum and
    /// dimensions are placeholders: no test that uses this reads bytes, only whether the version exists.
    /// </remarks>
    public static MediaAssetVersion VersionOf(MediaAsset asset, int versionNumber = 1) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = asset.WorkspaceId,
            MediaAssetId = asset.Id,
            VersionNumber = versionNumber,
            MediaType = "image/jpeg",
            SizeBytes = 1024,
            Width = 800,
            Height = 600,
            ContentChecksum = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
            ObjectKey = $"assets/{asset.Id:D}/{versionNumber}.jpg",
            Source = MediaAssetVersionSource.Upload,
            CreatedByMembershipId = asset.CreatedByMembershipId,
            CreatedAt = asset.CreatedAt,
        };
}
