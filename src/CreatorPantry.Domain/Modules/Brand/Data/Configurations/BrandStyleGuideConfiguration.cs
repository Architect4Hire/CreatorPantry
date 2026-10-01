using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandStyleGuideConfiguration : IEntityTypeConfiguration<BrandStyleGuide>
{
    public void Configure(EntityTypeBuilder<BrandStyleGuide> builder)
    {
        builder.ToTable("BrandStyleGuides", table =>
        {
            table.HasCheckConstraint("CK_BrandStyleGuides_DisplayName_NotBlank", "trim(DisplayName) <> ''");
            table.HasCheckConstraint("CK_BrandStyleGuides_Status_Specified", "Status <> 0");

            // An archived guide says when. The timestamp survives a restore, so the reverse is not required.
            table.HasCheckConstraint(
                "CK_BrandStyleGuides_Archived_HasTimestamp",
                $"Status <> {(int)BrandStyleGuideStatus.Archived} OR ArchivedAt IS NOT NULL");
        });

        builder.HasKey(guide => guide.Id);

        // Set by the application, never the store. Without this EF reads a preset Guid key on a row added through
        // a tracked parent's collection as an existing row and issues an UPDATE that matches nothing.
        builder.Property(guide => guide.Id).ValueGeneratedNever();

        // The target of a version's composite foreign key, so a version owned by a different workspace than
        // its guide is unrepresentable.
        builder.HasAlternateKey(guide => new { guide.WorkspaceId, guide.Id })
            .HasName("AK_BrandStyleGuides_Workspace_Id");

        builder.Property(guide => guide.DisplayName).IsRequired().HasMaxLength(BrandPolicy.StyleGuideDisplayNameMaxLength);
        builder.Property(guide => guide.Purpose).HasMaxLength(BrandPolicy.StyleGuidePurposeMaxLength);
        builder.Property(guide => guide.Status).IsRequired();
        builder.Property(guide => guide.CreatedByMembershipId).IsRequired();
        builder.Property(guide => guide.UpdatedByMembershipId).IsRequired();
        builder.Property(guide => guide.CreatedAt).IsRequired();
        builder.Property(guide => guide.UpdatedAt).IsRequired();
        builder.Property(guide => guide.RowVersion).IsRowVersion();

        // The erasure path, and the only thing that deletes a guide.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(guide => guide.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The guide picker: a workspace's live guides, by name.
        builder.HasIndex(guide => new { guide.WorkspaceId, guide.Status, guide.DisplayName })
            .HasDatabaseName("IX_BrandStyleGuides_Workspace_Status_DisplayName");
    }
}
