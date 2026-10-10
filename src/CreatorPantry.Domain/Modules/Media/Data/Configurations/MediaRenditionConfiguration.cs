using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Media.Data.Configurations;

internal sealed class MediaRenditionConfiguration : IEntityTypeConfiguration<MediaRendition>
{
    public void Configure(EntityTypeBuilder<MediaRendition> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("MediaRenditions", table =>
        {
            // Exactly one source: a staged image, or one version of a library asset. Never both and never
            // neither, so a row can always be found by the picture it was made from.
            table.HasCheckConstraint(
                "CK_MediaRenditions_OneSource",
                "(GeneratedImageId IS NOT NULL AND MediaAssetId IS NULL AND MediaAssetVersionNumber IS NULL) "
                    + "OR (GeneratedImageId IS NULL AND MediaAssetId IS NOT NULL AND MediaAssetVersionNumber IS NOT NULL)");

            table.HasCheckConstraint("CK_MediaRenditions_Purpose_Declared", "Purpose IN (1, 2)");

            // Ready describes stored bytes and gives no reason; not-compressed gives a reason and describes
            // none. Either half alone would let a row claim bytes it does not have, or hide why it has none.
            table.HasCheckConstraint(
                "CK_MediaRenditions_Status_Agrees",
                "(Status = 1 AND NotCompressedReason IS NULL AND ObjectKey IS NOT NULL AND MediaType IS NOT NULL "
                    + "AND SizeBytes IS NOT NULL AND Width IS NOT NULL AND Height IS NOT NULL AND ContentChecksum IS NOT NULL) "
                    + "OR (Status = 2 AND NotCompressedReason IS NOT NULL AND ObjectKey IS NULL AND MediaType IS NULL "
                    + "AND SizeBytes IS NULL AND Width IS NULL AND Height IS NULL AND ContentChecksum IS NULL)");

            table.HasCheckConstraint(
                "CK_MediaRenditions_Reason_Declared",
                "NotCompressedReason IS NULL OR NotCompressedReason BETWEEN 1 AND 7");

            table.HasCheckConstraint(
                "CK_MediaRenditions_Bytes_Positive",
                "SizeBytes IS NULL OR (SizeBytes > 0 AND Width > 0 AND Height > 0)");

            table.HasCheckConstraint(
                "CK_MediaRenditions_Text_NotBlank",
                "trim(SourceContentChecksum) <> '' AND (ObjectKey IS NULL OR "
                    + "(trim(ObjectKey) <> '' AND trim(MediaType) <> '' AND trim(ContentChecksum) <> ''))");
        });

        builder.HasKey(rendition => rendition.Id);

        // Set by the application, never the store.
        builder.Property(rendition => rendition.Id).ValueGeneratedNever();

        builder.Property(rendition => rendition.SourceContentChecksum)
            .IsRequired()
            .HasMaxLength(MediaPolicy.ChecksumMaxLength);

        builder.Property(rendition => rendition.ObjectKey).HasMaxLength(MediaPolicy.ObjectKeyMaxLength);
        builder.Property(rendition => rendition.MediaType).HasMaxLength(MediaPolicy.MediaTypeMaxLength);
        builder.Property(rendition => rendition.ContentChecksum).HasMaxLength(MediaPolicy.ChecksumMaxLength);
        builder.Property(rendition => rendition.CreatedAt).IsRequired();

        // The erasure path, and the only cascade into this table.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(rendition => rendition.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite, on the workspace as well as the id, so a row cannot name another workspace's picture
        // even if something upstream were wrong. Restrict rather than cascade: the workspace cascade above
        // already reaches this table, and a second path to it is what SQL Server refuses. Neither source
        // row is deleted by ordinary code, and a rendition's bytes have to be removed before its row is,
        // so a cascade here would only ever orphan an object.
        builder.HasOne<GeneratedImage>()
            .WithMany()
            .HasForeignKey(rendition => new { rendition.WorkspaceId, rendition.GeneratedImageId })
            .HasPrincipalKey(image => new { image.WorkspaceId, image.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MediaAssetVersion>()
            .WithMany()
            .HasForeignKey(rendition => new
            {
                rendition.WorkspaceId,
                rendition.MediaAssetId,
                rendition.MediaAssetVersionNumber,
            })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber })
            .OnDelete(DeleteBehavior.Restrict);

        // One rendition per source and purpose. Filtered, because each identity is null for the other kind
        // of source. This is what a retried job collides on rather than storing a second copy.
        builder.HasIndex(rendition => new { rendition.WorkspaceId, rendition.GeneratedImageId, rendition.Purpose })
            .IsUnique()
            .HasFilter("GeneratedImageId IS NOT NULL")
            .HasDatabaseName("UX_MediaRenditions_Workspace_GeneratedImage_Purpose");

        builder.HasIndex(rendition => new
            {
                rendition.WorkspaceId,
                rendition.MediaAssetId,
                rendition.MediaAssetVersionNumber,
                rendition.Purpose,
            })
            .IsUnique()
            .HasFilter("MediaAssetId IS NOT NULL")
            .HasDatabaseName("UX_MediaRenditions_Workspace_Asset_Version_Purpose");

        // One row per set of bytes. Two rows naming one object would let removing one rendition take
        // another's file.
        builder.HasIndex(rendition => rendition.ObjectKey)
            .IsUnique()
            .HasFilter("ObjectKey IS NOT NULL")
            .HasDatabaseName("UX_MediaRenditions_ObjectKey");
    }
}
