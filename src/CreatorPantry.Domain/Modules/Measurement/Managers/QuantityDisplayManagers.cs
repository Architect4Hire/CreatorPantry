namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>How <see cref="QuantityDisplayCalculator"/> rendered one number.</summary>
public enum FractionPresentation
{
    /// <summary>A bare integer — the exact value has no fractional remainder at all.</summary>
    Whole = 0,

    /// <summary>A whole part (when non-zero) plus a Unicode kitchen-fraction glyph — the remainder matched a common kitchen fraction exactly.</summary>
    Fraction = 1,

    /// <summary>A rounded decimal — the remainder did not match a common kitchen fraction, so no glyph applies.</summary>
    Decimal = 2,
}

/// <summary>
/// A readable rendering of a quantity (and, for a range, its upper bound). Presentation only — the exact
/// <c>Quantity</c>/<c>QuantityRange</c> this was built from remains the canonical value; nothing here replaces
/// or feeds back into it (recipes.md, ING-006).
/// </summary>
public sealed record QuantityDisplayResult
{
    /// <summary>The complete rendered text, including the unit — e.g. <c>"1½ cups"</c>, <c>"2–3 tbsp"</c>, <c>"0.02 g"</c>.</summary>
    public required string Text { get; init; }

    /// <summary>How the single value, or a range's lower bound, was rendered.</summary>
    public required FractionPresentation Presentation { get; init; }

    /// <summary>How a range's upper bound was rendered; <see langword="null"/> when this was a single value, not a range.</summary>
    public required FractionPresentation? UpperPresentation { get; init; }

    /// <summary>The precision used for the <see cref="FractionPresentation.Decimal"/> fallback, echoed rather than left implicit.</summary>
    public required int Precision { get; init; }

    /// <summary>The rounding mode used for that fallback, returned explicitly (CALC-002 — documented rounding).</summary>
    public required MidpointRounding Rounding { get; init; }
}
