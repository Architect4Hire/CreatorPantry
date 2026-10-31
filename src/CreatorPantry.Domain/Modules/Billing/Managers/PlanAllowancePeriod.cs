namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>How often a plan's <see cref="PlanOption.Credits"/> are granted (AP.4.1).</summary>
/// <remarks>
/// Billing's own word for it rather than the AiUsage module's period length, which does not cross a module
/// boundary. AP.4.2 translates it when the plan starts driving the allowance.
/// </remarks>
public enum PlanAllowancePeriod
{
    /// <summary>Not declared. Startup refuses a plan left on it.</summary>
    Unspecified = 0,

    /// <summary>Granted afresh every month, however the plan is paid for.</summary>
    Monthly = 1,

    /// <summary>
    /// Granted once and never renewed: the trial's (B-48, "granted once, not per period"). Required on a plan with
    /// <see cref="PlanOption.TrialDays"/> and refused on any other.
    /// </summary>
    Once = 2,
}
