using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Billing.Data.Configurations;

/// <remarks>
/// One row per provider subscription (the unique index), read back per account and mode (the other), with a row
/// version so two Workers refreshing one subscription cannot both write what they read (AP.6.2).
/// </remarks>
internal sealed class BillingSubscriptionConfiguration : IEntityTypeConfiguration<BillingSubscription>
{
    public void Configure(EntityTypeBuilder<BillingSubscription> builder)
    {
        builder.ToTable("BillingSubscriptions", table =>
        {
            table.HasCheckConstraint("CK_BillingSubscriptions_AccountId_NotBlank", "trim(AccountId) <> ''");
            table.HasCheckConstraint("CK_BillingSubscriptions_ExternalSubscriptionId_NotBlank", "trim(ExternalSubscriptionId) <> ''");
            table.HasCheckConstraint("CK_BillingSubscriptions_ExternalCustomerId_NotBlank", "trim(ExternalCustomerId) <> ''");
            table.HasCheckConstraint(
                "CK_BillingSubscriptions_NextPhase_Whole",
                "(NextPlanKey IS NULL AND NextPhaseStartsAt IS NULL) OR NextPhaseStartsAt IS NOT NULL");
        });

        builder.HasKey(subscription => subscription.Id);

        builder.Property(subscription => subscription.AccountId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.AccountIdMaxLength);

        builder.Property(subscription => subscription.ExternalSubscriptionId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);

        builder.Property(subscription => subscription.ExternalCustomerId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);

        builder.Property(subscription => subscription.IsLiveMode).IsRequired();
        builder.Property(subscription => subscription.Status).IsRequired();
        builder.Property(subscription => subscription.ItemId).HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);
        builder.Property(subscription => subscription.PlanKey).HasMaxLength(BillingPolicy.PlanKeyMaxLength);
        builder.Property(subscription => subscription.Cycle).IsRequired();
        builder.Property(subscription => subscription.ScheduleId).HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);
        builder.Property(subscription => subscription.NextPlanKey).HasMaxLength(BillingPolicy.PlanKeyMaxLength);
        builder.Property(subscription => subscription.NextCycle).IsRequired();
        builder.Property(subscription => subscription.LatestInvoicePaid).IsRequired();
        builder.Property(subscription => subscription.CreatedAt).IsRequired();
        builder.Property(subscription => subscription.UpdatedAt).IsRequired();
        builder.Property(subscription => subscription.RowVersion).IsRowVersion();

        builder.HasIndex(subscription => subscription.ExternalSubscriptionId)
            .IsUnique()
            .HasDatabaseName("UX_BillingSubscriptions_ExternalSubscriptionId");

        // Every subscription an account has, which is what its plan is decided from.
        builder.HasIndex(subscription => new { subscription.AccountId, subscription.IsLiveMode })
            .HasDatabaseName("IX_BillingSubscriptions_Account_Mode");
    }
}
