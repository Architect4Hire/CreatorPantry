using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeInstructionGroupConfiguration : IEntityTypeConfiguration<RecipeInstructionGroup>
{
    public void Configure(EntityTypeBuilder<RecipeInstructionGroup> builder)
    {
        builder.ToTable("RecipeInstructionGroups", table =>
            table.HasCheckConstraint(
                "CK_RecipeInstructionGroups_SortOrder_NonNegative",
                "SortOrder >= 0"));

        builder.HasKey(group => group.Id);

        // Business always assigns this itself (Guid.NewGuid()); it is never store-generated. Without this,
        // EF's default convention for a Guid key infers Added-vs-Modified from whether the value looks
        // "already assigned" — harmless for a brand-new recipe, where the whole graph is added together, but
        // wrong the moment a new group is attached to an *already-tracked* recipe (an edit): EF treats the
        // non-empty id as evidence the row might already exist, marks the entity Modified instead of Added,
        // and the resulting UPDATE matches no row — surfacing as a false concurrency conflict.
        builder.Property(group => group.Id).ValueGeneratedNever();

        // Carries RecipeId as well as WorkspaceId, for the same reason the ingredient groups do: a step's
        // composite foreign key then pins both, and a step cannot land in another recipe's group.
        builder.HasAlternateKey(group => new { group.WorkspaceId, group.RecipeId, group.Id })
            .HasName("AK_RecipeInstructionGroups_Workspace_Recipe_Id");

        builder.Property(group => group.Title).HasMaxLength(RecipePolicy.GroupTitleMaxLength);
        builder.Property(group => group.SortOrder).IsRequired();

        builder.HasOne<Recipe>()
            .WithMany(recipe => recipe.InstructionGroups)
            .HasForeignKey(group => new { group.WorkspaceId, group.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(group => new { group.WorkspaceId, group.RecipeId, group.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_RecipeInstructionGroups_Workspace_Recipe_Order");
    }
}
