using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Media.Data.Configurations;

internal sealed class GeneratedImageConfiguration : IEntityTypeConfiguration<GeneratedImage>
{
    public void Configure(EntityTypeBuilder<GeneratedImage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("GeneratedImages");
        builder.HasKey(image => image.Id);

        // What PromptRecord's composite foreign key points at (12.3's note): the pair is the principal key, so
        // a prompt record can only ever name an image of its own workspace.
        builder.HasAlternateKey(image => new { image.WorkspaceId, image.Id });

        builder.Property(image => image.ObjectKey)
            .IsRequired()
            .HasMaxLength(MediaPolicy.ObjectKeyMaxLength);

        builder.Property(image => image.MediaType)
            .IsRequired()
            .HasMaxLength(MediaPolicy.MediaTypeMaxLength);

        builder.Property(image => image.ContentChecksum)
            .IsRequired()
            .HasMaxLength(MediaPolicy.ChecksumMaxLength);

        builder.Property(image => image.ProviderName)
            .IsRequired()
            .HasMaxLength(MediaPolicy.ProviderIdentifierMaxLength);

        builder.Property(image => image.ModelName)
            .IsRequired()
            .HasMaxLength(MediaPolicy.ProviderIdentifierMaxLength);

        builder.Property(image => image.ModelDeployment)
            .HasMaxLength(MediaPolicy.ProviderIdentifierMaxLength);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(image => image.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restricted rather than cascading, so there is exactly one cascade path from Workspace into this
        // table. SQL Server refuses two, and SQLite does not — which is why the migration is checked against
        // a real engine rather than trusted because the in-memory suite was green.
        builder.HasOne<GeneratedImageOperation>()
            .WithMany()
            .HasForeignKey(image => new { image.WorkspaceId, image.GeneratedImageOperationId })
            .HasPrincipalKey(operation => new { operation.WorkspaceId, operation.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One row per variant of one request. This is what makes "a retry cannot double-stage" a property of
        // the schema rather than something a worker has to remember.
        builder.HasIndex(image => new { image.GeneratedImageOperationId, image.VariantIndex })
            .IsUnique()
            .HasDatabaseName("UX_GeneratedImages_Operation_Variant");

        // One row per staging object. Two rows naming the same bytes would let a rejection delete an image
        // somebody else kept, which is a data-loss bug rather than an inconsistency.
        builder.HasIndex(image => image.ObjectKey)
            .IsUnique()
            .HasDatabaseName("UX_GeneratedImages_ObjectKey");

        // 12.8's retention sweep, filtered to the one status it may act on: a kept image's deadline is a
        // historical fact, and an index that included it would grow forever for no reader.
        builder.HasIndex(image => new { image.Status, image.RetentionExpiresAt })
            .HasFilter("Status = 1")
            .HasDatabaseName("IX_GeneratedImages_Status_RetentionExpiresAt");

        // 12.8's purge queue: rows whose bytes are still in staging and whose status says they should not
        // be. Filtered to exactly that set, so the sweep's read costs nothing once it has drained — which
        // is the whole reason ObjectDeletedAt exists.
        builder.HasIndex(image => new { image.Status, image.ObjectDeletedAt })
            .HasFilter("Status IN (2, 3, 4) AND ObjectDeletedAt IS NULL")
            .HasDatabaseName("IX_GeneratedImages_Status_ObjectDeletedAt");

        // A workspace reading one request's images, in variant order.
        builder.HasIndex(image => new { image.WorkspaceId, image.GeneratedImageOperationId, image.VariantIndex })
            .HasDatabaseName("IX_GeneratedImages_Workspace_Operation_Variant");

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_GeneratedImages_Status_Declared", "Status <> 0");

            table.HasCheckConstraint(
                "CK_GeneratedImages_VariantIndex_Range",
                $"VariantIndex >= 0 AND VariantIndex < {MediaPolicy.MaxVariantsPerOperation}");

            // A staged image describes real bytes, so none of these may be absent or nonsensical.
            table.HasCheckConstraint(
                "CK_GeneratedImages_Dimensions_Positive",
                "Width > 0 AND Height > 0");

            table.HasCheckConstraint(
                "CK_GeneratedImages_Pixels_Range",
                $"CAST(Width AS bigint) * CAST(Height AS bigint) <= {MediaPolicy.ImageMaxPixels}");

            table.HasCheckConstraint("CK_GeneratedImages_SizeBytes_Positive", "SizeBytes > 0");

            // trim rather than LEN(LTRIM(RTRIM(..))): a check constraint is created verbatim by every
            // harness, including the SQLite ones, and SQLite has no LEN — which fails the CREATE TABLE and
            // so fails every test in every SQLite fixture, on a table none of them touch. The rest of the
            // repo uses this portable form for exactly that reason.
            table.HasCheckConstraint(
                "CK_GeneratedImages_ObjectKey_NotBlank",
                "trim(ObjectKey) <> ''");

            table.HasCheckConstraint(
                "CK_GeneratedImages_Checksum_NotBlank",
                "trim(ContentChecksum) <> ''");
        });
    }
}
