namespace CreatorPantry.Domain.Modules.AiUsage.Managers;

/// <summary>
/// Turns reported token counts, and configured credit estimates, into the unit a period is denominated in.
/// </summary>
/// <remarks>
/// <para>
/// Deterministic domain code, not a model's judgement: ai.md lists identifier resolution, quota enforcement and
/// arithmetic among the things a model may never do, and an allowance is all three at once.
/// </para>
/// <para>
/// <strong>Nothing here rounds.</strong> Callers sum raw conversions across a run's attempts and round the
/// total once, which is what <see cref="AiUsagePolicy.AllowanceScale"/>'s remarks say the scale exists for —
/// rounding each attempt would accumulate the error across a long run.
/// </para>
/// </remarks>
public static class AiQuotaUnitConverter
{
    private const decimal TokensPerRateUnit = 1_000_000m;

    /// <summary>
    /// What one attempt's reported tokens charge, or null when they cannot be converted.
    /// </summary>
    /// <param name="unit">The unit the period is denominated in.</param>
    /// <param name="modelName">The model that served the attempt.</param>
    /// <param name="inputTokens">Reported input tokens, or null when the provider reported none.</param>
    /// <param name="outputTokens">Reported output tokens, or null when the provider reported none.</param>
    /// <param name="options">The deployment's rates.</param>
    /// <remarks>
    /// <para>
    /// Null has one meaning and it is not zero: either the provider reported nothing, or the model has no
    /// configured rate. The caller charges its estimate in both cases, because a call whose cost is unknown is
    /// not a call that was free — the same reading of USAGE-005 the ledger's nullable counts encode.
    /// </para>
    /// <para>
    /// A token-denominated period takes the counts as they are. That unit is declared rather than used and
    /// carries every caveat <see cref="AiQuotaUnit.Tokens"/> lists, but a period opened in it still has to
    /// settle correctly.
    /// </para>
    /// </remarks>
    public static decimal? Charge(
        AiQuotaUnit unit,
        string modelName,
        int? inputTokens,
        int? outputTokens,
        AiQuotaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (inputTokens is null && outputTokens is null)
        {
            return null;
        }

        if (unit is AiQuotaUnit.Tokens)
        {
            return (inputTokens ?? 0) + (outputTokens ?? 0);
        }

        if (string.IsNullOrEmpty(modelName) || !options.ModelRates.TryGetValue(modelName, out var rate))
        {
            return null;
        }

        return ((inputTokens ?? 0) / TokensPerRateUnit * rate.CreditsPerMillionInputTokens)
            + ((outputTokens ?? 0) / TokensPerRateUnit * rate.CreditsPerMillionOutputTokens);
    }

    /// <summary>
    /// A configured per-call estimate, which is written in credits, expressed in
    /// <paramref name="unit"/>.
    /// </summary>
    public static decimal Estimate(AiQuotaUnit unit, decimal credits, AiQuotaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return unit is AiQuotaUnit.Tokens ? credits * options.TokensPerCredit : credits;
    }

    /// <summary>Rounds a completed total to the scale the allowance columns store.</summary>
    public static decimal Round(decimal amount) =>
        Math.Round(amount, AiUsagePolicy.AllowanceScale, MidpointRounding.AwayFromZero);
}
