using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Ai.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Ai.Data.Configurations;

internal sealed class WorkspaceAiTaskSwitchConfiguration : IEntityTypeConfiguration<WorkspaceAiTaskSwitch>
{
    public void Configure(EntityTypeBuilder<WorkspaceAiTaskSwitch> builder)
    {
        builder.ToTable("WorkspaceAiTaskSwitches", table =>
        {
            // Written unquoted and with trim rather than LEN so SQL Server and SQLite both accept it.
            table.HasCheckConstraint("CK_WorkspaceAiTaskSwitches_Task_NotBlank", "trim(Task) <> ''");
        });

        builder.HasKey(taskSwitch => taskSwitch.Id);

        // Set by the application, never the store.
        builder.Property(taskSwitch => taskSwitch.Id).ValueGeneratedNever();

        builder.Property(taskSwitch => taskSwitch.Task).IsRequired().HasMaxLength(AiFeaturePolicy.TaskMaxLength);
        builder.Property(taskSwitch => taskSwitch.IsEnabled).IsRequired();
        builder.Property(taskSwitch => taskSwitch.CreatedAt).IsRequired();
        builder.Property(taskSwitch => taskSwitch.UpdatedAt).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(taskSwitch => taskSwitch.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One answer per task per workspace. It is also the lookup every AI request makes, and the guard that
        // turns two Owners saving at once into a refused write rather than two rows that disagree.
        builder.HasIndex(taskSwitch => new { taskSwitch.WorkspaceId, taskSwitch.Task })
            .IsUnique()
            .HasDatabaseName("UX_WorkspaceAiTaskSwitches_Workspace_Task");
    }
}
