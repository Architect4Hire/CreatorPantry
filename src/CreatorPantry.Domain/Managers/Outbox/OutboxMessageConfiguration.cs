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
        builder.Property(message => message.Status).IsRequired();
        builder.Property(message => message.LastError).HasMaxLength(OutboxPolicy.LastErrorMaxLength);

        // The dispatcher's claim query: due Pending rows, or Leased rows whose lease expired.
        builder.HasIndex(message => new { message.Status, message.AvailableAt })
            .HasDatabaseName("IX_OutboxMessages_Status_AvailableAt");
        builder.HasIndex(message => new { message.Status, message.LeaseExpiresAt })
            .HasDatabaseName("IX_OutboxMessages_Status_LeaseExpiresAt");
    }
}
