using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Recipes.Data.Configurations;

internal sealed class TestIssueResolutionConfiguration : IEntityTypeConfiguration<TestIssueResolution>
{
    public void Configure(EntityTypeBuilder<TestIssueResolution> builder)
    {
        builder.ToTable("TestIssueResolutions", table =>
        {
            // Zero is not a disposition, for the reason CK_TestIssues_Severity_Specified gives: a resolution
            // defaulting to Fixed would claim a correction nobody made.
            table.HasCheckConstraint(
                "CK_TestIssueResolutions_Kind_Specified",
                "Kind <> 0");

            // A correction version belongs to a resolution that corrected something. Without this the two
            // columns can disagree, and a history can report that an issue was declined while pointing at the
            // version that fixed it. The same binding CK_RecipeVersions_RestoredFrom_Source enforces.
            //
            // One-directional on purpose: Fixed with no version is legitimate, because the fix may not have
            // been captured as a version yet.
            table.HasCheckConstraint(
                "CK_TestIssueResolutions_Version_Kind",
                $"ResolutionRecipeVersionId IS NULL OR Kind = {(int)TestIssueResolutionKind.Fixed}");

            // An override reason without a version to override is a reason for nothing.
            table.HasCheckConstraint(
                "CK_TestIssueResolutions_Override_RequiresVersion",
                "PredatingVersionOverrideReason IS NULL OR ResolutionRecipeVersionId IS NOT NULL");
        });

        builder.HasKey(resolution => resolution.Id);

        // Never store-generated; see TestObservationConfiguration.
        builder.Property(resolution => resolution.Id).ValueGeneratedNever();

        builder.Property(resolution => resolution.RecipeId).IsRequired();
        builder.Property(resolution => resolution.Kind).IsRequired();
        builder.Property(resolution => resolution.ResolvedByMembershipId).IsRequired();
        builder.Property(resolution => resolution.ResolvedAt).IsRequired();

        builder.Property(resolution => resolution.Notes)
            .HasMaxLength(TestRunPolicy.ResolutionNotesMaxLength);

        builder.Property(resolution => resolution.PredatingVersionOverrideReason)
            .HasMaxLength(TestRunPolicy.PredatingVersionOverrideReasonMaxLength);

        // Cascade from the issue. No Workspaces edge, for the reason TestObservationConfiguration gives.
        builder.HasOne<TestIssue>()
            .WithOne(issue => issue.Resolution)
            .HasForeignKey<TestIssueResolution>(resolution => new { resolution.WorkspaceId, resolution.RecipeId, resolution.TestIssueId })
            .HasPrincipalKey<TestIssue>(issue => new { issue.WorkspaceId, issue.RecipeId, issue.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // "The resolution version must belong to the same recipe and workspace" (TESTRUN-002), enforced as a
        // key rather than as a validator's promise. RecipeId arrived here through the issue's own foreign key
        // above, so it is the run's recipe by construction; pointing at (WorkspaceId, RecipeId, Id) therefore
        // makes a correction version from a different recipe — or a different workspace — unreferenceable
        // rather than merely rejected by whichever code path happens to run.
        //
        // Restrict, matching every other reference to an immutable version.
        builder.HasOne<RecipeVersion>()
            .WithMany()
            .HasForeignKey(resolution => new { resolution.WorkspaceId, resolution.RecipeId, resolution.ResolutionRecipeVersionId })
            .HasPrincipalKey(version => new { version.WorkspaceId, version.RecipeId, version.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // At most one resolution per issue, which is what makes resolving an already-resolved issue a
        // database refusal rather than a race the seam has to win. Uniqueness on the triple implies
        // uniqueness on (WorkspaceId, TestIssueId), because the foreign key above already forces RecipeId to
        // be the issue's — and keying it this way means the constraint doubles as the foreign key's index
        // instead of standing beside a second one.
        //
        // The one-to-one relationship above would enforce this on its own. It is spelled out because the
        // guarantee belongs to the table rather than to the relationship's cardinality: a later change that
        // made the navigation a collection would otherwise drop it silently.
        builder.HasIndex(resolution => new { resolution.WorkspaceId, resolution.RecipeId, resolution.TestIssueId })
            .IsUnique()
            .HasDatabaseName("UX_TestIssueResolutions_Workspace_Recipe_Issue");
    }
}
