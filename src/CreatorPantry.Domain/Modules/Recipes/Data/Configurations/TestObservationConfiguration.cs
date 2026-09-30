using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class TestObservationConfiguration : IEntityTypeConfiguration<TestObservation>
{
    public void Configure(EntityTypeBuilder<TestObservation> builder)
    {
        builder.ToTable("TestObservations", table =>
        {
            table.HasCheckConstraint(
                "CK_TestObservations_SortOrder_NonNegative",
                "SortOrder >= 0");

            // No constraint on Kind. Unspecified is a legitimate value here — a tester is not obliged to file
            // every note under a heading — which is exactly why TestIssues constrains its own enum and this
            // does not.
        });

        builder.HasKey(observation => observation.Id);

        // Business always assigns this itself (Guid.NewGuid()); it is never store-generated. The same line
        // RecipeInstructionGroupConfiguration carries, for the same reason and after the same failure: without
        // it, EF's default convention for a Guid key infers Added-vs-Modified from whether the value looks
        // "already assigned" — harmless for a brand-new test, where the whole graph is added together, but
        // wrong the moment a new note is attached to an *already-tracked* run (an edit). EF marks the entity
        // Modified instead of Added, the resulting UPDATE matches no row, and the edit is refused as a
        // concurrency conflict that never happened.
        builder.Property(observation => observation.Id).ValueGeneratedNever();

        // The target of TestIssue.TestObservationId's composite foreign key. Workspace-leading, so an issue
        // cannot cite another workspace's note.
        builder.HasAlternateKey(observation => new { observation.WorkspaceId, observation.Id })
            .HasName("AK_TestObservations_Workspace_Id");

        builder.Property(observation => observation.Kind).IsRequired();
        builder.Property(observation => observation.SortOrder).IsRequired();

        builder.Property(observation => observation.Text)
            .IsRequired()
            .HasMaxLength(TestRunPolicy.ObservationTextMaxLength);

        // Cascade: an observation has no life apart from the run it was made during. No foreign key to
        // Workspaces — this table reaches Workspaces through the run, and a second edge would give SQL Server
        // two cascade paths here and it refuses the DDL outright. SQLite does not enforce that rule, so it is
        // only visible once the migration runs against a real database (see AiProposalConfiguration).
        builder.HasOne<RecipeTestRun>()
            .WithMany(run => run.Observations)
            .HasForeignKey(observation => new { observation.WorkspaceId, observation.RecipeTestRunId })
            .HasPrincipalKey(run => new { run.WorkspaceId, run.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Position within one run, and the index that reads a run's notes in order.
        builder.HasIndex(observation => new { observation.WorkspaceId, observation.RecipeTestRunId, observation.SortOrder })
            .IsUnique()
            .HasDatabaseName("UX_TestObservations_Workspace_Run_Order");
    }
}
