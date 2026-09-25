namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which status changes an AI operation may make, and what each one is called in the audit trail.
/// </summary>
/// <remarks>
/// <para>
/// A pure table, deliberately: no clock, no context, no persistence. That is what lets every one of the
/// sixty-four ordered pairs be asserted directly, rather than inferred from whichever paths a seam happens to
/// exercise. The database repeats the terminal half of this table as a check constraint, and
/// <c>AiOperationConfiguration</c> reads <see cref="TerminalStatuses"/> to build it, so the two cannot drift.
/// </para>
/// <para>
/// Five states are terminal. Retrying a failed operation writes a new row; the only edge that ever moves
/// backwards is <see cref="AiOperationStatus.Running"/> to <see cref="AiOperationStatus.Requested"/>, and it
/// exists for exactly one reason — see <see cref="Allowed"/>.
/// </para>
/// </remarks>
public static class AiOperationTransitionPolicy
{
    /// <summary>The prefix every audit action in this module shares.</summary>
    private const string ActionPrefix = "ai.operation.";

    /// <remarks>
    /// <para>
    /// The <see cref="AiOperationStatus.Running"/> to <see cref="AiOperationStatus.Requested"/> edge is lease
    /// recovery, and it is the only cycle in this table. A worker that dies mid-flight would otherwise strand
    /// the creator's request in <see cref="AiOperationStatus.Running"/> forever; returning it to the queue
    /// lets another worker pick it up, which is what a creator would expect a crash to cost them.
    /// </para>
    /// <para>
    /// <strong>It needs a bound that does not exist yet.</strong> A task that crashes its worker every time
    /// will cycle between these two states indefinitely, because nothing here counts attempts. The attempt
    /// counter belongs with the lease columns, which arrive with the durable job claim — and until those
    /// exist there is no recovery mechanism to loop, so there is nothing to bound today. It must not ship
    /// without one.
    /// </para>
    /// <para>
    /// <see cref="AiOperationStatus.Running"/> has no edge to <see cref="AiOperationStatus.Expired"/>: an
    /// abandoned lease goes back to <see cref="AiOperationStatus.Requested"/>, and the overall time limit is
    /// enforced there. One expiry rule in one place.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<AiOperationStatus, AiOperationStatus[]> Allowed = new()
    {
        [AiOperationStatus.Requested] =
        [
            AiOperationStatus.Running,
            AiOperationStatus.Failed,
            AiOperationStatus.Expired,
        ],
        [AiOperationStatus.Running] =
        [
            AiOperationStatus.Proposed,
            AiOperationStatus.Failed,
            AiOperationStatus.Requested,
        ],
        [AiOperationStatus.Proposed] =
        [
            AiOperationStatus.Accepted,
            AiOperationStatus.PartiallyAccepted,
            AiOperationStatus.Rejected,
            AiOperationStatus.Expired,
        ],
        [AiOperationStatus.Accepted] = [],
        [AiOperationStatus.PartiallyAccepted] = [],
        [AiOperationStatus.Rejected] = [],
        [AiOperationStatus.Failed] = [],
        [AiOperationStatus.Expired] = [],
    };

    /// <summary>
    /// The states an operation never leaves. <c>CompletedAt</c> is set in exactly these, and the database
    /// check constraint is generated from this set.
    /// </summary>
    public static IReadOnlySet<AiOperationStatus> TerminalStatuses { get; } =
        Allowed.Where(entry => entry.Value.Length == 0).Select(entry => entry.Key).ToHashSet();

    /// <summary>Every status, so callers and tests enumerate the same list this table was built from.</summary>
    public static IReadOnlyList<AiOperationStatus> AllStatuses { get; } = Enum.GetValues<AiOperationStatus>();

    /// <summary>Whether <paramref name="status"/> is a state the operation never leaves.</summary>
    public static bool IsTerminal(AiOperationStatus status) => TerminalStatuses.Contains(status);

    /// <summary>The statuses reachable in one step from <paramref name="from"/>; empty when terminal.</summary>
    public static IReadOnlyList<AiOperationStatus> AllowedFrom(AiOperationStatus from) =>
        Allowed.TryGetValue(from, out var allowed) ? allowed : [];

    /// <summary>
    /// Whether the operation may move from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// A status change to the status it already holds is <em>not</em> allowed. It reads harmless, but every
    /// transition appends audit evidence, and a no-op that writes an audit row saying nothing changed makes
    /// the trail harder to read rather than more complete.
    /// </remarks>
    public static bool IsAllowed(AiOperationStatus from, AiOperationStatus to) =>
        Array.IndexOf(Allowed[from], to) >= 0;

    /// <summary>
    /// The stable audit action code for one transition, e.g. <c>ai.operation.proposed</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The transition is not allowed.</exception>
    /// <remarks>
    /// Keyed on both ends rather than the destination alone, because two edges arrive at
    /// <see cref="AiOperationStatus.Requested"/> and they are not the same event. An operation being created
    /// and an abandoned lease being returned to the queue must not share an action code, or the audit trail
    /// cannot distinguish a creator's request from a worker's crash.
    /// </remarks>
    public static string AuditAction(AiOperationStatus from, AiOperationStatus to)
    {
        if (!IsAllowed(from, to))
        {
            throw new ArgumentOutOfRangeException(
                nameof(to), to, $"An AI operation cannot move from {from} to {to}.");
        }

        return ActionPrefix + (from, to) switch
        {
            (AiOperationStatus.Running, AiOperationStatus.Requested) => "requeued",
            (_, AiOperationStatus.Running) => "started",
            (_, AiOperationStatus.Proposed) => "proposed",
            (_, AiOperationStatus.Accepted) => "accepted",
            (_, AiOperationStatus.PartiallyAccepted) => "partially-accepted",
            (_, AiOperationStatus.Rejected) => "rejected",
            (_, AiOperationStatus.Failed) => "failed",
            (_, AiOperationStatus.Expired) => "expired",

            // Unreachable: IsAllowed has already refused every pair not covered above. Present so that
            // adding a status without an action code fails here rather than producing "ai.operation.".
            _ => throw new ArgumentOutOfRangeException(
                nameof(to), to, $"No audit action is defined for {from} to {to}."),
        };
    }

    /// <summary>
    /// The audit trail's before/after pointers for one transition: the status names, and nothing else.
    /// </summary>
    /// <remarks>
    /// Names, not content. An audit reference may never carry a prompt body, a recipe body, or a provider
    /// payload, and a status transition has nothing else worth recording — the operation id is already on the
    /// audit row.
    /// </remarks>
    public static (string Before, string After) AuditReferences(
        AiOperationStatus from,
        AiOperationStatus to) => (from.ToString(), to.ToString());
}
