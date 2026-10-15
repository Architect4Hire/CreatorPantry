using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Planning.Data.Configurations;

internal sealed class WorkTaskLabelConfiguration : IEntityTypeConfiguration<WorkTaskLabel>
{
    public void Configure(EntityTypeBuilder<WorkTaskLabel> builder)
    {
        builder.ToTable("WorkTaskLabels");

        // A task carries a tag once or not at all. Workspace first, so the key also serves the task's foreign key.
        builder.HasKey(label => new { label.WorkspaceId, label.WorkTaskId, label.WorkspaceTagId });

        // Interior for deletion: the task cascades its labels, and there is no second path from Workspaces.
        builder.HasOne<WorkTask>()
            .WithMany()
            .HasForeignKey(label => new { label.WorkspaceId, label.WorkTaskId })
            .HasPrincipalKey(task => new { task.WorkspaceId, task.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // The shared workspace vocabulary. Workspace-paired, so one workspace's task cannot carry another's tag,
        // and restricted, because the tag cascades from the workspace too.
        builder.HasOne<WorkspaceTag>()
            .WithMany()
            .HasForeignKey(label => new { label.WorkspaceId, label.WorkspaceTagId })
            .HasPrincipalKey(tag => new { tag.WorkspaceId, tag.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtering by label: every task carrying this one.
        builder.HasIndex(label => new { label.WorkspaceId, label.WorkspaceTagId, label.WorkTaskId })
            .HasDatabaseName("IX_WorkTaskLabels_Workspace_Tag_Task");
    }
}
