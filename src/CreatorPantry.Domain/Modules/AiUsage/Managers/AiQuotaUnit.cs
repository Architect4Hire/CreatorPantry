namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// What an allowance and a period's totals are denominated in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The unit is frozen onto a period as well as onto the quota</strong>, which is why this needs to be
/// a column at all rather than a constant. An administrator who changes an account's unit changes what
/// <em>new</em> periods accumulate in; the running period's totals were accumulated in the old unit and the
/// two are not comparable, so a change takes effect at the next roll instead of corrupting the current one.
/// </para>
/// <para>
/// The conversion from provider-reported tokens into whichever unit is in force is configuration applied by
/// domain code at settlement, on the same footing as <c>Ai:Cost</c>. A model never participates in it and
/// never sees a balance (ai.md).
/// </para>
/// </remarks>
public enum AiQuotaUnit
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>
    /// The unit CreatorPantry meters in. A weighted, model-normalized measure of work, so that an allowance
    /// means the same thing whichever model served it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Raw tokens were rejected as the default deliberately.</strong> A token is not a unit of value:
    /// an output token costs several times an input token, and a token on one model costs an order of
    /// magnitude more than on another — which the creator can neither see nor choose. Metering in tokens
    /// would let a model swap silently re-price everyone's allowance.
    /// </para>
    /// <para>
    /// USAGE-010 also requires the creator-facing figure to be credits and a reset date rather than a raw
    /// token count, so a token-denominated quota would need the conversion anyway, in the presentation layer,
    /// which is the worst place for it.
    /// </para>
    /// </remarks>
    Credits = 1,

    /// <summary>
    /// Raw provider-reported tokens, for a deployment that genuinely wants to meter the underlying resource.
    /// </summary>
    /// <remarks>
    /// Declared rather than used. It carries every caveat in <see cref="Credits"/>' remarks, and an account
    /// metered this way cannot be compared with one metered in credits — which is exactly what the frozen
    /// per-period unit exists to keep straight.
    /// </remarks>
    Tokens = 2,
}
