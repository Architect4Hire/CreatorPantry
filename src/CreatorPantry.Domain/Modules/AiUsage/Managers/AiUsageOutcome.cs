namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// How the provider attempt a ledger entry records ended.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Deliberately not <c>AiFailureCategory</c>.</strong> That enum exists to answer "is this worth
/// retrying?" and grows with every distinct provider failure mode worth routing on. This one answers "should
/// this attempt have cost anything, and do we know what it cost?", which is a smaller and much more stable
/// question — and a platform ledger that adopted the AI module's retry vocabulary would have to migrate every
/// time that vocabulary learned a new provider quirk.
/// </para>
/// <para>
/// A failed, cancelled, timed-out or blocked attempt still posts an entry (USAGE-005). An attempt that cost
/// tokens and then failed cost those tokens, and an attempt whose cost is unknown must be visible as unknown
/// rather than absent — which is why this is an outcome column and not a filter applied before writing.
/// </para>
/// </remarks>
public enum AiUsageOutcome
{
    /// <summary>
    /// Not declared. Never valid on a stored row — the check constraint refuses it — so a row that forgot to
    /// say how its attempt ended cannot pass for a successful one.
    /// </summary>
    Unspecified = 0,

    /// <summary>The provider answered and the attempt completed.</summary>
    Succeeded = 1,

    /// <summary>The attempt ended in an error. It may still have consumed tokens before it did.</summary>
    Failed = 2,

    /// <summary>The caller or the operation lease cancelled the attempt before it finished.</summary>
    Cancelled = 3,

    /// <summary>The provider did not answer in time.</summary>
    TimedOut = 4,

    /// <summary>The provider or a safety check blocked or altered the response.</summary>
    SafetyBlocked = 5,
}
