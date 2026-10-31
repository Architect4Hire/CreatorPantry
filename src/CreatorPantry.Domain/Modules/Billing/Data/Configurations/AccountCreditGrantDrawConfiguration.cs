using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Billing.Data.Configurations;

/// <remarks>
/// Every decimal is cast before it is compared, for the reason <see cref="AccountCreditGrantConfiguration"/> gives:
/// SQLite keeps a decimal as text, and an uncast comparison there is a string comparison.
/// </remarks>
internal sealed class AccountCreditGrantDrawConfiguration : IEntityTypeConfiguration<AccountCreditGrantDraw>
{
    public void Configure(EntityTypeBuilder<AccountCreditGrantDraw> builder)
    {
        builder.ToTable("AccountCreditGrantDraws", table =>
        {
            // A draw of nought would be a row that took nothing and still names a hold.
            table.HasCheckConstraint(
                "CK_AccountCreditGrantDraws_Held_Positive",
                "CAST(CreditsHeld AS FLOAT) > 0");

            table.HasCheckConstraint(
                "CK_AccountCreditGrantDraws_Spent_Within_Held",
                "CreditsSpent IS NULL OR (CAST(CreditsSpent AS FLOAT) >= 0 AND CAST(CreditsSpent AS FLOAT) <= CAST(CreditsHeld AS FLOAT))");

            table.HasCheckConstraint(
                "CK_AccountCreditGrantDraws_ClawedBack_Within_Held",
                "CAST(CreditsClawedBack AS FLOAT) >= 0 AND CAST(CreditsClawedBack AS FLOAT) <= CAST(CreditsHeld AS FLOAT)");

            // Resolved is the instant and the figure together, and an open draw has sent nothing to a claw-back.
            table.HasCheckConstraint(
                "CK_AccountCreditGrantDraws_Resolution_Recorded",
                "(ResolvedAt IS NULL AND CreditsSpent IS NULL AND CAST(CreditsClawedBack AS FLOAT) = 0) "
                    + "OR (ResolvedAt IS NOT NULL AND CreditsSpent IS NOT NULL AND ResolvedAt >= HeldAt)");
        });

        builder.HasKey(draw => draw.Id);

        builder.Property(draw => draw.AccountId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.AccountIdMaxLength);

        builder.Property(draw => draw.HoldId).IsRequired();
        builder.Property(draw => draw.HeldAt).IsRequired();

        builder.Property(draw => draw.CreditsHeld)
            .IsRequired()
            .HasPrecision(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale);

        builder.Property(draw => draw.CreditsSpent)
            .HasPrecision(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale);

        builder.Property(draw => draw.CreditsClawedBack)
            .IsRequired()
            .HasPrecision(BillingPolicy.CreditPrecision, BillingPolicy.CreditScale);

        // A real relationship inside the module. Restrict, though nothing deletes either row: a grant with draws
        // against it is the record of where its credits went.
        builder.HasOne(draw => draw.Grant)
            .WithMany()
            .HasForeignKey(draw => draw.GrantId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(draw => draw.GrantId)
            .HasDatabaseName("IX_AccountCreditGrantDraws_Grant");

        // One draw per grant per hold, and the seek for a hold's draws when it resolves. A hold that took from a
        // grant twice would be two rows answering one question.
        builder.HasIndex(draw => new { draw.HoldId, draw.GrantId })
            .IsUnique()
            .HasDatabaseName("UX_AccountCreditGrantDraws_Hold_Grant");
    }
}
