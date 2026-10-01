using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceDocumentConfiguration : IEntityTypeConfiguration<BrandSourceDocument>
{
    public void Configure(EntityTypeBuilder<BrandSourceDocument> builder)
    {
        builder.ToTable("BrandSourceDocuments", table =>
        {
            table.HasCheckConstraint("CK_BrandSourceDocuments_Title_NotBlank", "trim(Title) <> ''");
            table.HasCheckConstraint("CK_BrandSourceDocuments_DocumentType_Specified", "DocumentType <> 0");
            table.HasCheckConstraint("CK_BrandSourceDocuments_Purpose_Specified", "Purpose <> 0");
            table.HasCheckConstraint("CK_BrandSourceDocuments_Status_Specified", "Status <> 0");
            table.HasCheckConstraint("CK_BrandSourceDocuments_CurrentVersionNumber_Positive", "CurrentVersionNumber >= 1");

            // An archived document says when. The timestamp survives a restore, so the reverse is not required.
            table.HasCheckConstraint(
                "CK_BrandSourceDocuments_Archived_HasTimestamp",
                $"Status <> {(int)BrandSourceDocumentStatus.Archived} OR ArchivedAt IS NOT NULL");

            // A tombstone names when and who, and nothing else carries those.
            table.HasCheckConstraint(
                "CK_BrandSourceDocuments_Removed_Consistent",
                $"(Status = {(int)BrandSourceDocumentStatus.Removed} AND RemovedAt IS NOT NULL AND RemovedByMembershipId IS NOT NULL) OR "
                    + $"(Status <> {(int)BrandSourceDocumentStatus.Removed} AND RemovedAt IS NULL AND RemovedByMembershipId IS NULL)");
        });

        builder.HasKey(document => document.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(document => document.Id).ValueGeneratedNever();

        // The target of every child's composite foreign key, so a version or tag link owned by a different
        // workspace than its document is unrepresentable.
        builder.HasAlternateKey(document => new { document.WorkspaceId, document.Id })
            .HasName("AK_BrandSourceDocuments_Workspace_Id");

        builder.Property(document => document.Title).IsRequired().HasMaxLength(BrandPolicy.SourceDocumentTitleMaxLength);
        builder.Property(document => document.DocumentType).IsRequired();
        builder.Property(document => document.Purpose).IsRequired();
        builder.Property(document => document.ChannelKey).HasMaxLength(BrandPolicy.ChannelKeyMaxLength);
        builder.Property(document => document.Audience).HasMaxLength(BrandPolicy.SourceDocumentAudienceMaxLength);
        builder.Property(document => document.Status).IsRequired();
        builder.Property(document => document.CurrentVersionNumber).IsRequired();
        builder.Property(document => document.CreatedByMembershipId).IsRequired();
        builder.Property(document => document.UpdatedByMembershipId).IsRequired();
        builder.Property(document => document.CreatedAt).IsRequired();
        builder.Property(document => document.UpdatedAt).IsRequired();
        builder.Property(document => document.RowVersion).IsRowVersion();

        // The erasure path, and the only thing that deletes a document.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(document => document.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The library list: one status, newest first.
        builder.HasIndex(document => new { document.WorkspaceId, document.Status, document.UpdatedAt })
            .HasDatabaseName("IX_BrandSourceDocuments_Workspace_Status_UpdatedAt");

        builder.HasIndex(document => new { document.WorkspaceId, document.DocumentType })
            .HasDatabaseName("IX_BrandSourceDocuments_Workspace_DocumentType");

        builder.HasIndex(document => new { document.WorkspaceId, document.ChannelKey })
            .HasDatabaseName("IX_BrandSourceDocuments_Workspace_ChannelKey");
    }
}
