using CreatorPantry.Domain.Modules.Billing.Data.Entities;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Billing.Data.Configurations;

/// <remarks>
/// Four constraints between them hold each account's rows to a single chain whose starts strictly increase —
/// the proof is short. Every row but one per account supersedes another (the root index). The row it supersedes
/// exists (the foreign key) and started earlier (the check). No row is superseded twice (the successor index).
/// Walking back from any row therefore reaches the one root without repeating, and walking forward from the root
/// meets every row once, in order. So at any instant exactly one row — the latest to have started — is in force,
/// or none is before the first.
/// </remarks>
internal sealed class AccountPlanConfiguration : IEntityTypeConfiguration<AccountPlan>
{
    public void Configure(EntityTypeBuilder<AccountPlan> builder)
    {
        builder.ToTable("AccountPlans", table =>
        {
            // Unquoted identifiers and the enum's own integer value, so the same expression is valid on SQL
            // Server and on the SQLite database the model tests run against.
            table.HasCheckConstraint(
                "CK_AccountPlans_Source_Declared",
                $"Source <> {(int)AccountPlanSource.Unspecified}");

            // Null means "the configured default"; an empty key would mean neither that nor a plan.
            table.HasCheckConstraint(
                "CK_AccountPlans_PlanKey_NotBlank",
                "PlanKey IS NULL OR trim(PlanKey) <> ''");

            // A row comes after the one it supersedes. Without this, a successor could start inside an earlier
            // row's span and the chain would double back on itself.
            table.HasCheckConstraint(
                "CK_AccountPlans_After_Superseded",
                "SupersedesFrom IS NULL OR SupersedesFrom < EffectiveFrom");
        });

        builder.HasKey(plan => plan.Id);

        builder.Property(plan => plan.AccountId)
            .IsRequired()
            .HasMaxLength(BillingPolicy.AccountIdMaxLength);

        builder.Property(plan => plan.PlanKey).HasMaxLength(BillingPolicy.PlanKeyMaxLength);
        builder.Property(plan => plan.Source).IsRequired();
        builder.Property(plan => plan.EffectiveFrom).IsRequired();

        // The principal end of the chain, and the seek for "the row in force at an instant": the latest start at
        // or before it. Also what keeps two rows for one account from starting together.
        builder.HasAlternateKey(plan => new { plan.AccountId, plan.EffectiveFrom })
            .HasName("AK_AccountPlans_Account_EffectiveFrom");

        // A row supersedes a row of the same account that exists. The account is part of the key, so a chain
        // cannot run into another account's rows. No action on delete: the only row ever deleted is the end of
        // the chain, which nothing supersedes, and a cascade would be a chain of deletions nobody asked for.
        builder.HasOne<AccountPlan>()
            .WithMany()
            .HasForeignKey(plan => new { plan.AccountId, plan.SupersedesFrom })
            .HasPrincipalKey(plan => new { plan.AccountId, plan.EffectiveFrom })
            .OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_AccountPlans_Superseded");

        // At most one successor per row: two writers that read the same latest row and both try to supersede it
        // meet here, and one of them is told. Filtered, and written unquoted, so a first row is not counted.
        builder.HasIndex(plan => new { plan.AccountId, plan.SupersedesFrom })
            .IsUnique()
            .HasFilter("SupersedesFrom IS NOT NULL")
            .HasDatabaseName("UX_AccountPlans_Account_Successor");

        // At most one first row per account: two writers both giving an account with no rows its first plan meet
        // here, for the same reason.
        builder.HasIndex(plan => plan.AccountId)
            .IsUnique()
            .HasFilter("SupersedesFrom IS NULL")
            .HasDatabaseName("UX_AccountPlans_Account_Root");
    }
}
