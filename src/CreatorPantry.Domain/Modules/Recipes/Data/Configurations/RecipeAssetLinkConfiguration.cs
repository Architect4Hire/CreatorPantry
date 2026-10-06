using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeAssetLinkConfiguration : IEntityTypeConfiguration<RecipeAssetLink>
{
    public void Configure(EntityTypeBuilder<RecipeAssetLink> builder)
    {
        builder.ToTable("RecipeAssetLinks", table =>
        {
            table.HasCheckConstraint(
                "CK_RecipeAssetLinks_SortOrder_NonNegative",
                "SortOrder >= 0");

            // Zero is not a role. A link whose role was never set would otherwise default to Hero and
            // collide with the real one through the filtered index below, reporting a duplicate hero where
            // the actual fault is a field nobody filled in.
            table.HasCheckConstraint(
                "CK_RecipeAssetLinks_Role_Specified",
                "Role <> 0");
        });

        builder.HasKey(link => link.Id);

        builder.Property(link => link.MediaAssetId).IsRequired();
        builder.Property(link => link.Role).IsRequired();
        builder.Property(link => link.SortOrder).IsRequired();
        builder.Property(link => link.Caption).HasMaxLength(RecipePolicy.CaptionMaxLength);

        builder.HasOne<Recipe>()
            .WithMany(recipe => recipe.AssetLinks)
            .HasForeignKey(link => new { link.WorkspaceId, link.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The constraint this comment used to promise, now that 12.9 has landed the aggregate. Composite,
        // because a plain MediaAssetId -> MediaAssets(Id) would still let one workspace's recipe link
        // another's photograph. Restricted, because deleting a link never deletes the asset — it is
        // independently owned creator property with its own retention rules (media.md) — and because
        // Workspace already cascades into this table through Recipe, and a second cascade path is what
        // SQL Server refuses outright.
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.MediaAssetId })
            .HasPrincipalKey(asset => new { asset.WorkspaceId, asset.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(link => new { link.WorkspaceId, link.RecipeId, link.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_RecipeAssetLinks_Workspace_Recipe_Order");

        // "Where is this image used", which the media library needs before it will let anything be deleted.
        builder.HasIndex(link => new { link.WorkspaceId, link.MediaAssetId })
            .HasDatabaseName("IX_RecipeAssetLinks_Workspace_MediaAsset");

        // At most one hero per recipe. A filtered unique index rather than an application rule, because "the
        // lead image" is singular by definition and two of them is a state no interface can render.
        builder.HasIndex(link => new { link.WorkspaceId, link.RecipeId })
            .IsUnique()
            .HasFilter($"Role = {(int)RecipeAssetRole.Hero}")
            .HasDatabaseName("UX_RecipeAssetLinks_Workspace_Recipe_Hero");
    }
}
