using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Managers.Idempotency;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");
        builder.HasKey(record => record.Id);

        builder.Property(record => record.UserId).IsRequired().HasMaxLength(450);
        builder.Property(record => record.Operation).IsRequired().HasMaxLength(IdempotencyPolicy.OperationMaxLength);
        builder.Property(record => record.Key).IsRequired().HasMaxLength(IdempotencyPolicy.KeyMaxLength);
        builder.Property(record => record.FingerprintHash).IsRequired().HasMaxLength(32).IsFixedLength();
        builder.Property(record => record.ResultJson).IsRequired();

        // The scope is never a bare key. A concurrent insert of the same scope waits on this index until the
        // first transaction commits, which is what serializes duplicate requests.
        builder.HasIndex(record => new { record.UserId, record.WorkspaceId, record.Operation, record.Key })
            .IsUnique()
            .HasDatabaseName("UX_IdempotencyRecords_Scope");

        // For the expired-record cleanup job.
        builder.HasIndex(record => record.ExpiresAt).HasDatabaseName("IX_IdempotencyRecords_ExpiresAt");
    }
}
