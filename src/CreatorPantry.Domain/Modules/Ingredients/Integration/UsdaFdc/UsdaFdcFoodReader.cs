using System.Globalization;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Ingredients.Gateways;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Ingredients.Integration.UsdaFdc;

/// <summary>How reading one FoodData Central food ended.</summary>
internal enum UsdaFdcReadResult
{
    Read,

    /// <summary>A real food, from a data set that is not approved (Branded, Survey, Experimental).</summary>
    NotApproved,

    Unreadable,
}

/// <summary>
/// Maps FoodData Central's JSON into evidence records. The only place in the solution that knows FDC's field
/// names, nutrient numbers or measure-unit words.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written against recorded responses, not the published spec</strong>, which differs from what the
/// API returns in ways that matter here (recorded 2026-10-20, <c>Tests/Ingredients/Fixtures/UsdaFdc</c>):
/// </para>
/// <list type="bullet">
/// <item>A food's <c>publicationDate</c> is <c>M/d/yyyy</c>; a search hit's <c>publishedDate</c> is ISO.</item>
/// <item>Unit names vary in case and spelling: <c>mg</c> and <c>MG</c>, <c>µg</c> and <c>UG</c>.</item>
/// <item>
/// SR Legacy foods do carry <c>foodPortions</c>, which the spec omits, and their volume is a word in
/// <c>modifier</c> ("cup", "tbsp, packed") under a measure unit of <c>undetermined</c>. Foundation foods mostly
/// carry only a RACC portion, which is a serving reference, not a measure.
/// </item>
/// <item>Foundation foods may report energy only as Atwater factors (957/958), with no 208 row.</item>
/// </list>
/// <para>
/// <strong>Anything not understood is dropped, never guessed.</strong> A nutrient that is not in
/// <see cref="NutrientCode"/>, reported in a unit other than its own, or with no amount; a portion with no
/// weight or a unit word that is not in the table below. A dropped row is missing evidence, which every
/// reader already has to treat as unknown.
/// </para>
/// </remarks>
internal static class UsdaFdcFoodReader
{
    /// <summary>FDC's measure unit for "the unit is in <c>modifier</c>".</summary>
    private const long UndeterminedMeasureUnit = 9999;

    private static readonly string[] DateFormats = ["M/d/yyyy", "yyyy-MM-dd"];

    /// <summary>
    /// FDC nutrient number → the nutrient it is, and its rank when more than one row reports the same one
    /// (lower wins). Energy prefers the general 208 row, then the Atwater specific factors, then general ones.
    /// </summary>
    private static readonly Dictionary<string, (NutrientCode Code, int Rank)> Nutrients = new(StringComparer.Ordinal)
    {
        ["208"] = (NutrientCode.Energy, 0),
        ["958"] = (NutrientCode.Energy, 1),
        ["957"] = (NutrientCode.Energy, 2),
        ["203"] = (NutrientCode.Protein, 0),
        ["204"] = (NutrientCode.TotalFat, 0),
        ["606"] = (NutrientCode.SaturatedFat, 0),
        ["605"] = (NutrientCode.TransFat, 0),
        ["601"] = (NutrientCode.Cholesterol, 0),
        ["205"] = (NutrientCode.Carbohydrate, 0),
        ["291"] = (NutrientCode.DietaryFiber, 0),
        ["269"] = (NutrientCode.TotalSugars, 0),
        ["269.3"] = (NutrientCode.TotalSugars, 1),
        ["539"] = (NutrientCode.AddedSugars, 0),
        ["307"] = (NutrientCode.Sodium, 0),
        ["306"] = (NutrientCode.Potassium, 0),
        ["301"] = (NutrientCode.Calcium, 0),
        ["303"] = (NutrientCode.Iron, 0),
        ["328"] = (NutrientCode.VitaminD, 0),
    };

    /// <summary>
    /// FDC's words for a volume → the <c>MeasurementUnit.Code</c> it is. US customary throughout, because FDC's
    /// household measures are. A word not here leaves the portion with no volume, never a guessed one.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> VolumeUnits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["cup"] = "cup-us",
        ["cups"] = "cup-us",
        ["tbsp"] = "tbsp",
        ["tablespoon"] = "tbsp",
        ["tablespoons"] = "tbsp",
        ["tsp"] = "tsp",
        ["teaspoon"] = "tsp",
        ["teaspoons"] = "tsp",
        ["fl oz"] = "floz-us",
        ["fluid ounce"] = "floz-us",
        ["pint"] = "pint-us",
        ["quart"] = "quart-us",
        ["gallon"] = "gallon-us",
        ["ml"] = "ml",
        ["milliliter"] = "ml",
        ["millilitre"] = "ml",
        ["liter"] = "l",
        ["litre"] = "l",
    };

    /// <summary>FDC's <c>dataType</c> → the catalogue's key for that data set, or null for one that is not approved.</summary>
    internal static string? DataSetOf(string? dataType) => dataType switch
    {
        "Foundation" => EvidenceSourceCatalog.FdcFoundation,
        "SR Legacy" => EvidenceSourceCatalog.FdcSrLegacy,
        _ => null,
    };

    /// <summary>The <c>dataType</c> values a search asks for: the approved data sets, in FDC's words.</summary>
    internal static readonly string[] ApprovedDataTypes = ["Foundation", "SR Legacy"];

    public static UsdaFdcReadResult ReadFood(
        JsonElement root, ApprovedEvidenceSource source, DateTimeOffset retrievedAt, out ReferenceFoodEvidence? food)
    {
        food = null;

        if (root.ValueKind is not JsonValueKind.Object
            || RecordId(root) is not { } id
            || Text(root, "description") is not { Length: <= EvidencePolicy.DescriptionMaxLength } description)
        {
            return UsdaFdcReadResult.Unreadable;
        }

        if (DataSetOf(Text(root, "dataType")) is not { } dataSet || !source.DataSets.Contains(dataSet))
        {
            return UsdaFdcReadResult.NotApproved;
        }

        // A food with no nutrient table at all is a record this reader does not understand, not a food with no
        // nutrients: answering Found with an empty list would invite reading the gap as zero.
        var coversNutrition = source.Covers.Contains(ReferenceEvidenceKind.Nutrition);
        if (coversNutrition && !(root.TryGetProperty("foodNutrients", out var table) && table.ValueKind is JsonValueKind.Array))
        {
            return UsdaFdcReadResult.Unreadable;
        }

        var publishedOn = Date(root, "publicationDate");

        food = new ReferenceFoodEvidence(
            new EvidenceProvenance(
                source.Code,
                source.Name,
                source.Kind,
                SourceVersion(dataSet, publishedOn),
                source.LicenseCode,
                source.LicenseUrl,
                source.Citation,
                id,
                publishedOn,
                retrievedAt),
            description,
            coversNutrition ? ReadNutrients(root) : [],
            source.Covers.Contains(ReferenceEvidenceKind.Density) ? ReadPortions(root) : []);

        return UsdaFdcReadResult.Read;
    }

    /// <summary>A search's hits, in approved data sets only. False when the answer is not a search result at all.</summary>
    public static bool TryReadSearch(JsonElement root, ApprovedEvidenceSource source, List<ReferenceFoodCandidate> into)
    {
        if (root.ValueKind is not JsonValueKind.Object
            || !root.TryGetProperty("foods", out var foods)
            || foods.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var hit in foods.EnumerateArray())
        {
            // The request already asked for approved data sets only; this is the check that does not trust it.
            if (DataSetOf(Text(hit, "dataType")) is not { } dataSet
                || !source.DataSets.Contains(dataSet)
                || RecordId(hit) is not { } id
                || Text(hit, "description") is not { Length: <= EvidencePolicy.DescriptionMaxLength } description)
            {
                continue;
            }

            var publishedOn = Date(hit, "publishedDate");
            into.Add(new ReferenceFoodCandidate(source.Code, id, description, SourceVersion(dataSet, publishedOn), publishedOn));

            if (into.Count == EvidencePolicy.MaxSearchResults)
            {
                break;
            }
        }

        return true;
    }

    private static string SourceVersion(string dataSet, DateOnly? publishedOn) =>
        $"{dataSet}/{(publishedOn is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "undated")}";

    private static List<NutrientEvidence> ReadNutrients(JsonElement root)
    {
        var best = new Dictionary<NutrientCode, (int Rank, NutrientEvidence Evidence)>();

        if (!root.TryGetProperty("foodNutrients", out var rows) || rows.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind is not JsonValueKind.Object
                || !row.TryGetProperty("nutrient", out var nutrient)
                || Text(nutrient, "number") is not { } number
                || !Nutrients.TryGetValue(number, out var known)
                || UnitOf(Text(nutrient, "unitName")) is not { } unit
                || unit != EvidencePolicy.UnitOf(known.Code)
                || !row.TryGetProperty("amount", out var amountElement)
                || amountElement.ValueKind is not JsonValueKind.Number
                || !amountElement.TryGetDecimal(out var amount)
                || amount < 0)
            {
                continue;
            }

            if (best.TryGetValue(known.Code, out var held) && held.Rank <= known.Rank)
            {
                continue;
            }

            var derivation = row.TryGetProperty("foodNutrientDerivation", out var derived)
                ? Bounded(Text(derived, "description"))
                : null;

            best[known.Code] = (known.Rank, new NutrientEvidence(
                known.Code,
                amount,
                unit,
                Bounded(Text(nutrient, "name")) ?? number,
                derivation));
        }

        return [.. best.Values.Select(entry => entry.Evidence).OrderBy(evidence => evidence.Code)];
    }

    private static List<PortionEvidence> ReadPortions(JsonElement root)
    {
        var portions = new List<PortionEvidence>();

        if (!root.TryGetProperty("foodPortions", out var rows) || rows.ValueKind is not JsonValueKind.Array)
        {
            return portions;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind is not JsonValueKind.Object
                || Number(row, "gramWeight") is not { } grams
                || grams <= 0
                || Number(row, "amount") is not { } amount
                || amount <= 0)
            {
                continue;
            }

            var modifier = Text(row, "modifier");
            var description = Text(row, "portionDescription");
            var measureUnit = row.TryGetProperty("measureUnit", out var unitElement) && unitElement.ValueKind is JsonValueKind.Object
                ? unitElement
                : default;
            var unitName = measureUnit.ValueKind is JsonValueKind.Object ? Text(measureUnit, "name") : null;
            var undetermined = measureUnit.ValueKind is JsonValueKind.Object
                && measureUnit.TryGetProperty("id", out var unitId)
                && unitId.TryGetInt64(out var unitIdValue)
                && unitIdValue == UndeterminedMeasureUnit;

            string? volume;
            string? condition;

            if (undetermined)
            {
                // SR Legacy: the unit is the modifier's first words, and what follows a comma is how it was measured.
                (volume, condition) = SplitModifier(modifier);
            }
            else
            {
                volume = unitName is not null && VolumeUnits.TryGetValue(unitName, out var code) ? code : null;
                condition = Bounded(modifier);
            }

            var label = Bounded(description ?? modifier ?? unitName);
            if (label is null)
            {
                continue;
            }

            portions.Add(new PortionEvidence(amount, grams, volume, volume is null ? null : condition, label));
        }

        return portions;
    }

    /// <summary>"cup, sifted" → (cup-us, "sifted"); "cup (8 fl oz)" → (cup-us, null); "large" → (null, null).</summary>
    internal static (string? VolumeUnitCode, string? Condition) SplitModifier(string? modifier)
    {
        if (string.IsNullOrWhiteSpace(modifier))
        {
            return (null, null);
        }

        var comma = modifier.IndexOf(',');
        var head = comma < 0 ? modifier : modifier[..comma];
        var paren = head.IndexOf('(');
        head = (paren < 0 ? head : head[..paren]).Trim();

        if (!VolumeUnits.TryGetValue(head, out var code))
        {
            return (null, null);
        }

        var condition = comma < 0 ? null : Bounded(modifier[(comma + 1)..].Trim());
        return (code, string.IsNullOrEmpty(condition) ? null : condition);
    }

    private static NutrientUnit? UnitOf(string? unitName) => unitName?.ToLowerInvariant() switch
    {
        "kcal" => NutrientUnit.Kilocalorie,
        "g" => NutrientUnit.Gram,
        "mg" => NutrientUnit.Milligram,
        "µg" or "μg" or "ug" or "mcg" => NutrientUnit.Microgram,
        _ => null,
    };

    /// <summary>FDC's id is a number; it travels as its digits, and nothing else is accepted as one.</summary>
    private static string? RecordId(JsonElement element) =>
        element.TryGetProperty("fdcId", out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt64(out var id)
        && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;

    private static DateOnly? Date(JsonElement element, string name) =>
        Text(element, name) is { } text
        && DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetDecimal(out var number)
            ? number
            : null;

    private static string? Bounded(string? text) =>
        text is { Length: > 0 and <= EvidencePolicy.SourceLabelMaxLength } ? text : null;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
