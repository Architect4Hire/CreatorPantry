using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Configurations;

internal sealed class AccountAiQuotaReservationConfiguration
    : IEntityTypeConfiguration<AccountAiQuotaReservation>
{
    public void Configure(EntityTypeBuilder<AccountAiQuotaReservation> builder)
    {
        builder.ToTable("AccountAiQuotaReservations", table =>
        {
            // Unquoted identifiers and the enums' own integer values, so the same expression is valid on SQL
            // Server and on the SQLite database the constraint tests run against.
            table.HasCheckConstraint(
                "CK_AccountAiQuotaReservations_Status_Declared",
                $"Status <> {(int)AiQuotaReservationStatus.Unspecified}");

            table.HasCheckConstraint(
                "CK_AccountAiQuotaReservations_Unit_Declared",
                $"Unit <> {(int)AiQuotaUnit.Unspecified}");

            // A negative hold would add allowance rather than spend it. A zero one is refused separately, in
            // Business, because an unpriced task must not be admitted free -- but a run whose settlement comes
            // out at zero is ordinary, so only the estimate columns have a floor above zero.
            table.HasCheckConstraint(
                "CK_AccountAiQuotaReservations_Amounts_NotNegative",
                "ReservedAmount > 0 AND PerAttemptEstimate > 0 "
                    + "AND (SettledAmount IS NULL OR SettledAmount >= 0)");

            // Two states know the charge and two do not, and the pairing is a constraint rather than a
            // convention: a settled row with no amount would post as nothing, silently making a run free.
            // Expired sits with Held rather than with the settlements because expiry gives the hold back
            // without ever learning what the run cost -- an unknown, not a zero.
            table.HasCheckConstraint(
                "CK_AccountAiQuotaReservations_Outcome_Matches_Status",
                $"(Status IN ({(int)AiQuotaReservationStatus.Held}, {(int)AiQuotaReservationStatus.Expired}) "
                    + "AND SettledAt IS NULL AND SettledAmount IS NULL) "
                    + $"OR (Status IN ({(int)AiQuotaReservationStatus.Settled}, {(int)AiQuotaReservationStatus.Released}) "
                    + "AND SettledAt IS NOT NULL AND SettledAmount IS NOT NULL)");

            // A hold that is still held has given nothing back and posted nothing; an expired one has given
            // its hold back by definition. The timestamps are applied in a different transaction from the one
            // that decides the outcome -- see the entity's remarks -- so this is what keeps the two in step.
            table.HasCheckConstraint(
                "CK_AccountAiQuotaReservations_Timestamps_Match_Status",
                $"(Status <> {(int)AiQuotaReservationStatus.Held} "
                    + "OR (ReleasedAt IS NULL AND PostedAt IS NULL)) "
                    + $"AND (Status <> {(int)AiQuotaReservationStatus.Expired} OR ReleasedAt IS NOT NULL)");

            // Nothing may be posted that was never decided.
            table.HasCheckConstraint(
                "CK_AccountAiQuotaReservations_Posted_Requires_Settlement",
                "PostedAt IS NULL OR SettledAmount IS NOT NULL");
        });

        builder.HasKey(reservation => reservation.Id);

        builder.Property(reservation => reservation.AccountId)
            .IsRequired()
            .HasMaxLength(AiUsagePolicy.AccountIdMaxLength);

        builder.Property(reservation => reservation.PeriodId).IsRequired();
        builder.Property(reservation => reservation.AiOperationId).IsRequired();
        builder.Property(reservation => reservation.LeaseToken).IsRequired();
        builder.Property(reservation => reservation.TaskType).IsRequired();
        builder.Property(reservation => reservation.Unit).IsRequired();
        builder.Property(reservation => reservation.Status).IsRequired();
        builder.Property(reservation => reservation.UsageReported).IsRequired();
        builder.Property(reservation => reservation.HeldAt).IsRequired();
        builder.Property(reservation => reservation.ExpiresAt).IsRequired();

        builder.Property(reservation => reservation.ReservedAmount)
            .IsRequired()
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        builder.Property(reservation => reservation.PerAttemptEstimate)
            .IsRequired()
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        builder.Property(reservation => reservation.SettledAmount)
            .HasPrecision(AiUsagePolicy.AllowancePrecision, AiUsagePolicy.AllowanceScale);

        // The one real relationship in this module. A reservation without its period is uninterpretable rather
        // than merely incomplete, and Restrict is the point: a period holding outstanding reservations is not a
        // period anything may remove out from under them.
        builder.HasOne(reservation => reservation.Period)
            .WithMany()
            .HasForeignKey(reservation => reservation.PeriodId)
            .OnDelete(DeleteBehavior.Restrict);

        // One claim, one reservation. A lease that lapses returns its operation to the queue and the next
        // worker to claim it asks for allowance again -- so the operation alone is not the identity, and this
        // pair is what makes a replayed admission find the hold it already took instead of taking a second.
        builder.HasIndex(reservation => new { reservation.AiOperationId, reservation.LeaseToken })
            .IsUnique()
            .HasDatabaseName("UX_AccountAiQuotaReservations_Operation_Lease");

        // The expiry sweep: holds past their lease. Deliberately does not lead with the account, for the reason
        // the period's unsettled index and AiOperation's claim index do not lead with theirs -- the sweep is
        // looking for work before it knows whose it is. Filtered so it stays small as history grows, and
        // written unquoted so SQL Server and SQLite both accept it.
        builder.HasIndex(reservation => reservation.ExpiresAt)
            .HasFilter("ReleasedAt IS NULL")
            .HasDatabaseName("IX_AccountAiQuotaReservations_Outstanding");

        // The posting backstop: decided but not yet applied to a period. The same shape and the same reasoning
        // as the sweep above -- it finds what a worker did not live long enough to finish.
        builder.HasIndex(reservation => reservation.SettledAt)
            .HasFilter("PostedAt IS NULL")
            .HasDatabaseName("IX_AccountAiQuotaReservations_Unposted");

        // The operator's leaderboard (USAGE-009): charged spend across every account in a window. It leads with
        // PostedAt rather than the account because the question is "who, in this window" — the window is the
        // seek and the account is the grouping. Every other index here leads with something this query does not
        // know, so without it the only plan is a scan of every reservation ever posted.
        builder.HasIndex(reservation => new { reservation.PostedAt, reservation.AccountId })
            .HasFilter("PostedAt IS NOT NULL")
            .HasDatabaseName("IX_AccountAiQuotaReservations_Posted_Account");
    }
}
