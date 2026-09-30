using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeTestRunConfiguration : IEntityTypeConfiguration<RecipeTestRun>
{
    public void Configure(EntityTypeBuilder<RecipeTestRun> builder)
    {
        // Check-constraint expressions throughout this module are written with unquoted identifiers so the
        // same text is valid on SQL Server and on the SQLite database the constraint tests run against.
        builder.ToTable("RecipeTestRuns", table =>
        {
            // Bounded at both ends, unlike CK_Recipes_Times_NonNegative, which leaves the upper bound to its
            // validator. The asymmetry is deliberate and this is the better of the two shapes: a paste of a
            // millisecond value into a field that means minutes is the common error, and catching it in the
            // database catches it on every write path rather than only the validated one.
            table.HasCheckConstraint(
                "CK_RecipeTestRuns_Times_Range",
                $"(ActualPrepTimeMinutes IS NULL OR (ActualPrepTimeMinutes >= 0 AND ActualPrepTimeMinutes <= {RecipePolicy.MaxTimeMinutes})) AND "
                    + $"(ActualCookTimeMinutes IS NULL OR (ActualCookTimeMinutes >= 0 AND ActualCookTimeMinutes <= {RecipePolicy.MaxTimeMinutes})) AND "
                    + $"(ActualRestTimeMinutes IS NULL OR (ActualRestTimeMinutes >= 0 AND ActualRestTimeMinutes <= {RecipePolicy.MaxTimeMinutes})) AND "
                    + $"(ActualTotalTimeMinutes IS NULL OR (ActualTotalTimeMinutes >= 0 AND ActualTotalTimeMinutes <= {RecipePolicy.MaxTimeMinutes}))");

            // Nothing constrains the total against the sum of the parts, here or on Recipes. A real kitchen
            // overlaps prep with cooking, so an elapsed total smaller than the sum is the correct answer and
            // not a contradiction to be refused.

            table.HasCheckConstraint(
                "CK_RecipeTestRuns_Rating_Range",
                $"Rating IS NULL OR (Rating >= {TestRunPolicy.MinRating} AND Rating <= {TestRunPolicy.MaxRating})");

            table.HasCheckConstraint(
                "CK_RecipeTestRuns_ActualYield_Positive",
                "ActualYieldQuantity IS NULL OR ActualYieldQuantity > 0");

            // The same pairing CK_Recipes_YieldUnit_Dimension enforces, for the same reason: this pins the
            // redundant dimension column away from Temperature, and the composite foreign key below pins it
            // to the referenced unit's own dimension. Neither alone is enough.
            table.HasCheckConstraint(
                "CK_RecipeTestRuns_ActualYieldUnit_Dimension",
                "(ActualYieldUnitId IS NULL AND ActualYieldUnitDimension IS NULL) OR "
                    + $"(ActualYieldUnitId IS NOT NULL AND ActualYieldUnitDimension IS NOT NULL AND ActualYieldUnitDimension <> {(int)MeasurementDimension.Temperature})");

            // A unit with nothing to measure is not a yield. The reverse is allowed: "got 10, not 12" is a
            // result a tester legitimately records with no unit at all.
            table.HasCheckConstraint(
                "CK_RecipeTestRuns_ActualYieldUnit_RequiresQuantity",
                "ActualYieldUnitId IS NULL OR ActualYieldQuantity IS NOT NULL");
        });

        builder.HasKey(run => run.Id);

        // Never store-generated; see TestObservationConfiguration. Applied to the root as well as the children
        // so the whole aggregate answers the Added-vs-Modified question the same way.
        builder.Property(run => run.Id).ValueGeneratedNever();

        // Two alternate keys, because the children point at this row for two different reasons.
        //
        // The first is the ordinary one every aggregate root here carries: including WorkspaceId in the key a
        // child points at makes a child owned by a different workspace than its run unrepresentable, rather
        // than merely rejected by WorkspaceOwnershipInterceptor at save time. Observations and attachments
        // use it.
        builder.HasAlternateKey(run => new { run.WorkspaceId, run.Id })
            .HasName("AK_RecipeTestRuns_Workspace_Id");

        // The second carries the recipe as well, and exists so that TestIssue -> TestIssueResolution can hand
        // the recipe down the aggregate. That is what lets a resolution's correction version be constrained
        // by the database to a version of *this same recipe* rather than by a validator somebody can forget
        // to call. It costs one unique index; see TestIssueResolution.ResolutionRecipeVersionId for what it
        // buys.
        builder.HasAlternateKey(run => new { run.WorkspaceId, run.RecipeId, run.Id })
            .HasName("AK_RecipeTestRuns_Workspace_Recipe_Id");

        builder.Property(run => run.RecipeId).IsRequired();

        // Required, and the point of the whole entity: a test run is evidence about an exact set of words.
        // There is no "latest" to fall back on.
        builder.Property(run => run.RecipeVersionId).IsRequired();

        builder.Property(run => run.TestedAt).IsRequired();
        builder.Property(run => run.TestedByMembershipId).IsRequired();
        builder.Property(run => run.Outcome).IsRequired();

        builder.Property(run => run.EnvironmentNotes).HasMaxLength(TestRunPolicy.EnvironmentNotesMaxLength);
        builder.Property(run => run.EquipmentNotes).HasMaxLength(TestRunPolicy.EquipmentNotesMaxLength);
        builder.Property(run => run.SummaryNotes).HasMaxLength(TestRunPolicy.SummaryNotesMaxLength);

        // The tester's own phrasing, bounded exactly as the recipe's yield text is — it is the same kind of
        // sentence, written about the result instead of the intent.
        builder.Property(run => run.ActualYieldText).HasMaxLength(RecipePolicy.YieldTextMaxLength);

        builder.Property(run => run.ActualYieldQuantity)
            .HasColumnType(QuantityFormat.DecimalColumnType);

        builder.Property(run => run.CreatedByMembershipId).IsRequired();
        builder.Property(run => run.UpdatedByMembershipId).IsRequired();
        builder.Property(run => run.CreatedAt).IsRequired();
        builder.Property(run => run.UpdatedAt).IsRequired();

        // Two people writing up one bake must not silently overwrite each other.
        builder.Property(run => run.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(run => run.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The constraint the whole design turns on. Pointing at (WorkspaceId, RecipeId, Id) rather than at
        // (WorkspaceId, Id) means the database itself rejects a run whose RecipeId disagrees with its
        // version's — which is what makes RecipeId a constrained mirror rather than a second copy of a fact
        // that can drift, the distinction Recipe.DuplicatedFromVersionId's remarks draw.
        //
        // Restrict, matching AiProposal and RecipeVersion: a version with test history cannot be removed out
        // from under it, and versions are immutable anyway. Deleting the whole workspace still reaches this
        // table, by its own cascade from Workspaces above.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(run => new { run.WorkspaceId, run.RecipeId, run.RecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // No separate foreign key to Recipes. The version already guarantees the recipe exists in this
        // workspace, so a second edge would constrain nothing and would add a cascade path into this table.

        // The other half of the yield-unit rule, exactly as RecipeConfiguration does it: pointing at the
        // alternate key (Id, Dimension) rather than the primary key alone means the database rejects an
        // actual yield expressed in degrees, because the check constraint above has already ruled Temperature
        // out of the mirrored column.
        builder.HasOne<MeasurementUnit>()
            .WithMany()
            .HasForeignKey(run => new { run.ActualYieldUnitId, run.ActualYieldUnitDimension })
            .HasPrincipalKey(unit => new { unit.Id, unit.Dimension })
            .OnDelete(DeleteBehavior.Restrict);

        // One recipe's test history, newest first — the list route's default ordering and its cursor.
        //
        // Descending with Id in the key for the reason IX_Recipes_Workspace_UpdatedAt states: an ascending
        // index cannot serve "newest first" without a sort, and a keyset cursor needs the tie-break in the
        // key or a page of twenty-five rows sorts the whole history to find them.
        //
        // TestedAt rather than CreatedAt, because testers write their notes up days later and a history
        // ordered by when somebody typed it is not a history of the cooking.
        builder.HasIndex(run => new { run.WorkspaceId, run.RecipeId, run.TestedAt, run.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_RecipeTestRuns_Workspace_Recipe_TestedAt");

        // No index for the tester, outcome or rating filters, and no included columns — the same judgement
        // RecipeConfiguration closes with. This route is already recipe-scoped, so the seek above has narrowed
        // the read to one recipe's tests before any of them is evaluated, and a recipe has tens of those, not
        // millions. "Every run against one version" is served by the foreign key's own index.
    }
}
