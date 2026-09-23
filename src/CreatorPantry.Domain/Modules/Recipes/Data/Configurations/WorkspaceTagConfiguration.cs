using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class WorkspaceTagConfiguration : IEntityTypeConfiguration<WorkspaceTag>
{
    public void Configure(EntityTypeBuilder<WorkspaceTag> builder)
    {
        builder.ToTable("WorkspaceTags");

        builder.HasKey(tag => tag.Id);

        // The target of RecipeTag's composite foreign key: a recipe cannot carry a tag from another workspace
        // because the workspace is part of the key it points at.
        builder.HasAlternateKey(tag => new { tag.WorkspaceId, tag.Id })
            .HasName("AK_WorkspaceTags_Workspace_Id");

        builder.Property(tag => tag.Name)
            .IsRequired()
            .HasMaxLength(TagPolicy.NameMaxLength);

        builder.Property(tag => tag.NormalizedName)
            .IsRequired()
            .HasMaxLength(TagPolicy.NameMaxLength);

        builder.Property(tag => tag.IsActive).IsRequired();
        builder.Property(tag => tag.CreatedAt).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(tag => tag.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The natural key, and workspace-relative: two creators may each have a "weeknight" tag, and neither
        // can have two. Normalization is what collapses the capitalisation and spacing variants into one row
        // (NameNormalization, the same rule the ingredient vocabulary uses).
        builder.HasIndex(tag => new { tag.WorkspaceId, tag.NormalizedName })
            .IsUnique()
            .HasDatabaseName("UX_WorkspaceTags_Workspace_NormalizedName");

        // Offering a workspace's live tags, alphabetically.
        builder.HasIndex(tag => new { tag.WorkspaceId, tag.IsActive, tag.Name })
            .HasDatabaseName("IX_WorkspaceTags_Workspace_IsActive_Name");
    }
}
