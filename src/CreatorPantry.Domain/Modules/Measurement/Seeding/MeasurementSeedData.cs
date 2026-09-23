using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;

namespace CreatorPantry.Domain.Modules.Measurement.Seeding;

/// <summary>
/// The platform unit catalogue: every <see cref="MeasurementUnit"/> the product offers, and the surface forms
/// that resolve to one. Tier A — seeded in every environment.
/// </summary>
/// <remarks>
/// <para>
/// Every factor here is <em>definitional rather than measured</em>. The international yard-and-pound agreement
/// of 1959 fixes the avoirdupois pound at exactly 453.59237 g and the US gallon at exactly 3785.411784 mL;
/// every other US customary figure below follows from those two by division, which is why they are written out
/// to their full exact length instead of rounded. Nothing in this file is a third-party dataset, so no
/// <see cref="ReferenceSource"/> is cited and none is needed — <see cref="MeasurementUnit"/> deliberately has
/// no provenance column.
/// </para>
/// <para>
/// Count units all carry a factor of 1, meaning "one of these is one item". That does <em>not</em> make two
/// different count nouns interconvertible: one head is not one clove, and conversion code must refuse a
/// cross-noun count conversion rather than trusting the arithmetic. The factor exists so a count quantity
/// <em>scales</em>, which is the only operation it supports.
/// </para>
/// </remarks>
internal static class MeasurementSeedData
{
    private const string UnitTable = "MeasurementUnits";
    private const string AliasTable = "UnitAliases";

    /// <summary>Resolves a seeded unit's id from its permanent code, for the rows that reference one.</summary>
    public static Guid UnitId(string code) => SeedId.For(UnitTable, code);

    public static IReadOnlyList<MeasurementUnit> Units() =>
    [
        // Mass, against the gram base.
        Unit("g", "gram", "grams", "g", MeasurementDimension.Mass, MeasurementSystem.Metric, 1m, 0),
        Unit("kg", "kilogram", "kilograms", "kg", MeasurementDimension.Mass, MeasurementSystem.Metric, 1000m, 3),
        Unit("mg", "milligram", "milligrams", "mg", MeasurementDimension.Mass, MeasurementSystem.Metric, 0.001m, 0),
        Unit("oz", "ounce", "ounces", "oz", MeasurementDimension.Mass, MeasurementSystem.UsCustomary, 28.349523125m, 2),
        Unit("lb", "pound", "pounds", "lb", MeasurementDimension.Mass, MeasurementSystem.UsCustomary, 453.59237m, 2),

        // Volume, against the millilitre base.
        Unit("ml", "milliliter", "milliliters", "ml", MeasurementDimension.Volume, MeasurementSystem.Metric, 1m, 0),
        Unit("dl", "deciliter", "deciliters", "dl", MeasurementDimension.Volume, MeasurementSystem.Metric, 100m, 2),
        Unit("l", "liter", "liters", "L", MeasurementDimension.Volume, MeasurementSystem.Metric, 1000m, 2),
        Unit("tsp", "teaspoon", "teaspoons", "tsp", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 4.92892159375m, 2),
        Unit("tbsp", "tablespoon", "tablespoons", "tbsp", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 14.78676478125m, 2),
        Unit("floz-us", "US fluid ounce", "US fluid ounces", "fl oz (US)", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 29.5735295625m, 2),
        Unit("cup-us", "US cup", "US cups", "cup", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 236.5882365m, 2),
        Unit("pint-us", "US pint", "US pints", "pt (US)", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 473.176473m, 2),
        Unit("quart-us", "US quart", "US quarts", "qt (US)", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 946.352946m, 2),
        Unit("gallon-us", "US gallon", "US gallons", "gal (US)", MeasurementDimension.Volume, MeasurementSystem.UsCustomary, 3785.411784m, 2),

        // Count, against the "each" base. See the class remarks on why these are not interconvertible.
        Unit("each", "each", "each", "ea", MeasurementDimension.Count, MeasurementSystem.Neutral, 1m, 0),
        Unit("clove", "clove", "cloves", "clove", MeasurementDimension.Count, MeasurementSystem.Neutral, 1m, 0),
        Unit("slice", "slice", "slices", "slice", MeasurementDimension.Count, MeasurementSystem.Neutral, 1m, 0),
        Unit("stick", "stick", "sticks", "stick", MeasurementDimension.Count, MeasurementSystem.Neutral, 1m, 0),
        Unit("sprig", "sprig", "sprigs", "sprig", MeasurementDimension.Count, MeasurementSystem.Neutral, 1m, 0),
        Unit("bunch", "bunch", "bunches", "bunch", MeasurementDimension.Count, MeasurementSystem.Neutral, 1m, 0),

        // Temperature: affine, so no factor. Conversion is a deterministic domain calculation, not a multiply.
        Unit("celsius", "degree Celsius", "degrees Celsius", "°C", MeasurementDimension.Temperature, MeasurementSystem.Metric, null, 0),
        Unit("fahrenheit", "degree Fahrenheit", "degrees Fahrenheit", "°F", MeasurementDimension.Temperature, MeasurementSystem.UsCustomary, null, 0),

        // Qualitative: no numeric relationship to anything. These are exactly the non-scalable quantities
        // recipes.md requires be preserved and flagged for review rather than multiplied through.
        Unit("pinch", "pinch", "pinches", "pinch", MeasurementDimension.Qualitative, MeasurementSystem.Neutral, null, 0),
        Unit("dash", "dash", "dashes", "dash", MeasurementDimension.Qualitative, MeasurementSystem.Neutral, null, 0),
        Unit("splash", "splash", "splashes", "splash", MeasurementDimension.Qualitative, MeasurementSystem.Neutral, null, 0),
        Unit("to-taste", "to taste", "to taste", "to taste", MeasurementDimension.Qualitative, MeasurementSystem.Neutral, null, 0),
    ];

    public static IReadOnlyList<UnitAlias> Aliases() =>
    [
        .. AliasDefinitions.SelectMany(definition => definition.Aliases.Select(alias => new UnitAlias
        {
            Id = SeedId.For(AliasTable, MeasurementPolicy.NormalizeAlias(alias)),
            MeasurementUnitId = UnitId(definition.Code),
            Alias = alias,
            NormalizedAlias = MeasurementPolicy.NormalizeAlias(alias),
        })),
    ];

    /// <summary>
    /// Surface forms, grouped by the unit they resolve to. Each must be unique across the whole catalogue once
    /// <see cref="MeasurementPolicy.NormalizeAlias"/> has stripped punctuation and folded case — an alias
    /// matching two units would make a recipe line ambiguous, and the unique index refuses to store it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberate omissions, all for the same reason <see cref="MeasurementPolicy.NormalizeAlias"/> already
    /// gives for <c>t</c> and <c>T</c>: once case is folded, the alias is ambiguous to the database in the
    /// same way it is ambiguous to a reader, and resolving it needs the surrounding recipe line.
    /// <list type="bullet">
    /// <item><c>c</c> — cup or Celsius. <c>°C</c> normalizes to <c>c</c> too, so the symbol form is left out of
    /// the Celsius aliases and the written forms carry it instead.</item>
    /// <item><c>t</c> and <c>T</c> — teaspoon or tablespoon.</item>
    /// <item><c>#</c> for pound — normalizes to the empty string, which is not a lookup key at all.</item>
    /// </list>
    /// <c>f</c> is kept: nothing else in the catalogue claims it, so <c>°F</c> resolves unambiguously.
    /// </para>
    /// <para>
    /// <strong><c>pint</c>, <c>quart</c>, <c>gallon</c>, and <c>fluid ounce</c> have no aliases at all</strong>,
    /// for the same reason at a larger scale. Each names a different quantity in US customary and in imperial
    /// — an imperial pint is 20 fl oz against the US 16, a 20% difference — and
    /// <see cref="MeasurementSystem"/> keeps the two traditions apart precisely so that difference cannot be
    /// silently averaged away. Claiming the bare words for the US units would resolve a British creator's
    /// "1 pint" to 473 ml instead of 568 ml, inside their own recipe, with nothing to show it happened.
    /// </para>
    /// <para>
    /// This is a one-way door, which is why it is decided here rather than later:
    /// <c>UX_UnitAliases_NormalizedAlias</c> is unique across the whole catalogue, so once <c>pint</c> belongs
    /// to <c>pint-us</c> an imperial pint can never take it back without a migration and a re-resolution of
    /// every recipe line already matched. Leaving these unresolved costs a creator a lookup; claiming them
    /// wrongly costs them a recipe. <c>cup</c> is kept despite a smaller version of the same ambiguity (US
    /// 236.6 ml against a metric 250 ml) because US customary is the overwhelming default for the bare word in
    /// English recipe writing — a deliberate line, not an oversight.
    /// </para>
    /// </remarks>
    private static readonly (string Code, string[] Aliases)[] AliasDefinitions =
    [
        ("g", ["g", "gram", "grams", "gramme", "grammes", "gm", "gms"]),
        ("kg", ["kg", "kgs", "kilogram", "kilograms", "kilogramme", "kilogrammes", "kilo", "kilos"]),
        ("mg", ["mg", "milligram", "milligrams"]),
        ("oz", ["oz", "ozs", "ounce", "ounces"]),
        ("lb", ["lb", "lbs", "pound", "pounds"]),

        ("ml", ["ml", "mls", "milliliter", "milliliters", "millilitre", "millilitres", "cc"]),
        ("dl", ["dl", "deciliter", "deciliters", "decilitre", "decilitres"]),
        ("l", ["l", "liter", "liters", "litre", "litres", "ltr"]),
        ("tsp", ["tsp", "tsps", "teaspoon", "teaspoons"]),
        ("tbsp", ["tbsp", "tbsps", "tbs", "tbl", "tblsp", "tablespoon", "tablespoons"]),
        ("cup-us", ["cup", "cups"]),

        ("each", ["each", "ea", "ct", "count", "piece", "pieces", "pc", "pcs", "whole"]),
        ("clove", ["clove", "cloves"]),
        ("slice", ["slice", "slices"]),
        ("stick", ["stick", "sticks"]),
        ("sprig", ["sprig", "sprigs"]),
        ("bunch", ["bunch", "bunches"]),

        ("celsius", ["celsius", "centigrade", "deg c", "degrees c", "degrees celsius"]),
        ("fahrenheit", ["°F", "fahrenheit", "deg f", "degrees f", "degrees fahrenheit"]),

        ("pinch", ["pinch", "pinches"]),
        ("dash", ["dash", "dashes"]),
        ("splash", ["splash", "splashes"]),
        ("to-taste", ["to taste", "as needed", "as desired"]),
    ];

    private static MeasurementUnit Unit(
        string code,
        string displayName,
        string pluralName,
        string abbreviation,
        MeasurementDimension dimension,
        MeasurementSystem system,
        decimal? baseUnitFactor,
        int displayPrecision) =>
        new()
        {
            Id = UnitId(code),
            Code = code,
            DisplayName = displayName,
            PluralName = pluralName,
            Abbreviation = abbreviation,
            Dimension = dimension,
            System = system,
            BaseUnitFactor = baseUnitFactor,
            DisplayPrecision = displayPrecision,
            IsActive = true,
        };
}
