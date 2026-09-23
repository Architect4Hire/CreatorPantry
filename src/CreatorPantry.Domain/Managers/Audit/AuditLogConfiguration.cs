using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Managers.Audit;

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(log => log.Id);

        // Not a foreign key: an audit row must survive deletion of the acting user's account, matching the
        // AuditLog remarks (a historical record, not a live reference).
        builder.Property(log => log.ActorUserId).HasMaxLength(450);

        builder.Property(log => log.Action).IsRequired().HasMaxLength(AuditPolicy.ActionMaxLength);
        builder.Property(log => log.ResourceType).IsRequired().HasMaxLength(AuditPolicy.ResourceTypeMaxLength);
        builder.Property(log => log.ResourceId).IsRequired().HasMaxLength(AuditPolicy.ResourceIdMaxLength);
        builder.Property(log => log.CorrelationId).IsRequired();
        builder.Property(log => log.OccurredAt).IsRequired();
        builder.Property(log => log.Summary).IsRequired().HasMaxLength(AuditPolicy.SummaryMaxLength);
        builder.Property(log => log.BeforeReference).HasMaxLength(AuditPolicy.ReferenceMaxLength);
        builder.Property(log => log.AfterReference).HasMaxLength(AuditPolicy.ReferenceMaxLength);

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(log => log.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Listing a workspace's history chronologically.
        builder.HasIndex(log => new { log.WorkspaceId, log.OccurredAt })
            .HasDatabaseName("IX_AuditLogs_Workspace_OccurredAt");

        // Listing the history of one specific resource.
        builder.HasIndex(log => new { log.WorkspaceId, log.ResourceType, log.ResourceId })
            .HasDatabaseName("IX_AuditLogs_Workspace_Resource");
    }
}
