namespace CreatorPantry.Domain.Managers.Ai;

/// <summary>
/// The key one image's credit rate and provider price are configured under (AP.2.4).
/// </summary>
/// <remarks>
/// <para>
/// A picture is priced by the model that makes it and by the fields that model's request actually carries:
/// <c>gpt-image-2-5-flare/4K/high</c> for a model sized by a resolution tier and priced by quality,
/// <c>qwen-image-2-1-turbo/2K</c> for one that takes a tier and offers no qualities, <c>seedream-v5-lite</c> for
/// one sized by aspect ratio alone. The parts a model does not take are absent rather than empty, so a table
/// holds one row per thing that can actually be requested.
/// </para>
/// <para>
/// <strong>The separator is <c>/</c> because <c>:</c> is the configuration section delimiter.</strong> A JSON key
/// holding a colon would bind as nesting and the row would never be found.
/// </para>
/// <para>
/// <strong>Nothing falls back across specificity.</strong> <c>m/4K/high</c> does not fall back to <c>m/4K</c> or to
/// <c>m</c>: a deployment that wrote only the model's name would otherwise pass startup and then charge a 4K
/// high picture at a 1K rate. The exhaustive table is what makes
/// <c>AiProviderRegistration.RequireImageRates</c> mean anything. Only the model's own name is tried more than
/// one way, through <see cref="AiModelRateKeys"/>, so a provider's decorated id still finds its model's row.
/// </para>
/// <para>
/// <strong>A part that holds the separator has no key at all</strong>, rather than one that runs into the next
/// part. A model named <c>m/1K</c> asked for at <c>high</c> would otherwise spell <c>m/1K/high</c> and be
/// charged a tier and quality nobody asked for — the one route left to a wrong rate once fallback is gone.
/// Startup names such a model instead, and a sizing profile may not offer a value holding it.
/// </para>
/// </remarks>
public static class AiImageRateKeys
{
    /// <summary>What separates a key's parts.</summary>
    public const char Separator = '/';

    /// <summary>
    /// The key for <paramref name="modelId"/> at <paramref name="resolution"/> and
    /// <paramref name="quality"/>, exactly as a table should spell it.
    /// </summary>
    /// <remarks>
    /// Empty when there is no key: a blank model, a part holding <see cref="Separator"/>, or a quality with no
    /// resolution, which no Venice sizing kind produces and which would be indistinguishable from a resolution
    /// in the grammar above.
    /// </remarks>
    public static string Key(string? modelId, string? resolution, string? quality)
    {
        if (Unkeyable(modelId) || Unkeyable(resolution) || Unkeyable(quality))
        {
            return string.Empty;
        }

        var model = Stated(modelId);
        var tier = Stated(resolution);
        var grade = Stated(quality);

        if (model is null)
        {
            return string.Empty;
        }

        return (tier, grade) switch
        {
            (null, null) => model,
            (not null, null) => $"{model}{Separator}{tier}",
            (not null, not null) => $"{model}{Separator}{tier}{Separator}{grade}",

            // A quality with no tier. Keying it as the tier it is not would charge another row's rate.
            _ => string.Empty,
        };
    }

    /// <summary>The keys to try, in order, most specific model name first; empty when there is none.</summary>
    public static IReadOnlyList<string> Candidates(string? modelId, string? resolution, string? quality) =>
        Key(modelId, resolution, quality).Length == 0
            ? []
            : AiModelRateKeys.Candidates(modelId)
                .Select(candidate => Key(candidate, resolution, quality))
                .Where(key => key.Length > 0)
                .ToArray();

    /// <summary>The first candidate <paramref name="table"/> holds, or false.</summary>
    public static bool TryFind<TValue>(
        IReadOnlyDictionary<string, TValue> table,
        string? modelId,
        string? resolution,
        string? quality,
        out TValue value)
    {
        ArgumentNullException.ThrowIfNull(table);

        foreach (var candidate in Candidates(modelId, resolution, quality))
        {
            if (table.TryGetValue(candidate, out value!))
            {
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// The figure under the first candidate <paramref name="table"/> holds, when it is above nought; null
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// <strong>A row of nought is no rate, not a free picture.</strong> Startup refuses one by name, and this is
    /// the guard for a configuration that changed after startup: the whole point of the table is that a picture
    /// nobody rated is refused rather than made for nothing, and reserving zero would make the hold meaningless
    /// where it matters most. A negative row would pay a creator to generate.
    /// </remarks>
    public static decimal? Figure(
        IReadOnlyDictionary<string, decimal> table, string? modelId, string? resolution, string? quality) =>
        TryFind(table, modelId, resolution, quality, out var figure) && figure > 0m ? figure : null;

    /// <summary>A part that is stated but cannot be spelled in a key.</summary>
    private static bool Unkeyable(string? value) =>
        value is not null && value.Contains(Separator);

    /// <summary>A part the request states, or null where it states none.</summary>
    private static string? Stated(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
