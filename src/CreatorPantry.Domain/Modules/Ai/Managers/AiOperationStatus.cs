namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Where one AI operation has got to. <see cref="AiOperationTransitionPolicy"/> owns which moves between
/// these are legal; this enum only names them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Requested"/> is zero, unlike every other enum in this module, and the difference is deliberate.
/// A default <c>TaskType</c> or <c>Scope</c> would silently mislabel what an operation is allowed to do, so
/// those reject their zero value; a default status is simply the state every operation genuinely starts in,
/// and treating "unset" as "requested" is correct rather than dangerous.
/// </para>
/// <para>
/// Five of the eight are terminal. Retrying a failed operation writes a <em>new</em> row with a new
/// idempotency key rather than reviving this one — a row that changed its mind about what happened to it
/// would make the provenance recorded against an accepted proposal ambiguous.
/// </para>
/// </remarks>
public enum AiOperationStatus
{
    /// <summary>Recorded and queued; no provider has been called.</summary>
    Requested = 0,

    /// <summary>Claimed by a worker and in flight.</summary>
    Running = 1,

    /// <summary>The model returned, the output validated, and a proposal is waiting for the creator.</summary>
    Proposed = 2,

    /// <summary>The creator accepted every proposed change. Terminal.</summary>
    Accepted = 3,

    /// <summary>
    /// The creator accepted some proposed changes and left the rest. Terminal: the source version has moved
    /// on, so the skipped changes were computed against a version that no longer exists and cannot simply be
    /// offered again. Accepting them later is a new operation against the new source.
    /// </summary>
    PartiallyAccepted = 4,

    /// <summary>The creator rejected the proposal. Terminal, and a perfectly good outcome.</summary>
    Rejected = 5,

    /// <summary>
    /// The operation could not produce a proposal. <c>AiOperation.FailureCategory</c> says why, and is
    /// required in exactly this state. Terminal.
    /// </summary>
    Failed = 6,

    /// <summary>
    /// Time ran out — either nobody claimed the request, or nobody acted on the proposal. Terminal, and the
    /// only state reachable from two different places.
    /// </summary>
    Expired = 7,
}
