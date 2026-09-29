using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Entities;

/// <summary>
/// One account's allowance window and what it has spent inside it. Platform-scoped, and the only row in this
/// module that is written to repeatedly.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The boundaries are stored, never computed from "now" (USAGE-003).</strong>
/// <see cref="EndsAt"/> is derived once, when the period opens, from <see cref="LocalStartDate"/> plus one
/// period length in <see cref="TimeZoneId"/>; the next period's <see cref="StartsAt"/> is <em>this</em> row's
/// stored <see cref="EndsAt"/> copied verbatim rather than re-derived. Consecutive periods therefore abut
/// exactly, and a daylight-saving transition can neither open a gap nor double-count an hour — the period
/// containing a fall-back is thirty days and one hour long, and that is correct rather than a defect.
/// Intervals are half-open: <c>[StartsAt, EndsAt)</c>.
/// </para>
/// <para>
/// <strong>It freezes the terms it opened under.</strong> <see cref="Unit"/>, <see cref="Allowance"/> and
/// <see cref="TimeZoneId"/> are copied from <see cref="AccountAiQuota"/> at open and never re-read, so an
/// administrator changing a quota mid-period cannot retroactively re-price what a creator has already spent
/// against. There is no foreign key back to the quota, deliberately: the terms were copied, not referenced,
/// and a period has to survive the quota that opened it being superseded.
/// </para>
/// <para>
/// <strong>A period is closed by its last reservation, not by the clock.</strong> A reservation taken just
/// before <see cref="EndsAt"/> settles against the period it was reserved in — it cannot drift into the next
/// one, because the reservation names this row. So the totals below may still move after
/// <see cref="EndsAt"/> has passed, and <see cref="SettledAt"/> is what says whether they might. The
/// alternatives are worse: re-reserving into the new period would admit work against an allowance nobody
/// checked, and refusing to reserve near a boundary would make the last minutes of every period silently
/// unusable.
/// </para>
/// <para>
/// Not an <see cref="IImmutableRecord"/>, and the only entity here carrying a <see cref="RowVersion"/>:
/// <see cref="Consumed"/> and <see cref="Reserved"/> are contended counters, and USAGE-004 requires that two
/// workers reserving for the same account cannot both be admitted past the last of the allowance.
/// </para>
/// </remarks>
public class AccountAiQuotaPeriod
{
    public Guid Id { get; set; }

    public string AccountId { get; set; } = string.Empty;

    /// <summary>The instant the period opens. Inclusive.</summary>
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>The instant the period ends. Exclusive, and the next period's <see cref="StartsAt"/>.</summary>
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>
    /// The zone the boundaries above were computed in, copied from the quota at open.
    /// </summary>
    /// <remarks>
    /// Stored rather than read back from the quota so that changing an account's zone does not retro-shift an
    /// open period, and so the read seam can say "resets on <em>this local date</em>" using the zone the
    /// boundary actually came from.
    /// </remarks>
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>
    /// The local calendar date <see cref="StartsAt"/> represents. The period's intent, kept beside the instant
    /// it resolved to, the way publishing.md keeps a scheduled time's local intent beside its UTC.
    /// </summary>
    public DateOnly LocalStartDate { get; set; }

    /// <inheritdoc cref="AccountAiQuota.Unit"/>
    public AiQuotaUnit Unit { get; set; }

    /// <summary>
    /// The allowance in force for this period, in <see cref="Unit"/>.
    /// </summary>
    /// <remarks>
    /// Required, unlike <see cref="AccountAiQuota.Allowance"/>: by the time a period opens the platform
    /// default has been resolved to a number, and this records what was actually spendable rather than where
    /// the number came from.
    /// </remarks>
    public decimal Allowance { get; set; }

    /// <summary>
    /// What the previous period handed over under <see cref="AiQuotaCarryOver.Unused"/>, capped by the
    /// quota's <see cref="AccountAiQuota.CarryOverCap"/>. Zero under
    /// <see cref="AiQuotaCarryOver.None"/>.
    /// </summary>
    /// <remarks>
    /// Recorded rather than re-derived, so a roll can be read after the fact. The period's spendable total is
    /// <see cref="Allowance"/> plus this.
    /// </remarks>
    public decimal CarriedOver { get; set; }

    /// <summary>Settled spend: what attempts in this period actually reported using.</summary>
    public decimal Consumed { get; set; }

    /// <summary>
    /// Allowance currently held by admitted operations that have not settled yet.
    /// </summary>
    /// <remarks>
    /// A balance, not a ledger. Releasing an abandoned reservation on lease expiry and settling exactly once
    /// on a replay both need a reservation record with its own identity to move this number against; that
    /// record arrives with the reserve/settle seam in 9A.5, and this column is not correct without it.
    /// </remarks>
    public decimal Reserved { get; set; }

    /// <summary>
    /// When the last reservation in this period settled or expired, after which the totals are final. Null
    /// while they may still move — including after <see cref="EndsAt"/> has passed.
    /// </summary>
    public DateTimeOffset? SettledAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Not for editing — for competing reservations: two workers reserving for
    /// the same account must not both read the same remaining balance and both be admitted, and the loser
    /// needs a conflict to retry from rather than a silent overwrite.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];
}
