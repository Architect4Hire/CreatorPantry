using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Planning.Data.Configurations;

internal sealed class WorkTaskFocusConfiguration : IEntityTypeConfiguration<WorkTaskFocus>
{
    public void Configure(EntityTypeBuilder<WorkTaskFocus> builder)
    {
        builder.ToTable("WorkTaskFocuses", table =>
            table.HasCheckConstraint(
                "CK_WorkTaskFocuses_Position_Range",
                $"Position >= 1 AND Position <= {WorkTaskPolicy.TopThreeMax}"));

        // One task in each place of one person's day, so there are never more than three.
        builder.HasKey(focus => new { focus.WorkspaceId, focus.AssigneeMembershipId, focus.FocusDate, focus.Position });

        // And each task in one place only.
        builder.HasIndex(focus => new { focus.WorkspaceId, focus.AssigneeMembershipId, focus.FocusDate, focus.WorkTaskId })
            .IsUnique()
            .HasDatabaseName("UX_WorkTaskFocuses_Workspace_Assignee_Date_Task");

        // Interior for deletion: the task cascades its places, and there is no second path from Workspaces.
        builder.HasOne<WorkTask>()
            .WithMany()
            .HasForeignKey(focus => new { focus.WorkspaceId, focus.WorkTaskId })
            .HasPrincipalKey(task => new { task.WorkspaceId, task.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
