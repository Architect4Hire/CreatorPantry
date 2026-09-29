using CreatorPantry.Domain.Modules.AiUsage.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// How one provider attempt is described to the account ledger: how it ended, and whether it counts against an
/// allowance.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Extracted so there is one copy.</strong> Two paths post to the ledger — the live one, which settles
/// an attempt as it finishes, and 9A.7's reconciliation, which posts attempts written before that path existed.
/// Both have to answer these two questions identically, and a billing rule kept in two places drifts: the day
/// they disagree, the same attempt is billable settled and unbillable reconciled, and nothing would say which
/// answer the period was built from.
/// </para>
/// <para>
/// Takes fields rather than an entity, because only one of the two callers has one. The other reads a
/// projection deliberately narrow enough to carry no creator content.
/// </para>
/// </remarks>
public static class AiUsageAttribution
{
    /// <summary>
    /// How the attempt ended, in the ledger's smaller vocabulary.
    /// </summary>
    /// <remarks>
    /// A blocked attempt is reported as blocked whichever way it was detected — <c>SafetyBlocked</c> is set by
    /// the provider flagging the response, and the failure category by a check refusing it — because a
    /// creator-facing "this was blocked" should not depend on which of the two noticed.
    /// </remarks>
    public static AiUsageOutcome OutcomeOf(bool safetyBlocked, AiFailureCategory? failureCategory) =>
        safetyBlocked || failureCategory is AiFailureCategory.SafetyBlocked
            ? AiUsageOutcome.SafetyBlocked
            : failureCategory switch
            {
                null => AiUsageOutcome.Succeeded,
                AiFailureCategory.Timeout => AiUsageOutcome.TimedOut,
                AiFailureCategory.Cancelled => AiUsageOutcome.Cancelled,
                _ => AiUsageOutcome.Failed,
            };

    /// <summary>
    /// Whether this attempt counts against the account's allowance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// False only when the attempt was <em>structurally incapable</em> of costing anything: a
    /// <see cref="AiTaskType.Diagnostic"/> task never calls a model, and the five categories below all fail
    /// before a provider is reached.
    /// </para>
    /// <para>
    /// Everything else is billable, including <see cref="AiFailureCategory.Timeout"/> and
    /// <see cref="AiFailureCategory.RateLimited"/>. That is deliberate and it is the conservative reading: a
    /// call that timed out may well have been served and charged for, and treating an unknown cost as free is
    /// exactly the silent under-attribution USAGE-005 exists to prevent.
    /// </para>
    /// </remarks>
    public static bool IsBillable(AiTaskType taskType, AiFailureCategory? failureCategory) =>
        taskType is not AiTaskType.Diagnostic
        && failureCategory is not (AiFailureCategory.Validation
            or AiFailureCategory.Quota
            or AiFailureCategory.AccountSuspended
            or AiFailureCategory.TemplateUnavailable
            or AiFailureCategory.LeaseAbandoned);
}
