using CreatorPantry.Domain.Managers.Quantities;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Ingredients.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

/// <summary>The rules an estimate is computed and rounded by (14.3, B-35). Change one, change the method version.</summary>
public static class NutritionEstimatePolicy
{
    /// <summary>Recorded on every estimate, so one computed under different rules can be told apart.</summary>
    public const string MethodVersion = "nutrition-estimate/1";

    public const string Label = "Estimate";

    public const string Method =
        "Each counted ingredient's nutrients per 100 g, from its confirmed reference, multiplied by its weight in "
        + "grams and summed. Quantities are taken as the edible portion as written: no trim, refuse or cooking-loss "
        + "adjustment is applied. Volumes and counts become grams only through the portion confirmed for that "
        + "ingredient. Optional and unquantified ingredients are not counted. Values are exact until shown, then "
        + "rounded once, with ties to even.";

    /// <summary>The places an exact value is reported to. Every display value is rounded from the exact one.</summary>
    public const int ExactScale = 6;

    public const MidpointRounding Rounding = MidpointRounding.ToEven;

    /// <summary>The most servings a caller may ask for.</summary>
    public const decimal MaxRequestedServings = 10_000m;

    /// <summary>How many places a nutrient is shown to, by its unit. Display only; never a statement of accuracy.</summary>
    public static int DisplayPrecisionOf(NutrientUnit unit) => unit switch
    {
        NutrientUnit.Gram or NutrientUnit.Microgram => 1,
        _ => 0,
    };
}

/// <summary>How one calculation ended.</summary>
/// <param name="Estimate">Present unless <paramref name="OutOfRange"/>.</param>
/// <param name="OutOfRange">A total too large to state as a decimal. Refused rather than thrown.</param>
public sealed record NutritionEstimateOutcome(NutritionEstimateServiceModel? Estimate, bool OutOfRange = false);

/// <summary>
/// The nutrition arithmetic (14.3, ING-008, CALC-001/006). Pure: no I/O, no clock, no model — the same input is
/// the same estimate.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Exact until shown.</strong> Every step is an exact fraction (<see cref="Quantity"/>) computed from the
/// stored values, never from a rounded one; each figure is rounded once, at the end, and reported beside its
/// exact value (CALC-002, CALC-006).
/// </para>
/// <para>
/// <strong>Nothing is guessed.</strong> A line that cannot be weighed or has no confirmed reference is listed with
/// the reason and not counted; a nutrient a reference does not report is unknown for that line and the total says
/// so; no trim, refuse or cooking loss is assumed (CALC-005).
/// </para>
/// </remarks>
public static class NutritionEstimateCalculator
{
    private static readonly Quantity OneHundred = Quantity.FromInt(100);

    public static NutritionEstimateOutcome Calculate(NutritionEstimateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = input.Lines.Select(line => Weigh(line, input)).ToList();
        var counted = lines.Where(line => line.Outcome is NutritionLineOutcome.Included).ToList();
        var complete = lines.All(line => !line.Required || line.Outcome is NutritionLineOutcome.Included);
        var (servings, servingsSource) = ServingsOf(input.Servings);

        try
        {
            var nutrients = Totals(counted)
                .Select(total => new NutritionNutrientEstimateServiceModel(
                    total.Code,
                    total.Unit,
                    Amount(total.Low, total.High, total.Unit),
                    servings is { } count
                        ? Amount(Divide(total.Low, count), Divide(total.High, count), total.Unit)
                        : null,
                    total.MissingFrom.Count == 0,
                    total.MissingFrom))
                .ToList();

            var estimate = new NutritionEstimateServiceModel(
                input.RecipeId,
                input.RecipeVersionId,
                input.VersionNumber,
                NutritionEstimatePolicy.Label,
                NutritionEstimatePolicy.MethodVersion,
                NutritionEstimatePolicy.Method,
                complete ? NutritionEstimateStatus.Complete : NutritionEstimateStatus.Incomplete,
                Publishable: complete,
                Partial: !complete,
                counted.Count == 0 ? null : counted.Max(line => line.Basis!.Value),
                servings?.ToDecimal(NutritionEstimatePolicy.ExactScale, NutritionEstimatePolicy.Rounding),
                servingsSource,
                nutrients,
                [.. lines.Select(line => line.ToServiceModel())],
                [.. counted
                    .Select(line => line.Reference!)
                    .DistinctBy(reference => reference.SourceCode)
                    .OrderBy(reference => reference.SourceCode, StringComparer.Ordinal)
                    .Select(reference => new NutritionCitationServiceModel(
                        reference.SourceCode, reference.Citation, reference.LicenseCode, reference.LicenseUrl))]);

            return new NutritionEstimateOutcome(estimate);
        }
        catch (OverflowException)
        {
            return new NutritionEstimateOutcome(null, OutOfRange: true);
        }
    }

    // ---- One line ----

    private sealed record WeighedLine(
        NutritionEstimateLineInput Line,
        NutritionLineOutcome Outcome,
        NutritionLineReason Reason,
        bool Required,
        Quantity? GramsLow = null,
        Quantity? GramsHigh = null,
        NutritionQuantityBasis? Basis = null,
        NutritionEstimateMappingInput? Mapping = null,
        NutritionEstimateReferenceInput? Reference = null)
    {
        public NutritionLineEstimateServiceModel ToServiceModel() => new(
            Line.LineId,
            Line.DisplayText,
            Outcome,
            Reason,
            Required,
            GramsLow?.ToDecimal(NutritionEstimatePolicy.ExactScale, NutritionEstimatePolicy.Rounding),
            GramsHigh?.ToDecimal(NutritionEstimatePolicy.ExactScale, NutritionEstimatePolicy.Rounding),
            Basis,
            Mapping?.MappingId,
            Reference is null ? null : new NutritionEstimateSourceServiceModel(Reference.SourceCode, Reference.ExternalRecordId, Reference.SourceVersion));
    }

    /// <summary>
    /// What one line comes to, in the order the rules are written: optional, then unquantified, then unmapped,
    /// then under review, then unweighable — and only then counted.
    /// </summary>
    private static WeighedLine Weigh(NutritionEstimateLineInput line, NutritionEstimateInput input)
    {
        if (line.IsOptional)
        {
            return new(line, NutritionLineOutcome.Optional, NutritionLineReason.None, Required: false);
        }

        if (line.Quantity is not { } quantity)
        {
            return new(line, NutritionLineOutcome.NotQuantified, NutritionLineReason.None, Required: false);
        }

        if (line.IngredientId is not { } ingredientId)
        {
            return new(line, NutritionLineOutcome.Unmapped, NutritionLineReason.NoIngredient, Required: true);
        }

        if (!input.MappingsByIngredient.TryGetValue(ingredientId, out var mapping))
        {
            return new(line, NutritionLineOutcome.Unmapped, NutritionLineReason.NoMapping, Required: true);
        }

        switch (mapping.Status)
        {
            case NutritionMappingStatus.NeedsReview:
                return new(line, NutritionLineOutcome.NeedsReview, NutritionLineReason.MappingUnderReview, Required: true, Mapping: mapping);
            case not NutritionMappingStatus.Confirmed:
                return new(line, NutritionLineOutcome.Unmapped, NutritionLineReason.MappingNotConfirmed, Required: true, Mapping: mapping);
        }

        if (!input.References.TryGetValue((mapping.SourceCode!, mapping.ExternalRecordId!), out var reference))
        {
            return new(line, NutritionLineOutcome.NeedsReview, NutritionLineReason.ReferenceWithdrawn, Required: true, Mapping: mapping);
        }

        // What was confirmed is one version of the record; a different one now is not what the creator said yes to.
        if (!string.Equals(reference.SourceVersion, mapping.SourceVersion, StringComparison.Ordinal))
        {
            return new(line, NutritionLineOutcome.NeedsReview, NutritionLineReason.SourceVersionChanged, Required: true, Mapping: mapping);
        }

        var (reason, basis, gramsPerUnit) = GramsPerLineUnit(line, mapping, input.Units);
        if (gramsPerUnit is not { } factor)
        {
            return new(line, NutritionLineOutcome.Unconvertible, reason, Required: true, Mapping: mapping);
        }

        var low = Quantity.FromDecimal(quantity).Multiply(factor);
        var high = line.QuantityUpper is { } upper ? Quantity.FromDecimal(upper).Multiply(factor) : low;

        return new(line, NutritionLineOutcome.Included, NutritionLineReason.None, Required: true, low, high, basis, mapping, reference);
    }

    /// <summary>How many grams one of the line's unit weighs for this ingredient, or why that cannot be known.</summary>
    private static (NutritionLineReason Reason, NutritionQuantityBasis? Basis, Quantity? GramsPerUnit) GramsPerLineUnit(
        NutritionEstimateLineInput line, NutritionEstimateMappingInput mapping, IReadOnlyDictionary<Guid, NutritionEstimateUnitInput> units)
    {
        if (line.MeasurementUnitId is not { } unitId)
        {
            // "2 eggs" with no unit is not taken to mean "2 each": that would be a guess (B-35).
            return (NutritionLineReason.NoUnit, null, null);
        }

        if (!units.TryGetValue(unitId, out var unit))
        {
            return (NutritionLineReason.UnitUnknown, null, null);
        }

        if (unit.Dimension is MeasurementDimension.Mass)
        {
            // Mass's base unit is the gram.
            return unit.BaseUnitFactor is { } grams
                ? (NutritionLineReason.None, NutritionQuantityBasis.Mass, Quantity.FromDecimal(grams))
                : (NutritionLineReason.UnitNotMeasurable, null, null);
        }

        if (unit.Dimension is not (MeasurementDimension.Volume or MeasurementDimension.Count))
        {
            return (NutritionLineReason.UnitNotMeasurable, null, null);
        }

        if (mapping is not { PortionMeasurementUnitId: { } portionUnitId, PortionAmount: { } portionAmount, PortionGrams: { } portionGrams, PortionOrigin: { } origin })
        {
            return (NutritionLineReason.NoPortion, null, null);
        }

        var basis = origin is NutritionPortionOrigin.Reference ? NutritionQuantityBasis.ReferencePortion : NutritionQuantityBasis.CreatorPortion;
        var gramsPerPortionUnit = Divide(Quantity.FromDecimal(portionGrams), Quantity.FromDecimal(portionAmount));

        if (unit.Dimension is MeasurementDimension.Count)
        {
            // Counts of different things never convert: a clove is not a slice.
            return unitId == portionUnitId
                ? (NutritionLineReason.None, basis, gramsPerPortionUnit)
                : (NutritionLineReason.PortionDoesNotMatch, null, null);
        }

        // Volumes convert through millilitres: grams per line unit = (line unit's ml ÷ portion unit's ml) × grams per portion unit.
        if (!units.TryGetValue(portionUnitId, out var portionUnit)
            || portionUnit.Dimension is not MeasurementDimension.Volume
            || unit.BaseUnitFactor is not { } lineMl
            || portionUnit.BaseUnitFactor is not { } portionMl)
        {
            return (NutritionLineReason.PortionDoesNotMatch, null, null);
        }

        return (NutritionLineReason.None, basis,
            Divide(Quantity.FromDecimal(lineMl), Quantity.FromDecimal(portionMl)).Multiply(gramsPerPortionUnit));
    }

    // ---- Totals ----

    private sealed record NutrientTotal(NutrientCode Code, NutrientUnit Unit, Quantity Low, Quantity High, IReadOnlyList<Guid> MissingFrom);

    /// <summary>
    /// One total per nutrient any counted reference reports, in <see cref="NutrientCode"/> order. A counted line whose
    /// reference does not report a nutrient is named against it rather than counted as zero.
    /// </summary>
    private static List<NutrientTotal> Totals(IReadOnlyList<WeighedLine> counted)
    {
        var codes = counted
            .SelectMany(line => line.Reference!.Nutrients)
            .Select(nutrient => nutrient.Code)
            .Distinct()
            .Order();

        var totals = new List<NutrientTotal>();
        foreach (var code in codes)
        {
            var low = Quantity.Zero;
            var high = Quantity.Zero;
            var missing = new List<Guid>();
            var unit = EvidenceUnitOf(code);

            foreach (var line in counted)
            {
                // A unit other than the nutrient's own is the wrong row, not a value to convert (14.1).
                if (line.Reference!.Nutrients.FirstOrDefault(nutrient => nutrient.Code == code && nutrient.Unit == unit) is not { } nutrient)
                {
                    missing.Add(line.Line.LineId);
                    continue;
                }

                var per100 = Quantity.FromDecimal(nutrient.AmountPer100Grams);
                low = low.Add(Divide(line.GramsLow!.Value.Multiply(per100), OneHundred));
                high = high.Add(Divide(line.GramsHigh!.Value.Multiply(per100), OneHundred));
            }

            totals.Add(new NutrientTotal(code, unit, low, high, missing));
        }

        return totals;
    }

    /// <summary>The one unit 14.1 records each nutrient in. Restated here because a policy type does not cross modules.</summary>
    internal static NutrientUnit EvidenceUnitOf(NutrientCode code) => code switch
    {
        NutrientCode.Energy => NutrientUnit.Kilocalorie,
        NutrientCode.Cholesterol or NutrientCode.Sodium or NutrientCode.Potassium
            or NutrientCode.Calcium or NutrientCode.Iron => NutrientUnit.Milligram,
        NutrientCode.VitaminD => NutrientUnit.Microgram,
        _ => NutrientUnit.Gram,
    };

    // ---- Servings ----

    private static (Quantity? Servings, NutritionServingsSource Source) ServingsOf(NutritionEstimateServingsInput servings) =>
        servings switch
        {
            { Requested: > 0 and var requested } => (Quantity.FromDecimal(requested), NutritionServingsSource.Requested),
            { ServingCount: > 0 and var count } => (Quantity.FromDecimal(count), NutritionServingsSource.ServingCount),
            { YieldQuantity: > 0 and var batch, ServingSize: > 0 and var size } =>
                (Divide(Quantity.FromDecimal(batch), Quantity.FromDecimal(size)), NutritionServingsSource.YieldOverServingSize),
            _ => (null, NutritionServingsSource.None),
        };

    // ---- Rounding, once ----

    private static NutritionAmountServiceModel Amount(Quantity low, Quantity high, NutrientUnit unit)
    {
        var precision = NutritionEstimatePolicy.DisplayPrecisionOf(unit);
        var displayHigh = high.ToDecimal(precision, NutritionEstimatePolicy.Rounding);

        return new NutritionAmountServiceModel(
            low.ToDecimal(NutritionEstimatePolicy.ExactScale, NutritionEstimatePolicy.Rounding),
            high.ToDecimal(NutritionEstimatePolicy.ExactScale, NutritionEstimatePolicy.Rounding),
            low.ToDecimal(precision, NutritionEstimatePolicy.Rounding),
            displayHigh,
            precision,
            NutritionEstimatePolicy.Rounding,
            IsRange: low != high,
            Trace: !high.IsZero && displayHigh == 0m);
    }

    /// <summary>Exact division. <see cref="Quantity"/> has no divide of its own; every calculator states its own.</summary>
    private static Quantity Divide(Quantity dividend, Quantity divisor) =>
        Quantity.FromFraction(dividend.Numerator * divisor.Denominator, dividend.Denominator * divisor.Numerator);
}
