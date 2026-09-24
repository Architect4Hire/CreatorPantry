namespace CreatorPantry.Domain.Modules.Measurement.Managers;

/// <summary>
/// How a candidate's normalized text reached a <see cref="CreatorPantry.Domain.Modules.Measurement.Data.Entities.MeasurementUnit"/> —
/// its provenance, not a fuzzy score. Declaration order is the precedence <see cref="UnitMatcher"/> uses to
/// break a tie between two different units — do not reorder without updating it.
/// </summary>
/// <remarks>
/// Unlike <c>Ingredient</c>, a unit's alternate forms are scattered across several columns
/// (<c>Code</c>, <c>DisplayName</c>, <c>PluralName</c>, <c>Abbreviation</c>) plus the <c>UnitAlias</c> table,
/// and only <c>Code</c> is declared unique. A real cross-unit tie is structurally possible here in a way it
/// is not for ingredients — <see cref="UnitMatcher"/>'s ambiguity path exists for exactly this catalogue.
/// </remarks>
public enum UnitMatchKind
{
    Code = 0,
    DisplayName = 1,
    Abbreviation = 2,
    PluralName = 3,
    Alias = 4,
}

/// <summary>One row of the flattened lookup <see cref="UnitMatcher"/> resolves candidates against.</summary>
public sealed record UnitMatchIndexEntry(
    Guid MeasurementUnitId, string DisplayName, string NormalizedText, UnitMatchKind Kind);

/// <summary>One unit a candidate could resolve to.</summary>
public sealed record UnitMatchCandidate(Guid MeasurementUnitId, string DisplayName, UnitMatchKind Kind);

/// <summary>The outcome of matching one free-form candidate string against the unit catalogue.</summary>
public sealed record UnitMatchResult
{
    public required string InputText { get; init; }

    /// <summary>The confident match, or <see langword="null"/> when nothing resolved — including when it was ambiguous.</summary>
    public UnitMatchCandidate? Resolved { get; init; }

    /// <summary>
    /// Other units the same normalized text also touched, ranked below <see cref="Resolved"/> — or, when
    /// <see cref="IsAmbiguous"/> is true, every tied candidate with nothing chosen among them.
    /// </summary>
    public IReadOnlyList<UnitMatchCandidate> Alternates { get; init; } = [];

    /// <summary>
    /// True when the candidate's normalized text matched more than one <em>different</em> unit at the same
    /// precedence tier, so nothing broke the tie. <see cref="Resolved"/> is <see langword="null"/> in this
    /// case — low confidence remains unresolved rather than guessed.
    /// </summary>
    public bool IsAmbiguous { get; init; }
}
