namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>What happens to an allowance the account did not spend when a period rolls.</summary>
public enum AiQuotaCarryOver
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>Unspent allowance expires at the boundary. The new period starts at its own allowance.</summary>
    None = 1,

    /// <summary>
    /// Unspent allowance is added to the next period, up to <c>AccountAiQuota.CarryOverCap</c>.
    /// </summary>
    /// <remarks>
    /// The cap is required rather than optional for this member: without one, an account that uses nothing
    /// accumulates an unbounded balance, and the first month it does use the service it is effectively
    /// unmetered. The amount actually brought across is recorded on the new period as
    /// <c>AccountAiQuotaPeriod.CarriedOver</c>, so a roll is readable after the fact rather than re-derived.
    /// </remarks>
    Unused = 2,
}
