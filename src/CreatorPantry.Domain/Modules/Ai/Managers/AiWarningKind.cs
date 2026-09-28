namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What a warning attached to a proposal is telling the creator.
/// </summary>
/// <remarks>
/// <para>
/// Assumptions live here rather than in a text field on the proposal, so the review panel renders one uniform
/// list and an assumption cannot be presented with less weight than a caution.
/// </para>
/// <para>
/// The food-domain members exist because ai.md requires uncertainty to be surfaced rather than smoothed over.
/// None of them is a safety verdict: a proposal with no <see cref="SafetyCaution"/> has not been found safe,
/// it has simply not been flagged.
/// </para>
/// </remarks>
public enum AiWarningKind
{
    /// <summary>Not declared. Never valid on a stored row; the check constraint refuses it.</summary>
    Unspecified = 0,

    /// <summary>Something the model took as given because the source did not say.</summary>
    Assumption = 1,

    /// <summary>A culinary caution: plausible, but worth the creator's judgement.</summary>
    CulinaryCaution = 2,

    /// <summary>
    /// Wording that does not scale or convert — "to taste", a package size, a pan constraint. Named by
    /// recipes.md as a review flag rather than something to rewrite silently.
    /// </summary>
    NonScalableLanguage = 3,

    /// <summary>A claim with no vetted source behind it, and which must not be presented as established.</summary>
    UnverifiedClaim = 4,

    /// <summary>
    /// Touches preservation, temperature, allergens, or dietary suitability. Requires a vetted reference or an
    /// explicit caution, and is never a guarantee in either direction.
    /// </summary>
    SafetyCaution = 5,

    /// <summary>
    /// A question the model has for the creator, distinct from an <see cref="Assumption"/>: an assumption is
    /// what the model took as given; this is what it declined to guess at all. AIREC-002 is the first
    /// capability to emit one.
    /// </summary>
    UnresolvedQuestion = 6,

    /// <summary>
    /// Why one change was proposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not a warning, and it lives here anyway.</strong> A rationale belongs to a single change and is
    /// shown beside it, which is exactly what this table already does — <c>AiWarning</c> carries an
    /// <c>AiStructuredChangeId</c> and the review panel already renders a labelled note against a row. A
    /// <c>Rationale</c> column on <c>AiStructuredChange</c> would be a second mechanism for the same job, and
    /// a migration for it.
    /// </para>
    /// <para>
    /// A revision states why it changed something (AIREC-003), which is how a creator judges whether the goal
    /// they asked for is what they got. It carries no caution: a rationale says what the model was doing, not
    /// that anything about the result needs checking.
    /// </para>
    /// </remarks>
    Rationale = 7,
}
