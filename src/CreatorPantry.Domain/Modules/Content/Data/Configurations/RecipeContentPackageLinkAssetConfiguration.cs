using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class RecipeContentPackageLinkAssetConfiguration : IEntityTypeConfiguration<RecipeContentPackageLinkAsset>
{
    public void Configure(EntityTypeBuilder<RecipeContentPackageLinkAsset> builder)
    {
        builder.ToTable("RecipeContentPackageLinkAssets", table =>
        {
            table.HasCheckConstraint("CK_RecipeContentPackageLinkAssets_SortOrder_NonNegative", "SortOrder >= 0");
            table.HasCheckConstraint(
                "CK_RecipeContentPackageLinkAssets_Version_Positive", "MediaAssetVersionNumber >= 1");
        });

        builder.HasKey(asset => asset.Id);
        builder.Property(asset => asset.Id).ValueGeneratedNever();

        builder.Property(asset => asset.MediaAssetVersionNumber).IsRequired();
        builder.Property(asset => asset.SortOrder).IsRequired();

        // One cascade path: workspace, card, link, this.
        builder.HasOne<RecipeContentPackageLink>()
            .WithMany(link => link.Assets)
            .HasForeignKey(asset => new { asset.WorkspaceId, asset.RecipeContentPackageLinkId })
            .HasPrincipalKey(link => new { link.WorkspaceId, link.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The exact version, in this workspace: the pin cannot name a version the asset does not have.
        builder.HasOne<MediaAssetVersion>()
            .WithMany()
            .HasForeignKey(asset => new { asset.WorkspaceId, asset.MediaAssetId, asset.MediaAssetVersionNumber })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber })
            .OnDelete(DeleteBehavior.Restrict);

        // A package names a picture once, at one version.
        builder.HasIndex(asset => new { asset.WorkspaceId, asset.RecipeContentPackageLinkId, asset.MediaAssetId })
            .IsUnique()
            .HasDatabaseName("UX_RecipeContentPackageLinkAssets_Workspace_Link_Asset");
        builder.HasIndex(asset => new { asset.WorkspaceId, asset.RecipeContentPackageLinkId, asset.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_RecipeContentPackageLinkAssets_Workspace_Link_SortOrder");
    }
}
