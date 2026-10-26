using System.Text.RegularExpressions;

namespace CreatorPantry.Domain.Managers.Ai;

/// <summary>
/// The names a provider's reported model id may be priced under, most specific first.
/// </summary>
/// <remarks>
/// <para>
/// A rate is configured once per model (<c>gpt-5.4-nano</c>), but the model id a response carries is what
/// settlement and cost estimation are handed, and providers decorate it: Azure reports the dated snapshot a
/// deployment runs (<c>gpt-4o-mini-2024-07-18</c>) and Foundry Local appends a catalogue version
/// (<c>phi-4-mini-instruct-generic-gpu:5</c>). An exact match alone would leave every such call unpriced, and an
/// unpriced call settles at the flat estimate rather than at what it used.
/// </para>
/// <para>
/// Deterministic and narrow on purpose (ai.md: identifier resolution is code). Only the two decorations above
/// are removed, and the exact id is always tried first, so a rate configured for one snapshot wins over the
/// model's general rate. Nothing is matched by prefix: <c>gpt-5.4</c> must never price <c>gpt-5.4-nano</c>.
/// </para>
/// </remarks>
public static partial class AiModelRateKeys
{
    /// <summary>The keys to try, in order, for <paramref name="modelId"/>; empty for a blank id.</summary>
    public static IReadOnlyList<string> Candidates(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return [];
        }

        var exact = modelId.Trim();
        var unversioned = CatalogueVersion().Replace(exact, string.Empty);
        var undated = SnapshotDate().Replace(unversioned, string.Empty);

        return new[] { exact, unversioned, undated }
            .Where(candidate => candidate.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The first candidate for <paramref name="modelId"/> that <paramref name="table"/> holds, or false.
    /// </summary>
    public static bool TryFind<TValue>(
        IReadOnlyDictionary<string, TValue> table,
        string? modelId,
        out TValue value)
    {
        ArgumentNullException.ThrowIfNull(table);

        foreach (var candidate in Candidates(modelId))
        {
            if (table.TryGetValue(candidate, out value!))
            {
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>Foundry Local's <c>:N</c> catalogue version.</summary>
    [GeneratedRegex(@":\d+$")]
    private static partial Regex CatalogueVersion();

    /// <summary>Azure's <c>-YYYY-MM-DD</c> snapshot date.</summary>
    [GeneratedRegex(@"-\d{4}-\d{2}-\d{2}$")]
    private static partial Regex SnapshotDate();
}
