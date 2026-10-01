using CreatorPantry.Domain.Modules.Brand.Data.Entities;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Brand.Data.Configurations;

internal sealed class BrandSourceTagConfiguration : IEntityTypeConfiguration<BrandSourceTag>
{
    public void Configure(EntityTypeBuilder<BrandSourceTag> builder)
    {
        builder.ToTable("BrandSourceTags", table =>
            table.HasCheckConstraint("CK_BrandSourceTags_Name_NotBlank", "trim(Name) <> ''"));

        builder.HasKey(tag => tag.Id);
        builder.Property(tag => tag.Id).ValueGeneratedNever();

        // The target of BrandSourceDocumentTag's composite foreign key: a document cannot carry a tag from
        // another workspace because the workspace is part of the key it points at.
        builder.HasAlternateKey(tag => new { tag.WorkspaceId, tag.Id })
            .HasName("AK_BrandSourceTags_Workspace_Id");

        builder.Property(tag => tag.Name).IsRequired().HasMaxLength(BrandPolicy.SourceTagNameMaxLength);
        builder.Property(tag => tag.NormalizedName).IsRequired().HasMaxLength(BrandPolicy.SourceTagNameMaxLength);
        builder.Property(tag => tag.IsActive).IsRequired();
        builder.Property(tag => tag.CreatedAt).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(tag => tag.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The natural key, and workspace-relative: two creators may each have a "launch" tag, and neither can
        // have two.
        builder.HasIndex(tag => new { tag.WorkspaceId, tag.NormalizedName })
            .IsUnique()
            .HasDatabaseName("UX_BrandSourceTags_Workspace_NormalizedName");

        // Offering a workspace's live tags, alphabetically.
        builder.HasIndex(tag => new { tag.WorkspaceId, tag.IsActive, tag.Name })
            .HasDatabaseName("IX_BrandSourceTags_Workspace_IsActive_Name");
    }
}
