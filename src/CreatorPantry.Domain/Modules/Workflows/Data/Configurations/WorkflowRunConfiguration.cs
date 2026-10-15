using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Workflows.Data.Configurations;

internal sealed class WorkflowRunConfiguration : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> builder)
    {
        const int inProgress = (int)WorkflowRunStatus.InProgress;
        const int completed = (int)WorkflowRunStatus.Completed;
        const int abandoned = (int)WorkflowRunStatus.Abandoned;

        builder.ToTable("WorkflowRuns", table =>
        {
            table.HasCheckConstraint("CK_WorkflowRuns_Status_Range", $"Status >= {inProgress} AND Status <= {abandoned}");
            table.HasCheckConstraint("CK_WorkflowRuns_DefinitionKey_NotBlank", "trim(DefinitionKey) <> ''");
            table.HasCheckConstraint("CK_WorkflowRuns_DefinitionVersion_Positive", "DefinitionVersion >= 1");
            table.HasCheckConstraint("CK_WorkflowRuns_Title_NotBlank", "trim(Title) <> ''");
            table.HasCheckConstraint("CK_WorkflowRuns_StepCount_Positive", "StepCount >= 1");
            table.HasCheckConstraint(
                "CK_WorkflowRuns_Progress_Bounded",
                "CompletedStepCount >= 0 AND SkippedStepCount >= 0 AND CompletedStepCount + SkippedStepCount <= StepCount");

            // A run in progress is on some step; a completed one is on none. An abandoned one keeps the step it
            // was left on, which is the honest answer to "how far did it get".
            table.HasCheckConstraint(
                "CK_WorkflowRuns_CurrentStep_Status",
                $"(Status = {inProgress} AND CurrentStepKey IS NOT NULL) OR "
                    + $"(Status = {completed} AND CurrentStepKey IS NULL) OR "
                    + $"Status = {abandoned}");
            table.HasCheckConstraint("CK_WorkflowRuns_CurrentStep_NotBlank", "CurrentStepKey IS NULL OR trim(CurrentStepKey) <> ''");

            table.HasCheckConstraint(
                "CK_WorkflowRuns_CompletedAt_Status",
                $"(Status = {completed} AND CompletedAt IS NOT NULL) OR (Status <> {completed} AND CompletedAt IS NULL)");
            // Only a run stopped by starting over points at its replacement.
            table.HasCheckConstraint(
                "CK_WorkflowRuns_ReplacedBy_Abandoned",
                $"ReplacedByRunId IS NULL OR Status = {abandoned}");
            table.HasCheckConstraint(
                "CK_WorkflowRuns_AbandonedAt_Status",
                $"(Status = {abandoned} AND AbandonedAt IS NOT NULL) OR (Status <> {abandoned} AND AbandonedAt IS NULL)");
            // Who stopped it and why belong to a stopped run only. Neither is required of one: a run stopped before
            // they were recorded has neither, and a reason is the creator's to give or not.
            table.HasCheckConstraint(
                "CK_WorkflowRuns_AbandonedBy_Abandoned",
                $"AbandonedByMembershipId IS NULL OR Status = {abandoned}");
            table.HasCheckConstraint(
                "CK_WorkflowRuns_AbandonReason_Abandoned",
                $"AbandonReason IS NULL OR (Status = {abandoned} AND trim(AbandonReason) <> '')");

            // Relative to the workspace, so never rooted, never a scheme and never climbing out of it: a ':' would
            // let a stored route become somewhere else entirely once followed, a '\' is read by browsers as '/'
            // (so '\\host' is '//host'), and '..' leaves the workspace's own routes.
            table.HasCheckConstraint(
                "CK_WorkflowRuns_LastRoute_Relative",
                "LastRoute IS NULL OR (trim(LastRoute) <> '' AND LastRoute NOT LIKE '/%' AND LastRoute NOT LIKE '%:%' "
                    + @"AND LastRoute NOT LIKE '%\%' AND LastRoute NOT LIKE '%..%')");
        });

        builder.HasKey(run => run.Id);
        builder.Property(run => run.Id).ValueGeneratedNever();

        // Target of the step and link composite keys, so neither can hang off another workspace's run.
        builder.HasAlternateKey(run => new { run.WorkspaceId, run.Id })
            .HasName("AK_WorkflowRuns_Workspace_Id");

        builder.Property(run => run.DefinitionKey).IsRequired().HasMaxLength(WorkflowRunPolicy.KeyMaxLength);
        builder.Property(run => run.DefinitionVersion).IsRequired();
        builder.Property(run => run.Title).IsRequired().HasMaxLength(WorkflowRunPolicy.TitleMaxLength);
        builder.Property(run => run.Status).IsRequired();
        builder.Property(run => run.CurrentStepKey).HasMaxLength(WorkflowRunPolicy.KeyMaxLength);
        builder.Property(run => run.StepCount).IsRequired();
        builder.Property(run => run.CompletedStepCount).IsRequired();
        builder.Property(run => run.SkippedStepCount).IsRequired();
        builder.Property(run => run.LastRoute).HasMaxLength(WorkflowRunPolicy.LastRouteMaxLength);
        builder.Property(run => run.AbandonReason).HasMaxLength(WorkflowRunPolicy.AbandonReasonMaxLength);
        builder.Property(run => run.CreatedByMembershipId).IsRequired();
        builder.Property(run => run.StartedAt).IsRequired();
        builder.Property(run => run.UpdatedAt).IsRequired();
        builder.Property(run => run.RowVersion).IsRowVersion();

        // The run's one edge to the workspace, and so the one cascade path to its steps and links.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(run => run.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every run whatever its state, most recently touched first: the list's "all" view (13A.11).
        builder.HasIndex(run => new { run.WorkspaceId, run.UpdatedAt })
            .HasDatabaseName("IX_WorkflowRuns_Workspace_Updated");

        // The hub: a workspace's runs by state, most recently touched first (13A.11).
        builder.HasIndex(run => new { run.WorkspaceId, run.Status, run.UpdatedAt })
            .HasDatabaseName("IX_WorkflowRuns_Workspace_Status_Updated");

        builder.HasIndex(run => new { run.WorkspaceId, run.CreatedByMembershipId, run.Status, run.UpdatedAt })
            .HasDatabaseName("IX_WorkflowRuns_Workspace_Creator_Status_Updated");

        // Most runs are no one's in particular, so only the assigned ones are indexed.
        builder.HasIndex(run => new { run.WorkspaceId, run.AssignedMembershipId, run.Status, run.UpdatedAt })
            .HasDatabaseName("IX_WorkflowRuns_Workspace_Assignee_Status_Updated")
            .HasFilter("AssignedMembershipId IS NOT NULL");

        // The run a replacement was started over from.
        builder.HasIndex(run => new { run.WorkspaceId, run.ReplacedByRunId })
            .HasDatabaseName("IX_WorkflowRuns_Workspace_ReplacedBy")
            .HasFilter("ReplacedByRunId IS NOT NULL");

        // Which runs are pinned to a version, before anyone retires it.
        builder.HasIndex(run => new { run.WorkspaceId, run.DefinitionKey, run.DefinitionVersion })
            .HasDatabaseName("IX_WorkflowRuns_Workspace_Definition_Version");
    }
}
