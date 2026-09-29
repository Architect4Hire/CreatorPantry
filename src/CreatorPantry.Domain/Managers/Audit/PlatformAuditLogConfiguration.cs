using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Managers.Audit;

internal sealed class PlatformAuditLogConfiguration : IEntityTypeConfiguration<PlatformAuditLog>
{
    public void Configure(EntityTypeBuilder<PlatformAuditLog> builder)
    {
        builder.ToTable("PlatformAuditLogs");
        builder.HasKey(log => log.Id);

        builder.Property(log => log.ActorType).IsRequired();

        // Not foreign keys, to either OpsApiClients or AspNetUsers: an audit row must survive the rotation
        // or revocation of the credential that acted, and the deletion of the account it acted on.
        builder.Property(log => log.ActorId).IsRequired().HasMaxLength(AuditPolicy.ActorIdMaxLength);
        builder.Property(log => log.ActorName).IsRequired().HasMaxLength(AuditPolicy.ActorNameMaxLength);

        builder.Property(log => log.Action).IsRequired().HasMaxLength(AuditPolicy.ActionMaxLength);
        builder.Property(log => log.SubjectType).IsRequired().HasMaxLength(AuditPolicy.ResourceTypeMaxLength);
        builder.Property(log => log.SubjectId).IsRequired().HasMaxLength(AuditPolicy.ActorIdMaxLength);
        builder.Property(log => log.Reason).IsRequired().HasMaxLength(AuditPolicy.SummaryMaxLength);
        builder.Property(log => log.BeforeReference).HasMaxLength(AuditPolicy.ReferenceMaxLength);
        builder.Property(log => log.AfterReference).HasMaxLength(AuditPolicy.ReferenceMaxLength);
        builder.Property(log => log.CorrelationId).IsRequired();
        builder.Property(log => log.OccurredAt).IsRequired();

        // The operator's first question: what has been done to this account?
        builder.HasIndex(log => new { log.SubjectType, log.SubjectId, log.OccurredAt })
            .HasDatabaseName("IX_PlatformAuditLogs_Subject_OccurredAt");

        // The second: what has been done at all, lately?
        builder.HasIndex(log => log.OccurredAt)
            .HasDatabaseName("IX_PlatformAuditLogs_OccurredAt");

        builder.ToTable(table => table.HasCheckConstraint(
            "CK_PlatformAuditLogs_ActorType", $"[ActorType] <> {(int)PlatformAuditActorType.Unspecified}"));
    }
}
