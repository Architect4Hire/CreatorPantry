using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class TestIssueConfiguration : IEntityTypeConfiguration<TestIssue>
{
    public void Configure(EntityTypeBuilder<TestIssue> builder)
    {
        builder.ToTable("TestIssues", table =>
        {
            table.HasCheckConstraint(
                "CK_TestIssues_SortOrder_NonNegative",
                "SortOrder >= 0");

            // Zero is not a severity. An issue whose severity was never set would otherwise default to Minor
            // and quietly downgrade a blocking problem to a cosmetic one, which every reader after that point
            // would believe. The same refusal CK_RecipeAssetLinks_Role_Specified makes, and the opposite call
            // from TestRunOutcome, where an unset value means "not stated" and under-claims safely.
            table.HasCheckConstraint(
                "CK_TestIssues_Severity_Specified",
                "Severity <> 0");
        });

        builder.HasKey(issue => issue.Id);

        // Never store-generated; see TestObservationConfiguration for the false conflict this prevents when a
        // new issue is attached to an already-tracked run.
        builder.Property(issue => issue.Id).ValueGeneratedNever();

        // The target of TestIssueResolution's composite foreign key, carrying the recipe so the resolution's
        // correction version can be constrained to a version of this same recipe. See
        // RecipeTestRunConfiguration for the two-alternate-key arrangement this is half of.
        builder.HasAlternateKey(issue => new { issue.WorkspaceId, issue.RecipeId, issue.Id })
            .HasName("AK_TestIssues_Workspace_Recipe_Id");

        // The target of TestAttachmentLink.TestIssueId, which carries no recipe of its own.
        builder.HasAlternateKey(issue => new { issue.WorkspaceId, issue.Id })
            .HasName("AK_TestIssues_Workspace_Id");

        builder.Property(issue => issue.RecipeId).IsRequired();
        builder.Property(issue => issue.Severity).IsRequired();
        builder.Property(issue => issue.SortOrder).IsRequired();

        builder.Property(issue => issue.Title)
            .IsRequired()
            .HasMaxLength(TestRunPolicy.IssueTitleMaxLength);

        builder.Property(issue => issue.Description).HasMaxLength(TestRunPolicy.IssueDescriptionMaxLength);

        // Cascade from the run, which owns this row's lifetime. No Workspaces edge, for the reason
        // TestObservationConfiguration gives. The key includes RecipeId, so an issue's recipe is the run's
        // recipe by construction rather than by assignment.
        builder.HasOne<RecipeTestRun>()
            .WithMany(run => run.Issues)
            .HasForeignKey(issue => new { issue.WorkspaceId, issue.RecipeId, issue.RecipeTestRunId })
            .HasPrincipalKey(run => new { run.WorkspaceId, run.RecipeId, run.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict rather than Cascade or SetNull, and the choice is worth stating because it shapes the edit
        // seam. Cascade would delete an issue's evidence when the observation it came from was removed;
        // SetNull is not available at all, because WorkspaceId is part of this composite key and is not
        // nullable. So removing an observation that an issue cites is refused, and the seam has to say what
        // becomes of the issue first — which is the right conversation to be forced into.
        builder.HasOne<TestObservation>()
            .WithMany()
            .HasForeignKey(issue => new { issue.WorkspaceId, issue.TestObservationId })
            .HasPrincipalKey(observation => new { observation.WorkspaceId, observation.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Position within one run, and the index that reads a run's issues in order. Severity is included
        // rather than keyed so the per-run counts a history list shows — how many issues, how many blocking —
        // are answered from this index without a lookup into the table. An included column does not
        // participate in the uniqueness this index enforces.
        builder.HasIndex(issue => new { issue.WorkspaceId, issue.RecipeTestRunId, issue.SortOrder })
            .IsUnique()
            .IncludeProperties(issue => issue.Severity)
            .HasDatabaseName("UX_TestIssues_Workspace_Run_Order");
    }
}
