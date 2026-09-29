using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Configurations;

internal sealed class AccountAiQuotaConfiguration : IEntityTypeConfiguration<AccountAiQuota>
{
    public void Configure(EntityTypeBuilder<AccountAiQuota> builder)
    {
        builder.ToTable("AccountAiQuotas", table =>
        {
            // Written with unquoted identifiers and the enums' own integer values so the same expression is
            // valid on SQL Server and on the SQLite database the constraint tests run against.
            table.HasCheckConstraint(
                "CK_AccountAiQuotas_Unit_Declared",
                $"Unit <> {(int)AiQuotaUnit.Unspecified}");

            table.HasCheckConstraint(
                "CK_AccountAiQuotas_PeriodLength_Declared",
                $"PeriodLength <> {(int)AiQuotaPeriodLength.Unspecified}");

            table.HasCheckConstraint(
                "CK_AccountAiQuotas_CarryOver_Declared",
                $"CarryOver <> {(int)AiQuotaCarryOver.Unspecified}");

            // Null means "resolve the configured platform default", which is how USAGE-003's ban on inventing
            // a default in domain code is made structural. A negative allowance is neither that nor a number
            // anyone can spend against.
            table.HasCheckConstraint(
                "CK_AccountAiQuotas_Allowance_NotNegative",
                "Allowance IS NULL OR Allowance >= 0");

            // The anchor's domain depends on the length, so the two are constrained together rather than
            // separately: a weekly quota anchored to day 28 would otherwise be storable and unopenable.
            table.HasCheckConstraint(
                "CK_AccountAiQuotas_Anchor_Matches_Length",
                $"(PeriodLength = {(int)AiQuotaPeriodLength.Daily} AND PeriodAnchor = 0) "
                    + $"OR (PeriodLength = {(int)AiQuotaPeriodLength.Weekly} AND PeriodAnchor BETWEEN 0 AND 6) "
                    + $"OR (PeriodLength = {(int)AiQuotaPeriodLength.Monthly} AND PeriodAnchor BETWEEN 1 AND {AiUsagePolicy.MonthlyAnchorMax})");

            // A cap is required for Unused and forbidden otherwise. Without it an account that spends nothing
            // accumulates an unbounded balance and is effectively unmetered the first month it does spend.
            table.HasCheckConstraint(
                "CK_AccountAiQuotas_CarryOverCap_Matches_Rule",
                $"(CarryOver = {(int)AiQuotaCarryOver.Unused} AND CarryOverCap IS NOT NULL AND CarryOverCap >= 0) "
                    + $"OR (CarryOver = {(int)AiQuotaCarryOver.None} AND CarryOverCap IS NULL)");

            table.HasCheckConstraint(
                "CK_AccountAiQuotas_Effective_Range",
                "EffectiveTo IS NULL OR EffectiveTo > EffectiveFrom");
        });

        builder.HasKey(quota => quota.Id);

        builder.Property(quota => quota.AccountId)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.AccountIdMaxLength);

        builder.Property(quota => quota.Unit).IsRequired();
        builder.Property(quota => quota.PeriodLength).IsRequired();
        builder.Property(quota => quota.PeriodAnchor).IsRequired();
        builder.Property(quota => quota.CarryOver).IsRequired();
        builder.Property(quota => quota.IsSuspended).IsRequired();
        builder.Property(quota => quota.EffectiveFrom).IsRequired();
        builder.Property(quota => quota.LastChangedAt).IsRequired();

        // A concurrency token, so closing a row carries `WHERE EffectiveTo IS NULL` and a second writer that
        // already closed it is told rather than silently agreed with. The filtered unique index below catches
        // two writers that both *insert*; it cannot catch a clear racing a set, because a clear inserts
        // nothing — both would close the same row, neither would collide, and the audit trail would then
        // record a state the account was never in. No schema change: the column already exists and is already
        // nullable; this only changes the predicate EF puts on the UPDATE.
        builder.Property(quota => quota.EffectiveTo).IsConcurrencyToken();

        builder.Property(quota => quota.TimeZoneId)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.TimeZoneIdMaxLength);

        builder.Property(quota => quota.LastChangedByUserId)
            .HasMaxLength(AiUsagePolicy.AccountIdMaxLength);

        builder.Property(quota => quota.Allowance)
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        builder.Property(quota => quota.CarryOverCap)
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        // No relationship to ApplicationUser, for the reason AccountAiUsageEntry gives: account erasure is a
        // documented path (9A.3), not a cascade default.

        // The real invariant, expressed where it cannot be forgotten: at most one set of terms in force per
        // account at any instant. The write path closes the open row before inserting the next one, and this
        // is what happens when it does not. Filter written unquoted so SQL Server and SQLite both accept it.
        builder.HasIndex(quota => quota.AccountId)
            .IsUnique()
            .HasFilter("EffectiveTo IS NULL")
            .HasDatabaseName("UX_AccountAiQuotas_Account_Current");

        // History rows cannot collide on a start instant, which is also the order "what were this account's
        // terms in March?" reads in.
        builder.HasIndex(quota => new { quota.AccountId, quota.EffectiveFrom })
            .IsUnique()
            .HasDatabaseName("UX_AccountAiQuotas_Account_EffectiveFrom");
    }
}
