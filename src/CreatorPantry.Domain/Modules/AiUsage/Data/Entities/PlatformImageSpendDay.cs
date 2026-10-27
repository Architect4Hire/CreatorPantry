namespace CreatorPantry.Domain.Modules.AiUsage.Data.Entities;

/// <summary>
/// What the whole platform has admitted in pictures on one UTC day, estimated at provider list price, and the
/// state of the breaker that stops it passing <c>Ai:Quota:PlatformImageSpendPerDay</c> (PRICE-005, AP.2.7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One row a day, shared by every API and Worker instance.</strong> The counter has to be atomic across
/// processes, which rules memory out, and it has to survive a restart, which is why it is a SQL row rather than
/// a Redis key: a cache that came back empty would re-arm a breaker that had already tripped with the whole
/// ceiling available again. It is charged in the same transaction as the account hold that admits the picture
/// (<c>IAiQuotaDataLayer.ReserveAsync</c>), so the two cannot disagree.
/// </para>
/// <para>
/// <strong>Whole micro-dollars, not a decimal.</strong> The charge is one conditional <c>UPDATE</c> — add the
/// picture's price where the sum stays under the ceiling — and SQLite, which several hosted suites run on,
/// cannot compare or add decimals in SQL. An integer adds exactly on both, and a list price is quoted to far
/// fewer than six places.
/// </para>
/// <para>
/// <strong>Platform-scoped and content-free.</strong> No <c>WorkspaceId</c> and no account: it is a single
/// number about everybody, which is exactly why no creator surface may ever read it. Only the ops routes do.
/// </para>
/// <para>
/// No <c>RowVersion</c>. Every write is a single guarded statement — the charge, the trip and the lift each
/// carry their own condition in the <c>WHERE</c> — so there is no read-modify-write for a token to protect.
/// </para>
/// </remarks>
public class PlatformImageSpendDay
{
    /// <summary>The UTC calendar day. The key: there is one row per day, created by the first admission.</summary>
    public DateOnly Day { get; set; }

    /// <summary>
    /// The list-price estimate of every picture admitted this day, in millionths of a US dollar.
    /// </summary>
    /// <remarks>
    /// What admission counted, not what providers billed: a request is counted when it is admitted and given
    /// back only when its operation was never queued. A partial pass or a refused variant is not given back, so
    /// the figure can read high and never low — the safe direction for a breaker.
    /// </remarks>
    public long AdmittedMicros { get; set; }

    /// <summary>When the first request of the day was refused for the ceiling. Null until then.</summary>
    /// <remarks>
    /// Set once, by whichever refusal wins <c>WHERE TrippedAt IS NULL</c>, and that winner alone writes the
    /// platform audit event and the alerting log line — so a burst of refusals raises one alert, not hundreds.
    /// From then on the day admits nothing, even a request that would still fit, until it is lifted or ends.
    /// </remarks>
    public DateTimeOffset? TrippedAt { get; set; }

    /// <summary>When an operator lifted the breaker for this day. Null while it is armed.</summary>
    /// <remarks>
    /// A lift lasts until the UTC day ends; the next day's row starts armed. Admissions after a lift are still
    /// counted, so the figure keeps saying what the day actually cost.
    /// </remarks>
    public DateTimeOffset? LiftedAt { get; set; }

    /// <summary>The ops client that lifted it, by name, so the read can say who without a join to the audit log.</summary>
    public string? LiftedBy { get; set; }
}
