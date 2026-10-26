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
}

/// <summary>Prices per million tokens, by model name, from configuration.</summary>
public sealed class AiCostOptions
{
    public const string SectionName = "Ai:Cost";

    /// <summary>Model name to price. A model absent from here has no estimate, which is honest.</summary>
    public Dictionary<string, AiModelPrice> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
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
}
