using System.Collections.Concurrent;
using CreatorPantry.Domain.Managers.Ai;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Ai.Gateways;

/// <summary>What one attempt is believed to have cost.</summary>
/// <remarks>
/// An estimate, and never anything else. Nothing bills from it, and a model with no configured price returns
/// null rather than zero — an unknown cost is not a free one, and recording zero would make a cost report
/// quietly wrong rather than visibly incomplete.
/// </remarks>
public interface IAiCostEstimator
{
    decimal? Estimate(string modelName, int? inputTokens, int? outputTokens);

    /// <summary>
    /// What one image is believed to have cost, or null when this deployment has no price for it (AP.2.4).
    /// </summary>
    /// <remarks>
    /// Priced before it is made rather than after, because a provider prices the model, the resolution tier and
    /// the quality rather than the output. The credits charged for the same picture are
    /// <c>AiImageQuotaConverter</c>'s; this is the provider cost behind them.
    /// </remarks>
    decimal? EstimateImage(string modelName, string? resolution, string? quality);
}

/// <summary>Prices per million tokens, by model name, from configuration.</summary>
public sealed class AiCostOptions
{
    public const string SectionName = "Ai:Cost";

    /// <summary>Model name to price. A model absent from here has no estimate, which is honest.</summary>
    public Dictionary<string, AiModelPrice> Models { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// List price in currency units per image, keyed by <see cref="AiImageRateKeys"/> (AP.2.4).
    /// </summary>
    /// <remarks>
    /// The same rows as <c>Ai:Quota:ImageRates</c>, at list price rather than at the pinned credit unit: each
    /// rate there is its price here × 1,250 (<c>ai-pricing.md</c> B-49), and <c>ImageRateTableTests</c> holds
    /// the two together. Startup requires both, so a picture this deployment can request is never charged
    /// credits whose provider cost nobody can see.
    /// </remarks>
    public Dictionary<string, decimal> Images { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <param name="InputPerMillionTokens">Currency units per million input tokens.</param>
/// <param name="OutputPerMillionTokens">Currency units per million output tokens.</param>
public sealed record AiModelPrice(decimal InputPerMillionTokens, decimal OutputPerMillionTokens);

/// <inheritdoc cref="IAiCostEstimator"/>
/// <remarks>
/// A model that reports usage and has no price is said once per model id, as a warning. It is how a deployment
/// repointed at a model nobody priced becomes visible: the call still runs, its cost reads null and its credits
/// settle at the estimate, none of which anyone would otherwise notice. Only the id is logged.
/// </remarks>
public sealed class ConfiguredAiCostEstimator(
    AiCostOptions options,
    ILogger<ConfiguredAiCostEstimator>? logger = null) : IAiCostEstimator
{
    private const decimal TokensPerPriceUnit = 1_000_000m;

    private readonly ConcurrentDictionary<string, bool> _unpriced = new(StringComparer.OrdinalIgnoreCase);

    // Its own, so an image key can never suppress a model's warning or the reverse: the two namespaces are
    // configured apart and a picture's key is request-influenced where a model name is not.
    private readonly ConcurrentDictionary<string, bool> _unpricedImages = new(StringComparer.OrdinalIgnoreCase);

    public decimal? Estimate(string modelName, int? inputTokens, int? outputTokens)
    {
        // No usage reported means no estimate. Treating an absent count as zero would report a real call as
        // free, which is worse than reporting nothing.
        if (string.IsNullOrEmpty(modelName) || (inputTokens is null && outputTokens is null))
        {
            return null;
        }

        if (!AiModelRateKeys.TryFind(options.Models, modelName, out var price))
        {
            if (_unpriced.TryAdd(modelName, true))
            {
                logger?.LogWarning(
                    "Model {ModelName} reported usage but has no price under Ai:Cost:Models; its cost reads null, "
                    + "and unless Ai:Quota:ModelRates rates it, its credits settle at the per-call estimate.",
                    modelName);
            }

            return null;
        }

        var input = (inputTokens ?? 0) / TokensPerPriceUnit * price.InputPerMillionTokens;
        var output = (outputTokens ?? 0) / TokensPerPriceUnit * price.OutputPerMillionTokens;

        // Six places, matching the column. A single short call costs a fraction of a cent, and rounding it to
        // currency precision would record every one of them as nothing.
        return Math.Round(input + output, 6, MidpointRounding.AwayFromZero);
    }

    public decimal? EstimateImage(string modelName, string? resolution, string? quality)
    {
        // Figure, not TryFind: a price of nought is a picture nobody priced, not one the provider gives away.
        if (AiImageRateKeys.Figure(options.Images, modelName, resolution, quality) is { } price)
        {
            return Math.Round(price, 6, MidpointRounding.AwayFromZero);
        }

        // Said once per key, as an unpriced model is. Unlike a completion this cannot be reached by a repointed
        // deployment — startup refuses a host that can request an unpriced picture — so it means a request for
        // something no sizing profile offers, and the admission that reads AiImageRateKeys has refused it.
        var key = AiImageRateKeys.Key(modelName, resolution, quality);

        if (key.Length > 0 && _unpricedImages.TryAdd(key, true))
        {
            logger?.LogWarning(
                "Image {ImageRateKey} has no price under Ai:Cost:Images, so its cost reads null; "
                + "Ai:Quota:ImageRates will have refused the request rather than charging nothing for it.",
                key);
        }

        return null;
    }
}
