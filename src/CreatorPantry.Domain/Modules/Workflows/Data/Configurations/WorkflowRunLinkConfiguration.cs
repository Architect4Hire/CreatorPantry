using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Workflows.Data.Configurations;

internal sealed class WorkflowRunLinkConfiguration : IEntityTypeConfiguration<WorkflowRunLink>
{
    public void Configure(EntityTypeBuilder<WorkflowRunLink> builder)
    {
        builder.ToTable("WorkflowRunLinks", table =>
        {
            // Every kind the contract names, read from the enum: a kind added later changes this text, which is
            // a model change no migration has captured, so it cannot reach the database unnoticed.
            table.HasCheckConstraint(
                "CK_WorkflowRunLinks_ResourceKind_Range",
                $"ResourceKind >= {WorkflowResourceKinds.Lowest} AND ResourceKind <= {WorkflowResourceKinds.Highest}");
            table.HasCheckConstraint("CK_WorkflowRunLinks_StepKey_NotBlank", "StepKey IS NULL OR trim(StepKey) <> ''");
        });

        builder.HasKey(link => link.Id);
        builder.Property(link => link.Id).ValueGeneratedNever();

        builder.Property(link => link.ResourceKind).IsRequired();
        builder.Property(link => link.ResourceId).IsRequired();
        builder.Property(link => link.StepKey).HasMaxLength(WorkflowRunPolicy.KeyMaxLength);
        builder.Property(link => link.LinkedAt).IsRequired();
        builder.Property(link => link.LinkedByMembershipId).IsRequired();

        // The only foreign key: into the run. ResourceId names a record in another module and is not one, by
        // design — see WorkflowRunLink.
        builder.HasOne<WorkflowRun>()
            .WithMany()
            .HasForeignKey(link => new { link.WorkspaceId, link.WorkflowRunId })
            .HasPrincipalKey(run => new { run.WorkspaceId, run.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(link => new { link.WorkspaceId, link.WorkflowRunId, link.ResourceKind, link.ResourceId })
            .IsUnique()
            .HasDatabaseName("UX_WorkflowRunLinks_Workspace_Run_Resource");

        // The runs about one record, e.g. "continue the run on this recipe".
        builder.HasIndex(link => new { link.WorkspaceId, link.ResourceKind, link.ResourceId })
            .HasDatabaseName("IX_WorkflowRunLinks_Workspace_Resource");
    }
}
