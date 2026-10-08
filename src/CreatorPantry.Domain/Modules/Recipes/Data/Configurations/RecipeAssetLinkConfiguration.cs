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

            // A step image names its step, and nothing else names one. Either half alone is a row no screen
            // could render: a step image belonging to no step, or a hero that claims to belong to one.
            table.HasCheckConstraint(
                "CK_RecipeAssetLinks_Step_Paired",
                $"(Role = {(int)RecipeAssetRole.Step} AND InstructionStepId IS NOT NULL) OR "
                    + $"(Role <> {(int)RecipeAssetRole.Step} AND InstructionStepId IS NULL)");

            // Version numbers start at one. Zero or a negative could never match a version, so it is refused
            // here with a name rather than surfacing as a foreign-key violation.
            table.HasCheckConstraint(
                "CK_RecipeAssetLinks_VersionPin_Positive",
                "MediaAssetVersionNumber IS NULL OR MediaAssetVersionNumber >= 1");
        });

        builder.HasKey(link => link.Id);

        // See RecipeInstructionGroupConfiguration's Id configuration for why this matters: without it, a link
        // added to an existing recipe (12.10i) is misread as an update to a row that does not exist, and the
        // save reports a concurrency conflict nobody caused.
        builder.Property(link => link.Id).ValueGeneratedNever();

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

        // The pin (12.10i). A second key over the same two leading columns as the one above, narrowed to one
        // version: unpinned rows carry a null and are not checked, and a pinned one cannot name a version the
        // asset does not have — or another workspace's version of an asset with the same id. Restricted for
        // the reason above; versions are never deleted in any case.
        builder.HasOne<MediaAssetVersion>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.MediaAssetId, link.MediaAssetVersionNumber })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.MediaAssetId, version.VersionNumber })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // The step a step image belongs to. Restricted, never cascading: Recipe already cascades into this
        // table directly, and a second path through the instruction tree is what SQL Server refuses. The
        // consequence is deliberate — removing a step cannot remove its image, so the edit that removes one
        // demotes the link to a recipe-level image first (RecipeAssetRole).
        builder.HasOne<RecipeInstructionStep>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.InstructionStepId })
            .HasPrincipalKey(step => new { step.WorkspaceId, step.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(link => new { link.WorkspaceId, link.InstructionStepId })
            .HasDatabaseName("IX_RecipeAssetLinks_Workspace_InstructionStep");

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
