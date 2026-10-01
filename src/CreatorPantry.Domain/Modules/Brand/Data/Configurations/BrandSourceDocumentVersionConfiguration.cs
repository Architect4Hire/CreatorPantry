using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceDocumentVersionConfiguration : IEntityTypeConfiguration<BrandSourceDocumentVersion>
{
    public void Configure(EntityTypeBuilder<BrandSourceDocumentVersion> builder)
    {
        builder.ToTable("BrandSourceDocumentVersions", table =>
        {
            table.HasCheckConstraint("CK_BrandSourceDocumentVersions_VersionNumber_Positive", "VersionNumber >= 1");
            table.HasCheckConstraint("CK_BrandSourceDocumentVersions_SizeBytes_Positive", "SizeBytes > 0");
            table.HasCheckConstraint("CK_BrandSourceDocumentVersions_MediaType_NotBlank", "trim(MediaType) <> ''");
            table.HasCheckConstraint("CK_BrandSourceDocumentVersions_OriginalFileName_NotBlank", "trim(OriginalFileName) <> ''");
            table.HasCheckConstraint("CK_BrandSourceDocumentVersions_ContentChecksum_Sha256", "ContentChecksum LIKE 'sha256:%'");

            // A pointer into a private container, never an address: no scheme, so no public or signed URL.
            table.HasCheckConstraint(
                "CK_BrandSourceDocumentVersions_ObjectKey_NotUrl",
                "trim(ObjectKey) <> '' AND ObjectKey NOT LIKE '%://%'");
        });

        builder.HasKey(version => version.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(version => version.Id).ValueGeneratedNever();

        // Target of an extraction's foreign key and, later, a guide version's source pin.
        builder.HasAlternateKey(version => new { version.WorkspaceId, version.Id })
            .HasName("AK_BrandSourceDocumentVersions_Workspace_Id");

        builder.Property(version => version.VersionNumber).IsRequired();
        builder.Property(version => version.MediaType).IsRequired().HasMaxLength(BrandPolicy.MediaTypeMaxLength);
        builder.Property(version => version.SizeBytes).IsRequired();
        builder.Property(version => version.ContentChecksum).IsRequired().HasMaxLength(BrandPolicy.ChecksumMaxLength);
        builder.Property(version => version.OriginalFileName).IsRequired().HasMaxLength(BrandPolicy.OriginalFileNameMaxLength);
        builder.Property(version => version.ObjectKey).IsRequired().HasMaxLength(BrandPolicy.ObjectKeyMaxLength);
        builder.Property(version => version.CreatedByMembershipId).IsRequired();
        builder.Property(version => version.CreatedAt).IsRequired();

        // The erasure path. Cascade comes from the workspace alone.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(version => version.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite, so a version of another workspace's document is unrepresentable. Restrict: a version names
        // a blob, and no ordinary delete of its document may take that history with it.
        builder.HasOne<BrandSourceDocument>()
            .WithMany(document => document.Versions)
            .HasForeignKey(version => new { version.WorkspaceId, version.BrandSourceDocumentId })
            .HasPrincipalKey(document => new { document.WorkspaceId, document.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One version number per document. Also the second guard on a lost race: two replacements that both
        // read version N cannot both write N + 1.
        builder.HasIndex(version => new { version.WorkspaceId, version.BrandSourceDocumentId, version.VersionNumber })
            .IsUnique()
            .HasDatabaseName("UX_BrandSourceDocumentVersions_Workspace_Document_VersionNumber");

        // One row per blob: two versions can never claim the same object.
        builder.HasIndex(version => new { version.WorkspaceId, version.ObjectKey })
            .IsUnique()
            .HasDatabaseName("UX_BrandSourceDocumentVersions_Workspace_ObjectKey");

        // "Has this workspace already uploaded these bytes". Not unique: the same file may back two documents.
        builder.HasIndex(version => new { version.WorkspaceId, version.ContentChecksum })
            .HasDatabaseName("IX_BrandSourceDocumentVersions_Workspace_ContentChecksum");
    }
}
