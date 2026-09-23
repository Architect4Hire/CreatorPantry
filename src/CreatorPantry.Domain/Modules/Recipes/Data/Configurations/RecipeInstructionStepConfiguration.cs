using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeInstructionStepConfiguration : IEntityTypeConfiguration<RecipeInstructionStep>
{
    public void Configure(EntityTypeBuilder<RecipeInstructionStep> builder)
    {
        builder.ToTable("RecipeInstructionSteps", table =>
        {
            table.HasCheckConstraint(
                "CK_RecipeInstructionSteps_SortOrder_NonNegative",
                "SortOrder >= 0");

            table.HasCheckConstraint(
                "CK_RecipeInstructionSteps_Duration_NonNegative",
                "DurationMinutes IS NULL OR DurationMinutes >= 0");

            // A step temperature is present in all three columns or in none of them, and the unit is always a
            // temperature unit. Half of the rule; the composite foreign key below is the other half. A step
            // that says "bake at 180 g" is a food-safety-adjacent fact recorded wrongly, so it is refused by
            // the database rather than by whoever happens to read it next.
            table.HasCheckConstraint(
                "CK_RecipeInstructionSteps_Temperature_Dimension",
                "(TemperatureValue IS NULL AND TemperatureUnitId IS NULL AND TemperatureUnitDimension IS NULL) OR "
                    + "(TemperatureValue IS NOT NULL AND TemperatureUnitId IS NOT NULL AND "
                    + $"TemperatureUnitDimension = {(int)MeasurementDimension.Temperature})");
        });

        builder.HasKey(step => step.Id);

        // See RecipeInstructionGroupConfiguration's Id configuration for why this matters: without it, a new
        // step attached to an existing group during an edit is misread as an update to a row that does not
        // exist.
        builder.Property(step => step.Id).ValueGeneratedNever();

        builder.Property(step => step.Text)
            .IsRequired()
            .HasMaxLength(RecipePolicy.StepTextMaxLength);

        builder.Property(step => step.Note).HasMaxLength(RecipePolicy.NoteMaxLength);
        builder.Property(step => step.SortOrder).IsRequired();

        builder.Property(step => step.TemperatureValue)
            .HasColumnType(QuantityFormat.DecimalColumnType);

        builder.HasOne<RecipeInstructionGroup>()
            .WithMany(group => group.Steps)
            .HasForeignKey(step => new { step.WorkspaceId, step.RecipeId, step.RecipeInstructionGroupId })
            .HasPrincipalKey(group => new { group.WorkspaceId, group.RecipeId, group.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<CookingTechnique>()
            .WithMany()
            .HasForeignKey(step => step.TechniqueId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(step => new { step.TemperatureUnitId, step.TemperatureUnitDimension })
            .HasPrincipalKey(unit => new { unit.Id, unit.Dimension })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(step => new { step.WorkspaceId, step.RecipeInstructionGroupId, step.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_RecipeInstructionSteps_Workspace_Group_Order");

        // Loading one recipe's whole method is served by the index EF creates for the composite foreign key
        // above; (WorkspaceId, RecipeId) is its leading prefix. See RecipeIngredientConfiguration.
    }
}
