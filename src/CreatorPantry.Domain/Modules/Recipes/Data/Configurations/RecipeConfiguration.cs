using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        // Check-constraint expressions throughout this module are written with unquoted identifiers so the
        // same text is valid on SQL Server and on the SQLite database the constraint tests run against.
        builder.ToTable("Recipes", table =>
        {
            table.HasCheckConstraint(
                "CK_Recipes_Times_NonNegative",
                "(PrepTimeMinutes IS NULL OR PrepTimeMinutes >= 0) AND "
                    + "(CookTimeMinutes IS NULL OR CookTimeMinutes >= 0) AND "
                    + "(RestTimeMinutes IS NULL OR RestTimeMinutes >= 0) AND "
                    + "(TotalTimeMinutes IS NULL OR TotalTimeMinutes >= 0)");

            table.HasCheckConstraint(
                "CK_Recipes_Yield_Positive",
                "YieldQuantity IS NULL OR YieldQuantity > 0");

            // Half of "a yield unit is a unit you can be served in": this pins the redundant dimension column
            // away from Temperature, and the composite foreign key below pins it to the referenced unit's own
            // dimension. Neither alone is enough.
            table.HasCheckConstraint(
                "CK_Recipes_YieldUnit_Dimension",
                "(YieldUnitId IS NULL AND YieldUnitDimension IS NULL) OR "
                    + $"(YieldUnitId IS NOT NULL AND YieldUnitDimension IS NOT NULL AND YieldUnitDimension <> {(int)MeasurementDimension.Temperature})");

            // A unit with nothing to measure is not a yield. The reverse is allowed: "makes 12" with no unit
            // is a yield a creator legitimately writes.
            table.HasCheckConstraint(
                "CK_Recipes_YieldUnit_RequiresQuantity",
                "YieldUnitId IS NULL OR YieldQuantity IS NOT NULL");
        });

        builder.HasKey(recipe => recipe.Id);

        // The target of every child's composite foreign key. Including WorkspaceId in the key the children
        // point at is what makes a child owned by a different workspace than its recipe unrepresentable,
        // rather than merely rejected by WorkspaceOwnershipInterceptor at save time.
        builder.HasAlternateKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .HasName("AK_Recipes_Workspace_Id");

        builder.Property(recipe => recipe.Title)
            .IsRequired()
            .HasMaxLength(RecipePolicy.TitleMaxLength);

        builder.Property(recipe => recipe.Description).HasMaxLength(RecipePolicy.DescriptionMaxLength);
        builder.Property(recipe => recipe.Headnote).HasMaxLength(RecipePolicy.LongTextMaxLength);
        builder.Property(recipe => recipe.Notes).HasMaxLength(RecipePolicy.LongTextMaxLength);
        builder.Property(recipe => recipe.StorageNotes).HasMaxLength(RecipePolicy.LongTextMaxLength);
        builder.Property(recipe => recipe.AttributionText).HasMaxLength(RecipePolicy.AttributionMaxLength);
        builder.Property(recipe => recipe.SourceUrl).HasMaxLength(RecipePolicy.UrlMaxLength);
        builder.Property(recipe => recipe.YieldText).HasMaxLength(RecipePolicy.YieldTextMaxLength);

        builder.Property(recipe => recipe.YieldQuantity)
            .HasColumnType(QuantityFormat.DecimalColumnType);

        builder.Property(recipe => recipe.Status).IsRequired();
        builder.Property(recipe => recipe.CreatedByMembershipId).IsRequired();
        builder.Property(recipe => recipe.UpdatedByMembershipId).IsRequired();
        builder.Property(recipe => recipe.CreatedAt).IsRequired();
        builder.Property(recipe => recipe.UpdatedAt).IsRequired();

        // Collaborative editing: two creators on one recipe is the ordinary case, and the second writer must
        // get a recoverable conflict rather than silently erasing the first one's work.
        builder.Property(recipe => recipe.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(recipe => recipe.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // CreatedByMembershipId and UpdatedByMembershipId are deliberately not foreign keys, matching
        // AuditLog.ActorUserId: authorship is a historical fact that must survive a member leaving the
        // workspace, and a real FK would also give SQL Server a second cascade path into this table
        // (Workspace -> WorkspaceMembership alongside Workspace -> Recipe). The cost is that the composite
        // key trick protecting every other reference here is unavailable, so nothing in the database stops
        // these naming a membership in another workspace — the write seam owns that, and must take the
        // actor from IWorkspaceContext.MembershipId rather than from any request field.

        // Retiring a vocabulary entry never deletes the recipes described by it — every one of these tables
        // retires rows with IsActive and documents that existing recipes must keep resolving.
        builder.HasOne<Cuisine>()
            .WithMany()
            .HasForeignKey(recipe => recipe.CuisineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Course>()
            .WithMany()
            .HasForeignKey(recipe => recipe.CourseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CookingTechnique>()
            .WithMany()
            .HasForeignKey(recipe => recipe.PrimaryTechniqueId)
            .OnDelete(DeleteBehavior.Restrict);

        // The other half of the yield-unit rule: pointing at the alternate key (Id, Dimension) rather than
        // the primary key alone means the database itself rejects a yield expressed in degrees, because the
        // check constraint above has already ruled Temperature out of this column.
        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(recipe => new { recipe.YieldUnitId, recipe.YieldUnitDimension })
            .HasPrincipalKey(unit => new { unit.Id, unit.Dimension })
            .OnDelete(DeleteBehavior.Restrict);

        // The recipe list: one workspace's recipes in a status, most recently touched first.
        builder.HasIndex(recipe => new { recipe.WorkspaceId, recipe.Status, recipe.UpdatedAt })
            .HasDatabaseName("IX_Recipes_Workspace_Status_UpdatedAt");

        // Alphabetical listing and title lookup within a workspace.
        builder.HasIndex(recipe => new { recipe.WorkspaceId, recipe.Title })
            .HasDatabaseName("IX_Recipes_Workspace_Title");
    }
}
