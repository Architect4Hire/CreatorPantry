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

    /// <summary>
    /// A credit figure as <c>AccountCreditGrant</c> stores it: the width <c>AiUsagePolicy.AllowancePrecision</c>
    /// and <c>AllowanceScale</c> give a period's allowance, because a grant's credits are spent beside it (AP.5.2)
    /// and a picture costs a fraction of one (87.5, B-49). Restated for the reason <see cref="AccountIdMaxLength"/>
    /// is, and held equal by <c>AccountCreditGrantModelShapeTests</c>.
    /// </summary>
    public const int CreditPrecision = 18;

    /// <inheritdoc cref="CreditPrecision"/>
    public const int CreditScale = 4;

    /// <summary>
    /// An external reference on a grant: a Stripe checkout session id (AP.6.3), or an operator's ticket. The width
    /// of an <c>Idempotency-Key</c>, which is the other client-supplied identifier the platform stores.
    /// </summary>
    public const int ExternalReferenceMaxLength = 255;

    /// <summary>
    /// How often one stored row naming a plan the catalogue no longer has is warned about, per process.
    /// </summary>
    public static readonly TimeSpan RemovedPlanWarningInterval = TimeSpan.FromHours(1);
}
