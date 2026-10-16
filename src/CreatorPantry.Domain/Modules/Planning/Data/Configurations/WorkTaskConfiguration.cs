using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Planning.Data.Configurations;

internal sealed class WorkTaskConfiguration : IEntityTypeConfiguration<WorkTask>
{
    public void Configure(EntityTypeBuilder<WorkTask> builder)
    {
        const int backlog = (int)WorkTaskStatus.Backlog;
        const int waiting = (int)WorkTaskStatus.Waiting;
        const int done = (int)WorkTaskStatus.Done;

        builder.ToTable("WorkTasks", table =>
        {
            table.HasCheckConstraint("CK_WorkTasks_Title_NotBlank", "trim(Title) <> ''");
            table.HasCheckConstraint("CK_WorkTasks_Description_NotBlank", "Description IS NULL OR trim(Description) <> ''");

            // Stored as integers, and an enum column accepts any integer. Zero is neither.
            table.HasCheckConstraint("CK_WorkTasks_Status_Range", $"Status >= {backlog} AND Status <= {done}");
            table.HasCheckConstraint(
                "CK_WorkTasks_Priority_Range",
                $"Priority >= {(int)WorkTaskPriority.Low} AND Priority <= {(int)WorkTaskPriority.High}");

            // When, who and from-where travel together, and only with Done. A task is never completed from Done.
            table.HasCheckConstraint(
                "CK_WorkTasks_Completed_Recorded",
                $"(Status = {done} AND CompletedAt IS NOT NULL AND CompletedByMembershipId IS NOT NULL AND CompletedFromStatus IS NOT NULL) OR "
                    + $"(Status <> {done} AND CompletedAt IS NULL AND CompletedByMembershipId IS NULL AND CompletedFromStatus IS NULL)");
            table.HasCheckConstraint(
                "CK_WorkTasks_CompletedFrom_Range",
                $"CompletedFromStatus IS NULL OR (CompletedFromStatus >= {backlog} AND CompletedFromStatus <= {waiting})");

            // A day is in a week, and not before its first day. That it is not after the last is the write seam's:
            // a comparison is portable across both engines, date arithmetic is not.
            table.HasCheckConstraint(
                "CK_WorkTasks_ScheduledDate_InWeek",
                "ScheduledDate IS NULL OR (PlanningWeekStart IS NOT NULL AND ScheduledDate >= PlanningWeekStart)");

            table.HasCheckConstraint(
                "CK_WorkTasks_Estimate_Range",
                $"EstimateMinutes IS NULL OR (EstimateMinutes >= 1 AND EstimateMinutes <= {WorkTaskPolicy.EstimateMaxMinutes})");

            // A template pointer is whole or absent: an occurrence of nothing, or a template with no occurrence,
            // would slip past the one-task-per-occurrence index.
            table.HasCheckConstraint(
                "CK_WorkTasks_Recurrence_Whole",
                "(WorkTaskTemplateId IS NULL AND RecurrenceDate IS NULL) OR "
                    + "(WorkTaskTemplateId IS NOT NULL AND RecurrenceDate IS NOT NULL)");

            // A step is a step of some run.
            table.HasCheckConstraint(
                "CK_WorkTasks_WorkflowStep_WithRun",
                "WorkflowStepKey IS NULL OR (WorkflowRunId IS NOT NULL AND trim(WorkflowStepKey) <> '')");

            table.HasCheckConstraint(
                "CK_WorkTasks_Origin_Range",
                $"Origin >= {(int)WorkTaskOrigin.Capture} AND Origin <= {(int)WorkTaskOrigin.WorkflowStep}");

            // A step's task is the task for a step. Without one it would be the step's task for nothing, and SQL
            // Server's unique index would treat two such rows as the same.
            table.HasCheckConstraint(
                "CK_WorkTasks_WorkflowOrigin_WithStep",
                $"Origin <> {(int)WorkTaskOrigin.WorkflowStep} OR (WorkflowRunId IS NOT NULL AND WorkflowStepKey IS NOT NULL)");
        });

        builder.HasKey(task => task.Id);

        // Set by the application, never the store.
        builder.Property(task => task.Id).ValueGeneratedNever();

        // Target of the label composite key, so a label cannot hang off another workspace's task.
        builder.HasAlternateKey(task => new { task.WorkspaceId, task.Id })
            .HasName("AK_WorkTasks_Workspace_Id");

        builder.Property(task => task.OwnerMembershipId).IsRequired();
        builder.Property(task => task.AssigneeMembershipId).IsRequired();
        builder.Property(task => task.Title).IsRequired().HasMaxLength(WorkTaskPolicy.TitleMaxLength);
        builder.Property(task => task.Description).HasMaxLength(WorkTaskPolicy.DescriptionMaxLength);
        builder.Property(task => task.Status).IsRequired();
        builder.Property(task => task.Priority).IsRequired();
        builder.Property(task => task.SortRank).IsRequired();

        // A default, so every task captured before 13A.29 is what it always was. The sentinel is stated so a task
        // that reaches an insert without one takes the store's default rather than a zero the range check refuses.
        builder.Property(task => task.Origin)
            .IsRequired()
            .HasDefaultValue(WorkTaskOrigin.Capture)
            .HasSentinel(default(WorkTaskOrigin));
        builder.Property(task => task.WorkflowStepKey).HasMaxLength(WorkTaskPolicy.WorkflowStepKeyMaxLength);
        builder.Property(task => task.CreatedAt).IsRequired();
        builder.Property(task => task.UpdatedAt).IsRequired();
        builder.Property(task => task.RowVersion).IsRowVersion();

        // The task's one edge to the workspace, and so the one cascade path to its labels. Every link to another
        // module is a pointer and not a foreign key — see WorkTask.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(task => task.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The routine an occurrence came from (13A.30): this module's own record, so a real key, workspace-paired
        // so a task cannot claim another workspace's template, and restricted — a template is paused or ended,
        // never deleted, and the workspace already cascades to both.
        builder.HasOne<WorkTaskTemplate>()
            .WithMany()
            .HasForeignKey(task => new { task.WorkspaceId, task.WorkTaskTemplateId })
            .HasPrincipalKey(template => new { template.WorkspaceId, template.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Today and the week's day columns (13A.25, 13A.26): one person's tasks by status, then day, in order.
        builder.HasIndex(task => new
            {
                task.WorkspaceId,
                task.AssigneeMembershipId,
                task.Status,
                task.ScheduledDate,
                task.SortRank,
                task.Id,
            })
            .HasDatabaseName("IX_WorkTasks_Workspace_Assignee_Status_Day_Rank");

        // One person's selected week (13A.26).
        builder.HasIndex(task => new { task.WorkspaceId, task.AssigneeMembershipId, task.PlanningWeekStart, task.Status })
            .HasDatabaseName("IX_WorkTasks_Workspace_Assignee_Week_Status");

        // Overdue (13A.25). Most tasks have no due date, so only those that do are indexed.
        builder.HasIndex(task => new { task.WorkspaceId, task.AssigneeMembershipId, task.DueDate })
            .HasDatabaseName("IX_WorkTasks_Workspace_Assignee_Due")
            .HasFilter("DueDate IS NOT NULL");

        // Done today and done this week (13A.25, 13A.26).
        builder.HasIndex(task => new { task.WorkspaceId, task.AssigneeMembershipId, task.CompletedAt })
            .HasDatabaseName("IX_WorkTasks_Workspace_Assignee_Completed")
            .HasFilter("CompletedAt IS NOT NULL");

        // What someone captured and handed on.
        builder.HasIndex(task => new { task.WorkspaceId, task.OwnerMembershipId, task.Status })
            .HasDatabaseName("IX_WorkTasks_Workspace_Owner_Status");

        // "Add to my plan" finds the task already made from a step (13A.29). Not unique: whether one step may
        // have one task per person or one in all is 13A.29's to decide.
        builder.HasIndex(task => new { task.WorkspaceId, task.WorkflowRunId, task.WorkflowStepKey })
            .HasDatabaseName("IX_WorkTasks_Workspace_Run_Step")
            .HasFilter("WorkflowRunId IS NOT NULL");

        // One task per person per step added from a workflow (13A.29), kept by the store rather than by a key that
        // expires: two confirms at once, with different keys or none, still make one. Tasks pointed at the same
        // step by hand are outside the filter and unaffected.
        builder.HasIndex(task => new { task.WorkspaceId, task.OwnerMembershipId, task.WorkflowRunId, task.WorkflowStepKey })
            .IsUnique()
            .HasDatabaseName("UX_WorkTasks_Workspace_Owner_WorkflowStep")
            .HasFilter($"Origin = {(int)WorkTaskOrigin.WorkflowStep}");

        // One task per occurrence of a template, kept by the store so a materializer that runs twice, or two at
        // once, still makes one (13A.30).
        builder.HasIndex(task => new { task.WorkspaceId, task.WorkTaskTemplateId, task.RecurrenceDate })
            .IsUnique()
            .HasDatabaseName("UX_WorkTasks_Workspace_Template_Occurrence")
            .HasFilter("WorkTaskTemplateId IS NOT NULL");
    }
}
