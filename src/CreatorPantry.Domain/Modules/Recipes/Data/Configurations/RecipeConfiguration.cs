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

        // Duplication lineage, within one workspace. Composite for the reason every other reference here is:
        // an id alone could name a version of another workspace's recipe, and pointing at
        // AK_RecipeVersions_Workspace_Id makes that unrepresentable rather than merely refused by the write
        // seam.
        //
        // Restrict, and the cost is worth stating plainly: a recipe that has been duplicated cannot be
        // deleted while the copy still records where it came from. That is already true of any recipe with
        // history — RecipeVersion's own reference to Recipe is Restrict for the same reason — so this adds no
        // new class of problem, and whatever operation eventually deletes a recipe has to decide what becomes
        // of its lineage alongside what becomes of its versions. Deleting the workspace still reaches both
        // tables by their own cascades.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(recipe => new { recipe.WorkspaceId, recipe.DuplicatedFromVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The other half of the yield-unit rule: pointing at the alternate key (Id, Dimension) rather than
        // the primary key alone means the database itself rejects a yield expressed in degrees, because the
        // check constraint above has already ruled Temperature out of this column.
        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(recipe => new { recipe.YieldUnitId, recipe.YieldUnitDimension })
            .HasPrincipalKey(unit => new { unit.Id, unit.Dimension })
            .OnDelete(DeleteBehavior.Restrict);

        // The library's default ordering: one workspace's recipes, most recently edited first.
        //
        // Descending, and with Id in the key, because both are what RecipeSearchSort.RecentlyUpdated orders by.
        // An ascending index cannot serve "newest first" without a sort, and leaving the tie-break to the
        // clustered key's uniquifier would leave ORDER BY Id DESC unserved — so a page would sort the
        // workspace's whole recipe table to return twenty-five rows.
        builder.HasIndex(recipe => new { recipe.WorkspaceId, recipe.UpdatedAt, recipe.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("IX_Recipes_Workspace_UpdatedAt");

        // The same ordering with a status filter narrowing it first.
        builder.HasIndex(recipe => new { recipe.WorkspaceId, recipe.Status, recipe.UpdatedAt, recipe.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_Recipes_Workspace_Status_UpdatedAt");

        // Alphabetical listing and title lookup within a workspace — RecipeSearchSort.Title, tie-break
        // included. Titles are deliberately not unique: two of a creator's recipes may share one, which is
        // exactly why the tie-break has to be part of the key.
        builder.HasIndex(recipe => new { recipe.WorkspaceId, recipe.Title, recipe.Id })
            .HasDatabaseName("IX_Recipes_Workspace_Title");

        // No index per filter, deliberately. Cuisine, course, author and the date bounds are residual
        // predicates evaluated after one of the seeks above has already narrowed the read to this workspace.
        // One creator's library is hundreds of rows, not millions; an index for each filter combination would
        // be paid for on every recipe edit to save nothing measurable on a read.
        //
        // No included columns on these three either. At that row count the lookup into the clustered index is
        // cheaper than maintaining a wide copy of every summary column on every write. If measurement later
        // disagrees, adding includes is a change that breaks nothing.
    }
}
