using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Billing.Data.Configurations;

/// <remarks>
/// The unique index on the provider's event id is the whole replay guard: a second delivery of one event cannot be
/// stored, so it cannot be queued twice either (AP.6.2).
/// </remarks>
internal sealed class BillingProviderEventConfiguration : IEntityTypeConfiguration<BillingProviderEvent>
{
    public void Configure(EntityTypeBuilder<BillingProviderEvent> builder)
    {
        builder.ToTable("BillingProviderEvents", table =>
        {
            // Unquoted and trim(), and the enum's own integer, so the same expressions hold on SQL Server and on the
            // SQLite the model tests run against.
            table.HasCheckConstraint("CK_BillingProviderEvents_ExternalEventId_NotBlank", "trim(ExternalEventId) <> ''");
            table.HasCheckConstraint("CK_BillingProviderEvents_EventType_NotBlank", "trim(EventType) <> ''");
            table.HasCheckConstraint(
                "CK_BillingProviderEvents_Status_Declared",
                $"Status <> {(int)BillingProviderEventStatus.Unspecified}");
        });

        builder.HasKey(providerEvent => providerEvent.Id);

        builder.Property(providerEvent => providerEvent.ExternalEventId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);

        builder.Property(providerEvent => providerEvent.EventType)
            .IsRequired()
            .HasMaxLength(BillingWebhookPolicy.EventTypeMaxLength);

        // No check constraint: BillingEventKind.Other is a real answer — a type this host does not handle — and a row
        // stored before AP.6.3 carries it.
        builder.Property(providerEvent => providerEvent.Kind).IsRequired();

        builder.Property(providerEvent => providerEvent.IsLiveMode).IsRequired();
        builder.Property(providerEvent => providerEvent.OccurredAt).IsRequired();
        builder.Property(providerEvent => providerEvent.ReceivedAt).IsRequired();
        builder.Property(providerEvent => providerEvent.ObjectReference).HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);
        builder.Property(providerEvent => providerEvent.SubscriptionReference).HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);
        builder.Property(providerEvent => providerEvent.Status).IsRequired();
        builder.Property(providerEvent => providerEvent.AccountId).HasMaxLength(BillingPolicy.AccountIdMaxLength);
        builder.Property(providerEvent => providerEvent.Detail).HasMaxLength(BillingWebhookPolicy.DetailMaxLength);
        builder.Property(providerEvent => providerEvent.Redrives).IsRequired();

        builder.HasIndex(providerEvent => providerEvent.ExternalEventId)
            .IsUnique()
            .HasDatabaseName("UX_BillingProviderEvents_ExternalEventId");

        // What an operator looks for first: the events still waiting, or waiting on them.
        builder.HasIndex(providerEvent => new { providerEvent.Status, providerEvent.ReceivedAt })
            .HasDatabaseName("IX_BillingProviderEvents_Status_ReceivedAt");
    }
}
