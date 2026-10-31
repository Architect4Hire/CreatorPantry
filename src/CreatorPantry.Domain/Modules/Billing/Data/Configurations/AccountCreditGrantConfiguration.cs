using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Billing.Data.Configurations;

/// <remarks>
/// <para>
/// The checks between them hold a grant to "spent + remaining + clawed back = granted, none of them negative".
/// Remaining and clawed back each fit inside the grant, and the claw-back check makes one of the two nought —
/// before a claw-back nothing has been taken, after it nothing is left — so their sum fits inside it too, and
/// what was spent, the difference, is never below nought. Written as comparisons rather than as that sum because
/// each column is cast for SQLite (below), and a floating-point sum of two four-decimal figures can land a hair
/// above a third that equals it exactly.
/// </para>
/// <para>
/// <strong>Every decimal is cast before it is compared.</strong> SQLite stores a decimal as text, so an uncast
/// comparison there is a string comparison: <c>'0.0' &gt; 0</c> is true and <c>'10.0' &lt;= '9.0'</c> is false.
/// <c>FLOAT</c> rather than the <c>REAL</c> <c>AccountAiQuotaPeriods</c> casts to, because <c>REAL</c> is single
/// precision on SQL Server and those checks only ever compare with nought: a cast keeps order but can merge two
/// close figures, and a double cannot merge two four-decimal credit figures this table could plausibly hold.
/// </para>
/// </remarks>
internal sealed class AccountCreditGrantConfiguration : IEntityTypeConfiguration<AccountCreditGrant>
{
    public void Configure(EntityTypeBuilder<AccountCreditGrant> builder)
    {
        builder.ToTable("AccountCreditGrants", table =>
        {
            // Unquoted identifiers and the enum's own integer values, so the same expressions are valid on SQL
            // Server and on the SQLite database the model tests run against.
            table.HasCheckConstraint(
                "CK_AccountCreditGrants_Source_Declared",
                $"Source <> {(int)CreditGrantSource.Unspecified}");

            // A grant of nought would be a row that grants nothing and still answers to a reference.
            table.HasCheckConstraint(
                "CK_AccountCreditGrants_Granted_Positive",
                "CAST(CreditsGranted AS FLOAT) > 0");

            // The restriction AP.5.1 names: no write, a spend or a claw-back, makes a balance negative.
            table.HasCheckConstraint(
                "CK_AccountCreditGrants_Remaining_Within_Granted",
                "CAST(CreditsRemaining AS FLOAT) >= 0 AND CAST(CreditsRemaining AS FLOAT) <= CAST(CreditsGranted AS FLOAT)");

            table.HasCheckConstraint(
                "CK_AccountCreditGrants_ClawedBack_Within_Granted",
                "CAST(CreditsClawedBack AS FLOAT) >= 0 AND CAST(CreditsClawedBack AS FLOAT) <= CAST(CreditsGranted AS FLOAT)");

            // The claw-back is the instant and the figure together: before it nothing has been taken, after it
            // nothing is left to spend, and it cannot come before the grant it takes from.
            table.HasCheckConstraint(
                "CK_AccountCreditGrants_ClawBack_Recorded",
                "(ClawedBackAt IS NULL AND CAST(CreditsClawedBack AS FLOAT) = 0) "
                    + "OR (ClawedBackAt IS NOT NULL AND CAST(CreditsRemaining AS FLOAT) = 0 AND ClawedBackAt >= GrantedAt)");

            // A pack is always answerable to the checkout that paid for it (AP.6.3).
            table.HasCheckConstraint(
                "CK_AccountCreditGrants_Purchase_Referenced",
                $"Source <> {(int)CreditGrantSource.Purchase} OR ExternalReferenceId IS NOT NULL");

            // Null means "none"; an empty reference would mean neither that nor a reference.
            table.HasCheckConstraint(
                "CK_AccountCreditGrants_Reference_NotBlank",
                "ExternalReferenceId IS NULL OR trim(ExternalReferenceId) <> ''");
        });

        builder.HasKey(grant => grant.Id);

        builder.Property(grant => grant.AccountId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.AccountIdMaxLength);

        builder.Property(grant => grant.Source).IsRequired();

        builder.Property(grant => grant.CreditsGranted)
            .IsRequired()
            .HasPrecision(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale);

        builder.Property(grant => grant.CreditsRemaining)
            .IsRequired()
            .HasPrecision(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale);

        builder.Property(grant => grant.CreditsClawedBack)
            .IsRequired()
            .HasPrecision(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale);

        builder.Property(grant => grant.ExternalReferenceId)
            .HasMaxLength(BillingPolicy.ExternalReferenceMaxLength);

        builder.Property(grant => grant.GrantedAt).IsRequired();

        // Contended counters: admission spending the last credits (AP.5.2) and a refund clawing them back (AP.6.3)
        // must not both act on the balance they read. The loser is told and reads again.
        builder.Property(grant => grant.RowVersion).IsRowVersion();

        // One grant per external reference within a source: a checkout session's webhook delivered twice grants
        // once (AP.6.3), and two operators entering the same ticket meet here. Filtered, and written unquoted, so
        // the grants that carry no reference are not counted.
        builder.HasIndex(grant => new { grant.Source, grant.ExternalReferenceId })
            .IsUnique()
            .HasFilter("ExternalReferenceId IS NOT NULL")
            .HasDatabaseName("UX_AccountCreditGrants_Source_Reference");

        // An account's grants in the order they were made: the seek for listing them, and for spending them in a
        // stable order (AP.5.2 proposes oldest first). The id breaks a tie between two grants in one tick.
        builder.HasIndex(grant => new { grant.AccountId, grant.GrantedAt, grant.Id })
            .HasDatabaseName("IX_AccountCreditGrants_Account_GrantedAt");
    }
}
