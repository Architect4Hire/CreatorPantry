namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>What put an <c>AccountPlan</c> row in place (AP.4.1).</summary>
/// <remarks>
/// Who wrote the row, not a provider reference: a Stripe subscription id lives in Phase 6's integration records,
/// never here. The default plan has no member, because an account on the default has no row to be the source of.
/// </remarks>
public enum AccountPlanSource
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>A platform administrator, through an ops route (AP.4.4).</summary>
    Ops = 1,

    /// <summary>A verified Stripe webhook (AP.6.2).</summary>
    Stripe = 2,

    /// <summary>The sign-up trial: its grant, and its lapse when it runs out (AP.7.1).</summary>
    Trial = 3,
}
