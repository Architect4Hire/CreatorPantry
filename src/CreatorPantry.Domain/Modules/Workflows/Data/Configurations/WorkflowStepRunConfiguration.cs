using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Workflows.Data.Configurations;

internal sealed class WorkflowStepRunConfiguration : IEntityTypeConfiguration<WorkflowStepRun>
{
    public void Configure(EntityTypeBuilder<WorkflowStepRun> builder)
    {
        const int notStarted = (int)WorkflowStepRunStatus.NotStarted;
        const int completed = (int)WorkflowStepRunStatus.Completed;
        const int skipped = (int)WorkflowStepRunStatus.Skipped;

        builder.ToTable("WorkflowStepRuns", table =>
        {
            table.HasCheckConstraint("CK_WorkflowStepRuns_Status_Range", $"Status >= {notStarted} AND Status <= {skipped}");
            table.HasCheckConstraint("CK_WorkflowStepRuns_StepKey_NotBlank", "trim(StepKey) <> ''");
            table.HasCheckConstraint("CK_WorkflowStepRuns_NotStarted_NoStart", $"Status <> {notStarted} OR StartedAt IS NULL");

            // Who and when travel together, and only with the state they describe.
            table.HasCheckConstraint(
                "CK_WorkflowStepRuns_Completed_Recorded",
                $"(Status = {completed} AND CompletedAt IS NOT NULL AND CompletedByMembershipId IS NOT NULL) OR "
                    + $"(Status <> {completed} AND CompletedAt IS NULL AND CompletedByMembershipId IS NULL)");
            table.HasCheckConstraint(
                "CK_WorkflowStepRuns_Skipped_Recorded",
                $"(Status = {skipped} AND SkippedAt IS NOT NULL AND SkippedByMembershipId IS NOT NULL) OR "
                    + $"(Status <> {skipped} AND SkippedAt IS NULL AND SkippedByMembershipId IS NULL AND SkipReason IS NULL)");
            table.HasCheckConstraint("CK_WorkflowStepRuns_SkipReason_NotBlank", "SkipReason IS NULL OR trim(SkipReason) <> ''");

            // A draft pointer is whole or absent.
            table.HasCheckConstraint(
                "CK_WorkflowStepRuns_Draft_Whole",
                "(DraftResourceKind IS NULL AND DraftResourceId IS NULL AND DraftSavedAt IS NULL) OR "
                    + "(DraftResourceKind IS NOT NULL AND DraftResourceId IS NOT NULL AND DraftSavedAt IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_WorkflowStepRuns_DraftKind_Range",
                $"DraftResourceKind IS NULL OR (DraftResourceKind >= {WorkflowResourceKinds.Lowest} "
                    + $"AND DraftResourceKind <= {WorkflowResourceKinds.Highest})");
        });

        builder.HasKey(step => step.Id);
        builder.Property(step => step.Id).ValueGeneratedNever();

        builder.Property(step => step.StepKey).IsRequired().HasMaxLength(WorkflowRunPolicy.KeyMaxLength);
        builder.Property(step => step.Status).IsRequired();
        builder.Property(step => step.SkipReason).HasMaxLength(WorkflowRunPolicy.SkipReasonMaxLength);

        // Interior for deletion: the run cascades its steps, and there is no second path from Workspaces.
        builder.HasOne<WorkflowRun>()
            .WithMany()
            .HasForeignKey(step => new { step.WorkspaceId, step.WorkflowRunId })
            .HasPrincipalKey(run => new { run.WorkspaceId, run.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(step => new { step.WorkspaceId, step.WorkflowRunId, step.StepKey })
            .IsUnique()
            .HasDatabaseName("UX_WorkflowStepRuns_Workspace_Run_StepKey");
    }
}
