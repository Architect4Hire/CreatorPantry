using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeIngredientGroupConfiguration : IEntityTypeConfiguration<RecipeIngredientGroup>
{
    public void Configure(EntityTypeBuilder<RecipeIngredientGroup> builder)
    {
        builder.ToTable("RecipeIngredientGroups", table =>
            table.HasCheckConstraint(
                "CK_RecipeIngredientGroups_SortOrder_NonNegative",
                "SortOrder >= 0"));

        builder.HasKey(group => group.Id);

        // Carries RecipeId as well as WorkspaceId so an ingredient line's composite foreign key pins both:
        // a line cannot end up in a group belonging to another recipe, nor to another workspace.
        builder.HasAlternateKey(group => new { group.WorkspaceId, group.RecipeId, group.Id })
            .HasName("AK_RecipeIngredientGroups_Workspace_Recipe_Id");

        builder.Property(group => group.Title).HasMaxLength(RecipePolicy.GroupTitleMaxLength);
        builder.Property(group => group.SortOrder).IsRequired();

        // The only route into this table. There is no direct foreign key to Workspaces: WorkspaceId is part
        // of this key, so the workspace is guaranteed transitively and exactly one cascade path reaches here.
        builder.HasOne<Recipe>()
            .WithMany(recipe => recipe.IngredientGroups)
            .HasForeignKey(group => new { group.WorkspaceId, group.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Ordering is unique, so a list cannot be silently ambiguous. The cost is that a reorder writes in
        // two passes — positions move to a disjoint range first — because SQL Server has no deferred
        // constraints. That belongs to the DataLayer that owns the aggregate write.
        builder.HasIndex(group => new { group.WorkspaceId, group.RecipeId, group.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_RecipeIngredientGroups_Workspace_Recipe_Order");
    }
}
