using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Configurations;

internal sealed class AccountAiQuotaPeriodConfiguration : IEntityTypeConfiguration<AccountAiQuotaPeriod>
{
    public void Configure(EntityTypeBuilder<AccountAiQuotaPeriod> builder)
    {
        builder.ToTable("AccountAiQuotaPeriods", table =>
        {
            table.HasCheckConstraint(
                "CK_AccountAiQuotaPeriods_Unit_Declared",
                $"Unit <> {(int)AiQuotaUnit.Unspecified}");

            // Half-open [StartsAt, EndsAt). A zero-length period would be spendable-against and never current.
            table.HasCheckConstraint(
                "CK_AccountAiQuotaPeriods_Ends_After_Starts",
                "EndsAt > StartsAt");

            // Required here, unlike on the quota: by the time a period opens, the platform default has been
            // resolved to an actual number and this row records what was spendable.
            table.HasCheckConstraint(
                "CK_AccountAiQuotaPeriods_Totals_NotNegative",
                "Allowance >= 0 AND CarriedOver >= 0 AND Consumed >= 0 AND Reserved >= 0");

            // Deliberately NOT constrained: Consumed + Reserved <= Allowance + CarriedOver. A provider can use
            // more than the reservation estimated, and refusing to record that overshoot would be far worse
            // than storing it — an unrecordable settlement is a lost attempt. Handling the overshoot is the
            // settlement seam's job (9A.5), not the database's.

            // A period closes when its last reservation settles or expires, not when the clock passes EndsAt,
            // so a settlement instant before the period even ended would mean the two disagree about which.
            table.HasCheckConstraint(
                "CK_AccountAiQuotaPeriods_Settled_After_End",
                "SettledAt IS NULL OR SettledAt >= EndsAt");
        });

        builder.HasKey(period => period.Id);

        builder.Property(period => period.AccountId)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.AccountIdMaxLength);

        builder.Property(period => period.StartsAt).IsRequired();
        builder.Property(period => period.EndsAt).IsRequired();
        builder.Property(period => period.LocalStartDate).IsRequired();
        builder.Property(period => period.Unit).IsRequired();

        builder.Property(period => period.TimeZoneId)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.TimeZoneIdMaxLength);

        builder.Property(period => period.Allowance)
            .IsRequired()
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        builder.Property(period => period.CarriedOver)
            .IsRequired()
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        builder.Property(period => period.Consumed)
            .IsRequired()
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        builder.Property(period => period.Reserved)
            .IsRequired()
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        // Contended counters, not collaborative editing: two workers reserving for the same account must not
        // both read the same remaining balance and both be admitted (USAGE-004). The loser gets a conflict to
        // retry from. The single-statement atomic decrement that would avoid the retry is not available —
        // BulkOperationBoundaryTests bans ExecuteUpdate in domain code — so 9A.5 owns the retry policy.
        builder.Property(period => period.RowVersion).IsRowVersion();

        // No relationship to AccountAiQuota: the period copied its terms rather than referencing them, and it
        // has to survive the quota that opened it being superseded. See the entity's remarks.

        // One period per account per start instant — what makes the roll idempotent. Two workers both opening
        // the next period race here and one loses, instead of both succeeding and splitting the allowance.
        // Also the seek for "the period containing now", with EndsAt as a residual.
        builder.HasIndex(period => new { period.AccountId, period.StartsAt })
            .IsUnique()
            .HasDatabaseName("UX_AccountAiQuotaPeriods_Account_StartsAt");

        // The finalization sweep: ended periods whose reservations have not all settled. Deliberately does not
        // lead with the account, for the reason AiOperation's claim index does not lead with the workspace —
        // the sweep is looking for work before it knows whose it is. Filtered so it stays small as history
        // grows, and written unquoted so SQL Server and SQLite both accept it.
        builder.HasIndex(period => period.EndsAt)
            .HasFilter("SettledAt IS NULL")
            .HasDatabaseName("IX_AccountAiQuotaPeriods_Unsettled");
    }
}
