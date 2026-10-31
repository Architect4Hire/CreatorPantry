using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Billing.Data.Configurations;

/// <remarks>
/// <para>
/// The unique session is what makes one checkout one purchase however often its webhook arrives (AP.6.3); the unique
/// payment is what lets a refund find exactly one purchase to claw back.
/// </para>
/// <para>
/// <strong>Every decimal is cast before it is compared</strong>, and to <c>FLOAT</c>, for the reasons
/// <see cref="AccountCreditGrantConfiguration"/> gives: SQLite keeps a decimal as text, where an uncast comparison
/// is a string comparison.
/// </para>
/// </remarks>
internal sealed class BillingPackPurchaseConfiguration : IEntityTypeConfiguration<BillingPackPurchase>
{
    public void Configure(EntityTypeBuilder<BillingPackPurchase> builder)
    {
        builder.ToTable("BillingPackPurchases", table =>
        {
            // Unquoted identifiers, trim() and the enum's own integers, so the same expressions hold on SQL Server
            // and on the SQLite the model tests run against.
            table.HasCheckConstraint("CK_BillingPackPurchases_AccountId_NotBlank", "trim(AccountId) <> ''");
            table.HasCheckConstraint("CK_BillingPackPurchases_SessionId_NotBlank", "trim(ExternalSessionId) <> ''");
            table.HasCheckConstraint("CK_BillingPackPurchases_PackKey_NotBlank", "trim(PackKey) <> ''");

            // Null means "none"; an empty reference would mean neither that nor a reference.
            table.HasCheckConstraint(
                "CK_BillingPackPurchases_Payment_NotBlank",
                "ExternalPaymentReference IS NULL OR trim(ExternalPaymentReference) <> ''");

            // A purchase of nought credits would be a payment that bought nothing and still holds a grant.
            table.HasCheckConstraint("CK_BillingPackPurchases_Credits_Positive", "CAST(CreditsGranted AS FLOAT) > 0");

            // What a claw-back took cannot exceed what the payment bought, and cannot be negative.
            table.HasCheckConstraint(
                "CK_BillingPackPurchases_ClawedBack_Within_Granted",
                "CAST(CreditsClawedBack AS FLOAT) >= 0 AND CAST(CreditsClawedBack AS FLOAT) <= CAST(CreditsGranted AS FLOAT)");

            // The reversal is the kind, the instant, the reference and the figure together: before one, none of them
            // is set; after one, the kind and the instant both are, and it cannot come before the purchase.
            table.HasCheckConstraint(
                "CK_BillingPackPurchases_Reversal_Recorded",
                $"(Reversal = {(int)BillingPackReversal.None} AND ReversedAt IS NULL AND ExternalReversalReference IS NULL "
                    + "AND CAST(CreditsClawedBack AS FLOAT) = 0) "
                    + $"OR (Reversal <> {(int)BillingPackReversal.None} AND ReversedAt IS NOT NULL AND ReversedAt >= PurchasedAt)");
        });

        builder.HasKey(purchase => purchase.Id);

        builder.Property(purchase => purchase.AccountId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.AccountIdMaxLength);

        builder.Property(purchase => purchase.ExternalSessionId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);

        builder.Property(purchase => purchase.ExternalPaymentReference)
            .HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);

        builder.Property(purchase => purchase.IsLiveMode).IsRequired();

        builder.Property(purchase => purchase.PackKey)
            .IsRequired()
            .HasMaxLength(BillingPolicy.PackKeyMaxLength);

        builder.Property(purchase => purchase.CreditsGranted)
            .IsRequired()
            .HasPrecision(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale);

        builder.Property(purchase => purchase.CreditsClawedBack)
            .IsRequired()
            .HasPrecision(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale);

        builder.Property(purchase => purchase.GrantId).IsRequired();
        builder.Property(purchase => purchase.PurchasedAt).IsRequired();
        builder.Property(purchase => purchase.Reversal).IsRequired();

        builder.Property(purchase => purchase.ExternalReversalReference)
            .HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);

        // Two reversal events about one payment, processed at once: the loser is told and reads again.
        builder.Property(purchase => purchase.RowVersion).IsRowVersion();

        // The grant the payment made. Restricted rather than cascading: a grant is a retained record and nothing
        // deletes one, so there is no cascade to define — and a purchase must not vanish with it if one ever did.
        builder.HasOne(purchase => purchase.Grant)
            .WithMany()
            .HasForeignKey(purchase => purchase.GrantId)
            .OnDelete(DeleteBehavior.Restrict);

        // One purchase per checkout session: the replay guard on this row, beside the grant's own.
        builder.HasIndex(purchase => purchase.ExternalSessionId)
            .IsUnique()
            .HasDatabaseName("UX_BillingPackPurchases_Session");

        // One purchase per payment, where the payment is known: what a refund or a dispute seeks. Filtered, and
        // written unquoted, so purchases with no payment reference are not counted against each other.
        builder.HasIndex(purchase => purchase.ExternalPaymentReference)
            .IsUnique()
            .HasFilter("ExternalPaymentReference IS NOT NULL")
            .HasDatabaseName("UX_BillingPackPurchases_Payment");

        // An account's purchases, newest first, for an operator answering "what has this creator bought".
        builder.HasIndex(purchase => new { purchase.AccountId, purchase.PurchasedAt })
            .HasDatabaseName("IX_BillingPackPurchases_Account_PurchasedAt");
    }
}
