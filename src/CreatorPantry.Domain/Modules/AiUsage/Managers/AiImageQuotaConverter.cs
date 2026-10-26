using CreatorPantry.Domain.Managers.Ai;

namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// Turns one image request into the unit a period is denominated in (AP.2.4).
/// </summary>
/// <remarks>
/// <para>
/// Deterministic domain code, for the same reason <see cref="AiQuotaUnitConverter"/> is: ai.md puts quota
/// enforcement and arithmetic among the things a model may never do.
/// </para>
/// <para>
/// A picture differs from a completion in that its cost is known <em>before</em> it is made — the provider
/// prices the model, the resolution tier and the quality, not the output — so the same figure serves admission
/// and settlement. There is no estimate to fall back to and deliberately no default: an image with no rate is
/// refused at admission rather than reserved at nothing (PRICE-001, <c>ai-pricing.md</c> B-49).
/// </para>
/// <para>
/// <strong>Nothing here rounds</strong>, on the same grounds as its sibling: a caller sums the variants a
/// request asks for and rounds the total once with <see cref="AiQuotaUnitConverter.Round"/>.
/// </para>
/// </remarks>
public static class AiImageQuotaConverter
{
    /// <summary>
    /// What one image charges, or null when this deployment has no rate for it.
    /// </summary>
    /// <param name="unit">The unit the period is denominated in.</param>
    /// <param name="modelName">The image model the request names.</param>
    /// <param name="resolution">The resolution tier the request carries, or null when the model takes none.</param>
    /// <param name="quality">The quality the request carries, or null when the model offers none.</param>
    /// <param name="options">The deployment's rates.</param>
    /// <remarks>
    /// <para>
    /// Null means refused, not free — the one reading of USAGE-005 this module keeps throughout. Startup
    /// (<c>AiProviderRegistration.RequireImageRates</c>) has already refused to run a host that can request a
    /// picture it has no rate for, so a null here is a request for something no profile offers: a model this
    /// deployment does not send to, a quality it does not list, a quality named with no tier, or a name holding
    /// the key separator. A row of nought reads the same way, which is the guard for a configuration that
    /// changed after the startup check ran.
    /// </para>
    /// <para>
    /// A token-denominated period reads the rate through <see cref="AiQuotaUnitConverter.Estimate"/>, because an
    /// image reports no tokens of its own and the rates are written in credits. That unit is declared rather
    /// than used and carries every caveat <see cref="AiQuotaUnit.Tokens"/> lists.
    /// </para>
    /// </remarks>
    public static decimal? Charge(
        AiQuotaUnit unit,
        string? modelName,
        string? resolution,
        string? quality,
        AiQuotaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The grammar takes no fallback, so this is the rate for exactly what was asked for, and Figure reads a
        // row of nought as no rate rather than as a free picture: see AiImageRateKeys.
        return AiImageRateKeys.Figure(options.ImageRates, modelName, resolution, quality) is { } credits
            ? AiQuotaUnitConverter.Estimate(unit, credits, options)
            : null;
    }
}
