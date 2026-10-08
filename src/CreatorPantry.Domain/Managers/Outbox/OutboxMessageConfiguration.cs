using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Managers.Outbox;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(message => message.Id);

        builder.Property(message => message.Type).IsRequired().HasMaxLength(OutboxPolicy.TypeMaxLength);
        builder.Property(message => message.PayloadJson).IsRequired();
        builder.Property(message => message.CorrelationId).IsRequired();
        builder.Property(message => message.CreatedAt).IsRequired();
        builder.Property(message => message.AvailableAt).IsRequired();
        builder.Property(message => message.Attempts).IsRequired();
        // Status and LeasedBy together are the claim's guard (12.10l): as concurrency tokens they are named in
        // the WHERE clause of every UPDATE to a message, so a dispatcher can only claim a row that is still in
        // the state it read, and can only record an outcome for a row whose lease it still holds. No column
        // changes — this is how EF writes the UPDATE, not what the table stores.
        builder.Property(message => message.Status).IsRequired().IsConcurrencyToken();
        builder.Property(message => message.LeasedBy).IsConcurrencyToken();
        builder.Property(message => message.LastError).HasMaxLength(OutboxPolicy.LastErrorMaxLength);

        // The dispatcher's claim query: due Pending rows, or Leased rows whose lease expired.
        builder.HasIndex(message => new { message.Status, message.AvailableAt })
            .HasDatabaseName("IX_OutboxMessages_Status_AvailableAt");
        builder.HasIndex(message => new { message.Status, message.LeaseExpiresAt })
            .HasDatabaseName("IX_OutboxMessages_Status_LeaseExpiresAt");
    }
}
