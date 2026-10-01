using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceExtractionConfiguration : IEntityTypeConfiguration<BrandSourceExtraction>
{
    public void Configure(EntityTypeBuilder<BrandSourceExtraction> builder)
    {
        builder.ToTable("BrandSourceExtractions", table =>
        {
            table.HasCheckConstraint("CK_BrandSourceExtractions_Ordinal_Positive", "Ordinal >= 1");
            table.HasCheckConstraint("CK_BrandSourceExtractions_Status_Specified", "Status <> 0");
            table.HasCheckConstraint("CK_BrandSourceExtractions_Origin_Specified", "Origin <> 0");

            // Text exists exactly when the attempt succeeded, so a pointer can never outlive a failure.
            table.HasCheckConstraint(
                "CK_BrandSourceExtractions_Succeeded_HasArtifact",
                $"(Status = {(int)BrandSourceExtractionStatus.Succeeded} AND ExtractedTextObjectKey IS NOT NULL AND ContentChecksum IS NOT NULL) OR "
                    + $"(Status <> {(int)BrandSourceExtractionStatus.Succeeded} AND ExtractedTextObjectKey IS NULL AND ContentChecksum IS NULL)");

            // A correction is text a named person supplied.
            table.HasCheckConstraint(
                "CK_BrandSourceExtractions_Corrected_Succeeded_Attributed",
                $"Origin <> {(int)BrandSourceExtractionOrigin.Corrected} OR "
                    + $"(Status = {(int)BrandSourceExtractionStatus.Succeeded} AND CreatedByMembershipId IS NOT NULL)");

            table.HasCheckConstraint(
                "CK_BrandSourceExtractions_ObjectKey_NotUrl",
                "ExtractedTextObjectKey IS NULL OR (trim(ExtractedTextObjectKey) <> '' AND ExtractedTextObjectKey NOT LIKE '%://%')");
            table.HasCheckConstraint(
                "CK_BrandSourceExtractions_ContentChecksum_Sha256",
                "ContentChecksum IS NULL OR ContentChecksum LIKE 'sha256:%'");
        });

        builder.HasKey(extraction => extraction.Id);
        builder.Property(extraction => extraction.Id).ValueGeneratedNever();

        builder.HasAlternateKey(extraction => new { extraction.WorkspaceId, extraction.Id })
            .HasName("AK_BrandSourceExtractions_Workspace_Id");

        // Target of a chunk set's reference to the exact artifact it was cut from, and wider than it needs to
        // be to identify a row on purpose. Carrying the version and the status into the key means a referencing
        // row has to state both and cannot state them wrongly: a set whose version disagrees with its
        // extraction's has no key to resolve against, and neither does one hung off an extraction that failed
        // or was unsupported. An extraction is immutable, so neither column can drift after the reference is
        // made. See BrandSourceChunkSetConfiguration for the other half.
        builder.HasAlternateKey(extraction => new
        {
            extraction.WorkspaceId,
            extraction.Id,
            extraction.BrandSourceDocumentVersionId,
            extraction.Status,
        })
            .HasName("AK_BrandSourceExtractions_Workspace_Id_Version_Status");

        builder.Property(extraction => extraction.Ordinal).IsRequired();
        builder.Property(extraction => extraction.Status).IsRequired();
        builder.Property(extraction => extraction.Origin).IsRequired();
        builder.Property(extraction => extraction.ExtractedTextObjectKey).HasMaxLength(BrandPolicy.ObjectKeyMaxLength);
        builder.Property(extraction => extraction.ContentChecksum).HasMaxLength(BrandPolicy.ChecksumMaxLength);
        builder.Property(extraction => extraction.Reason).HasMaxLength(BrandPolicy.ReasonMaxLength);
        builder.Property(extraction => extraction.CreatedAt).IsRequired();

        // The erasure path. Cascade comes from the workspace alone.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(extraction => extraction.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: an extraction is blob-backed history of its version and outlives anything that would remove it.
        builder.HasOne<BrandSourceDocumentVersion>()
            .WithMany()
            .HasForeignKey(extraction => new { extraction.WorkspaceId, extraction.BrandSourceDocumentVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One ordinal per version, which also finds a version's current extraction: the highest.
        builder.HasIndex(extraction => new { extraction.WorkspaceId, extraction.BrandSourceDocumentVersionId, extraction.Ordinal })
            .IsUnique()
            .HasDatabaseName("UX_BrandSourceExtractions_Workspace_Version_Ordinal");

        // One row per text blob.
        builder.HasIndex(extraction => new { extraction.WorkspaceId, extraction.ExtractedTextObjectKey })
            .IsUnique()
            .HasDatabaseName("UX_BrandSourceExtractions_Workspace_ObjectKey");
    }
}
