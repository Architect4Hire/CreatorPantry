using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeIngredientConfiguration : IEntityTypeConfiguration<RecipeIngredient>
{
    public void Configure(EntityTypeBuilder<RecipeIngredient> builder)
    {
        builder.ToTable("RecipeIngredients", table =>
        {
            table.HasCheckConstraint(
                "CK_RecipeIngredients_SortOrder_NonNegative",
                "SortOrder >= 0");

            table.HasCheckConstraint(
                "CK_RecipeIngredients_Quantity_Positive",
                "(Quantity IS NULL OR Quantity > 0) AND (QuantityUpper IS NULL OR QuantityUpper > 0)");

            // A range needs both ends and has to run upward. "2 to 3" with the 2 missing is not a range, it
            // is a parse that went wrong, and storing it would put an invented low end into every scaling.
            table.HasCheckConstraint(
                "CK_RecipeIngredients_Quantity_Range",
                "QuantityUpper IS NULL OR (Quantity IS NOT NULL AND QuantityUpper > Quantity)");

            // An ingredient quantity is never a temperature. See RecipeIngredient.MeasurementUnitDimension.
            table.HasCheckConstraint(
                "CK_RecipeIngredients_Unit_Dimension",
                "(MeasurementUnitId IS NULL AND MeasurementUnitDimension IS NULL) OR "
                    + $"(MeasurementUnitId IS NOT NULL AND MeasurementUnitDimension IS NOT NULL AND MeasurementUnitDimension <> {(int)MeasurementDimension.Temperature})");

            // Matched means matched to something, and anything else means matched to nothing. Without this the
            // two can disagree, and an interface then has to guess which of them to believe.
            table.HasCheckConstraint(
                "CK_RecipeIngredients_Match_Status",
                $"(IngredientId IS NOT NULL AND MatchStatus = {(int)IngredientMatchStatus.Matched}) OR "
                    + $"(IngredientId IS NULL AND MatchStatus <> {(int)IngredientMatchStatus.Matched})");
        });

        builder.HasKey(ingredient => ingredient.Id);

        // See RecipeIngredientGroupConfiguration's Id configuration for why this matters: without it, a new
        // line attached to an existing group during an edit is misread as an update to a row that does not
        // exist.
        builder.Property(ingredient => ingredient.Id).ValueGeneratedNever();

        builder.Property(ingredient => ingredient.DisplayText)
            .IsRequired()
            .HasMaxLength(RecipePolicy.LineTextMaxLength);

        builder.Property(ingredient => ingredient.IngredientNameText)
            .HasMaxLength(RecipePolicy.IngredientNameTextMaxLength);

        builder.Property(ingredient => ingredient.PreparationNote).HasMaxLength(RecipePolicy.NoteMaxLength);

        builder.Property(ingredient => ingredient.Quantity)
            .HasColumnType(QuantityFormat.DecimalColumnType);

        builder.Property(ingredient => ingredient.QuantityUpper)
            .HasColumnType(QuantityFormat.DecimalColumnType);

        builder.Property(ingredient => ingredient.SortOrder).IsRequired();
        builder.Property(ingredient => ingredient.IsOptional).IsRequired();
        builder.Property(ingredient => ingredient.MatchStatus).IsRequired();
        builder.Property(ingredient => ingredient.ScalingBehavior).IsRequired();

        // Three columns in the key, so neither the workspace nor the recipe can disagree with the group's.
        builder.HasOne<RecipeIngredientGroup>()
            .WithMany(group => group.Ingredients)
            .HasForeignKey(ingredient => new { ingredient.WorkspaceId, ingredient.RecipeId, ingredient.RecipeIngredientGroupId })
            .HasPrincipalKey(group => new { group.WorkspaceId, group.RecipeId, group.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Retiring an ingredient must never delete the recipe lines that reference it; the catalogue retires
        // rows with IsActive precisely so existing recipes keep resolving.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(ingredient => ingredient.IngredientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(ingredient => new { ingredient.MeasurementUnitId, ingredient.MeasurementUnitDimension })
            .HasPrincipalKey(unit => new { unit.Id, unit.Dimension })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(ingredient => new { ingredient.WorkspaceId, ingredient.RecipeIngredientGroupId, ingredient.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_RecipeIngredients_Workspace_Group_Order");

        // Loading one recipe's whole ingredient list without going group by group is served by the index EF
        // creates for the composite foreign key above, (WorkspaceId, RecipeId, RecipeIngredientGroupId) —
        // (WorkspaceId, RecipeId) is its leading prefix. A second index on those two columns would be paid
        // for on every write and read on none.
        //
        // Declared here rather than left to that convention for one reason: MatchStatus is included. A recipe
        // search asks "does this recipe have an unresolved ingredient line" of every row it returns, and
        // without the included column that EXISTS leaves the index for a lookup per line — on a recipe whose
        // lines are all matched, every line, because there is no early exit to take. The key is identical to
        // the one the convention would have produced, and the name is too, so this replaces it rather than
        // adding a second copy.
        builder.HasIndex(ingredient => new { ingredient.WorkspaceId, ingredient.RecipeId, ingredient.RecipeIngredientGroupId })
            .IncludeProperties(ingredient => ingredient.MatchStatus)
            .HasDatabaseName("IX_RecipeIngredients_WorkspaceId_RecipeId_RecipeIngredientGroupId");

        // "Which of my recipes use buttermilk." Workspace-leading, so the answer is scoped before the
        // ingredient is even considered.
        builder.HasIndex(ingredient => new { ingredient.WorkspaceId, ingredient.IngredientId })
            .HasDatabaseName("IX_RecipeIngredients_Workspace_Ingredient");
    }
}
