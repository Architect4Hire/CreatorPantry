using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class RecipeStatusTransitionConfiguration : IEntityTypeConfiguration<RecipeStatusTransition>
{
    public void Configure(EntityTypeBuilder<RecipeStatusTransition> builder)
    {
        // Check-constraint expressions are written with unquoted identifiers so the same text is valid on SQL
        // Server and on the SQLite database the constraint tests run against.
        builder.ToTable("RecipeStatusTransitions", table =>
        {
            // A move to the state the recipe was already in is not a move. Nothing in
            // RecipeStatusTransitions.All produces one, and this is what keeps that true of the table however
            // a row arrives — a history containing Approved -> Approved would report an approval that
            // happened twice.
            table.HasCheckConstraint(
                "CK_RecipeStatusTransitions_States_Differ",
                "FromStatus <> ToStatus");

            // The three approval columns travel together or not at all. Half-recorded provenance is the
            // failure CK_RecipeVersions_RestoredFrom_Source exists to prevent, and it is worse here: a row
            // claiming a readiness rule-set version with no version it evaluated would say an approval was
            // gated without saying on what.
            //
            // Written against ToStatus rather than as a bare all-or-nothing, because which move carries them
            // is itself the rule: only the approval does.
            table.HasCheckConstraint(
                "CK_RecipeStatusTransitions_Approval_Columns",
                $"(ToStatus = {(int)RecipeStatus.Approved} AND ReadinessRuleSetVersion IS NOT NULL "
                    + "AND ReadinessEvaluatedVersionId IS NOT NULL AND CreatedVersionId IS NOT NULL) OR "
                    + $"(ToStatus <> {(int)RecipeStatus.Approved} AND ReadinessRuleSetVersion IS NULL "
                    + "AND ReadinessEvaluatedVersionId IS NULL AND CreatedVersionId IS NULL)");
        });

        builder.HasKey(transition => transition.Id);

        // Never store-generated; see TestObservationConfiguration.
        builder.Property(transition => transition.Id).ValueGeneratedNever();

        builder.Property(transition => transition.RecipeId).IsRequired();
        builder.Property(transition => transition.FromStatus).IsRequired();
        builder.Property(transition => transition.ToStatus).IsRequired();
        builder.Property(transition => transition.ActorMembershipId).IsRequired();
        builder.Property(transition => transition.OccurredAt).IsRequired();

        builder.Property(transition => transition.Reason)
            .HasMaxLength(RecipePolicy.TransitionReasonMaxLength);

        // Required, and short: a semantic version, not prose. Unbounded it would be nvarchar(max) for a
        // column that never holds more than "10.20.30".
        builder.Property(transition => transition.MachineVersion)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(transition => transition.ReadinessRuleSetVersion)
            .HasMaxLength(32);

        // No RowVersion. The row is immutable, so there is no second write for a token to guard — the
        // concurrency check that matters happened on the recipe, before this was written.

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(transition => transition.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, exactly as RecipeVersion does it and for the same reason: this is the constraint that
        // makes a recipe's editorial history outlive the recipe's ordinary life, so removing a recipe that has
        // one has to be a deliberate operation that says what becomes of it. Deleting the whole workspace
        // still reaches both tables, by their own cascades from Workspaces — which is also why this edge is
        // not Cascade: two cascade paths into this table is the shape SQL Server refuses outright.
        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(transition => new { transition.WorkspaceId, transition.RecipeId })
            .HasPrincipalKey(recipe => new { recipe.WorkspaceId, recipe.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The version this approval captured, constrained to a version of *this* recipe. Pointing at
        // (WorkspaceId, RecipeId, Id) rather than (WorkspaceId, Id) is what makes an approval naming another
        // recipe's version unrepresentable rather than merely refused by whichever code path happens to run —
        // the same argument TestIssueResolution.ResolutionRecipeVersionId makes.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(transition => new
            {
                transition.WorkspaceId,
                transition.RecipeId,
                transition.CreatedVersionId,
            })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The version readiness was evaluated of. A second relationship to the same entity type rather than
        // one with two meanings, configured identically — see RecipeVersionConfiguration's two lineage edges
        // for the precedent, and RecipeStatusTransition.ReadinessEvaluatedVersionId for why these two ids are
        // not the same column.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(transition => new
            {
                transition.WorkspaceId,
                transition.RecipeId,
                transition.ReadinessEvaluatedVersionId,
            })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One recipe's editorial history, newest first — what the history read and 10.7a's response need.
        //
        // Descending with Id in the key for the reason IX_RecipeTestRuns_Workspace_Recipe_TestedAt states: an
        // ascending index cannot serve "newest first" without a sort, and two transitions written in the same
        // tick need the tie-break in the key to have one total order at all.
        builder.HasIndex(transition => new { transition.WorkspaceId, transition.RecipeId, transition.OccurredAt, transition.Id })
            .IsDescending(false, false, true, true)
            .HasDatabaseName("IX_RecipeStatusTransitions_Workspace_Recipe_OccurredAt");
    }
}
