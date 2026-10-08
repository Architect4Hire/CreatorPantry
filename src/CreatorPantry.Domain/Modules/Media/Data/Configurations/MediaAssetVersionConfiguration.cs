using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Media.Data.Configurations;

internal sealed class MediaAssetVersionConfiguration : IEntityTypeConfiguration<MediaAssetVersion>
{
    public void Configure(EntityTypeBuilder<MediaAssetVersion> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MediaAssetVersions");
        builder.HasKey(version => version.Id);

        builder.Property(version => version.MediaType)
            .IsRequired()
            .HasMaxLength(MediaPolicy.MediaTypeMaxLength);

        builder.Property(version => version.ContentChecksum)
            .IsRequired()
            .HasMaxLength(MediaPolicy.ChecksumMaxLength);

        builder.Property(version => version.ObjectKey)
            .IsRequired()
            .HasMaxLength(MediaPolicy.ObjectKeyMaxLength);

        builder.Property(version => version.OriginalFileName)
            .HasMaxLength(MediaPolicy.FileNameMaxLength);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(version => version.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restricted rather than cascading, so there is exactly one cascade path from Workspace into this
        // table. SQL Server refuses two, and SQLite does not — which is why this migration is checked
        // against a real engine rather than trusted because the in-memory suite was green.
        builder.HasOne<MediaAsset>()
            .WithMany(asset => asset.Versions)
            .HasForeignKey(version => new { version.WorkspaceId, version.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The staged image these bytes came from, when they came from one. Restricted for the same reason,
        // and because 12.8 keeps the row after it removes the bytes: the provenance outlives the copy.
        builder.HasOne<GeneratedImage>()
            .WithMany()
            .HasForeignKey(version => new { version.WorkspaceId, version.SourceGeneratedImageId })
            .HasPrincipalKey(image => new { image.WorkspaceId, image.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // What a pin points at (12.10i): a recipe's or a test run's link may name one version of an asset, and
        // the workspace has to be in the key for the reason it is in every other one — a pin that matched on
        // asset and number alone could be satisfied by another workspace's row.
        builder.HasAlternateKey(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber });

        // DAM-010's "concurrent uploads cannot share a version number", as a property of the schema rather
        // than something the application has to remember.
        builder.HasIndex(version => new { version.MediaAssetId, version.VersionNumber })
            .IsUnique()
            .HasDatabaseName("UX_MediaAssetVersions_Asset_VersionNumber");

        // One row per set of bytes. Two rows naming one object would let deleting a version take away
        // another version's file, which is data loss rather than an inconsistency.
        builder.HasIndex(version => version.ObjectKey)
            .IsUnique()
            .HasDatabaseName("UX_MediaAssetVersions_ObjectKey");

        // DAM-006 and DAM-007 read the latest version; DAM-003 lists the history newest first. Both are
        // this one seek.
        builder.HasIndex(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_MediaAssetVersions_Workspace_Asset_VersionNumber");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_MediaAssetVersions_Source_Declared", "Source <> 0");

            table.HasCheckConstraint("CK_MediaAssetVersions_VersionNumber_Positive", "VersionNumber >= 1");

            // A version describes real bytes, so none of these may be absent or nonsensical.
            table.HasCheckConstraint("CK_MediaAssetVersions_Dimensions_Positive", "Width > 0 AND Height > 0");

            table.HasCheckConstraint(
                "CK_MediaAssetVersions_Pixels_Range",
                $"CAST(Width AS bigint) * CAST(Height AS bigint) <= {MediaPolicy.ImageMaxPixels}");

            table.HasCheckConstraint("CK_MediaAssetVersions_SizeBytes_Positive", "SizeBytes > 0");

            table.HasCheckConstraint("CK_MediaAssetVersions_ObjectKey_NotBlank", "trim(ObjectKey) <> ''");

            table.HasCheckConstraint("CK_MediaAssetVersions_Checksum_NotBlank", "trim(ContentChecksum) <> ''");

            // A generated version names the image it came from and an upload does not. Either column
            // alone would let a row claim a provenance it has no evidence for.
            table.HasCheckConstraint(
                "CK_MediaAssetVersions_Source_Agrees",
                "(Source = 1 AND SourceGeneratedImageId IS NULL) "
                    + "OR (Source = 2 AND SourceGeneratedImageId IS NOT NULL)");
        });
    }
}
