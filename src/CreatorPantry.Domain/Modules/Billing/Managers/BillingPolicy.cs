namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>The Billing module's fixed limits: column widths that configuration and the schema must agree on.</summary>
public static class BillingPolicy
{
    /// <summary>
    /// An Identity account id. The same width as <c>AiUsagePolicy.AccountIdMaxLength</c> and Identity's own key;
    /// restated rather than referenced because a module's policy does not cross a module boundary, and
    /// <c>AccountPlanModelShapeTests</c> holds the two equal.
    /// </summary>
    public const int AccountIdMaxLength = 450;

    /// <summary>
    /// A plan key as <c>AccountPlan.PlanKey</c> stores it. Startup refuses a catalogue key longer than this, so a
    /// plan the catalogue offers is always one an account can be put on.
    /// </summary>
    public const int PlanKeyMaxLength = 64;
}
