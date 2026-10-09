namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The contract a dish-name reading must satisfy: at most one suggestion per facet, each naming a catalogue
/// code or declining with a reason, and each saying how sure it is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A code, never a name.</strong> The only thing a suggestion may carry is a <c>code</c> from the
/// candidate list the prompt supplied — not "Levantine", not "Middle Eastern", not a code the model remembers
/// from elsewhere. That is what keeps this a constrained command rather than free text about food:
/// <see cref="DishFacetSuggestionAiTaskHandler"/> checks every code against the catalogue it just read and
/// discards anything else, so the worst a confused answer can do is suggest nothing.
/// </para>
/// <para>
/// <strong>There is no field for what the dish contains, tastes like, or is suitable for.</strong> Not an
/// ingredient list, not a dietary claim, not an allergen, not a cooking time. Strict shape validation refuses
/// an invented field, which matters because this capability reads a name — the thinnest evidence in the
/// product — and anything beyond "this is probably a Levantine salad" would be invention dressed as a
/// reading.
/// </para>
/// <para>
/// <strong>The absent field is not the whole guarantee, though, and it would be comfortable to pretend
/// otherwise.</strong> <see cref="AiDishFacetSuggestion.Rationale"/> and
/// <see cref="AiDishFacetsOutputWarning.Message"/> are free text that reaches the creator, so a claim can be
/// written into a sentence where it has no field. That is <see cref="AiDishFacetsOutputValidator"/>'s
/// <c>Claims</c> stage, against the same term list the image capabilities use — a denylist, with a
/// denylist's limits, which is why the template instructs as well.
/// </para>
/// <para>
/// <strong>Declining is expressed by a null code, and must be explained.</strong> A facet the name does not
/// carry is a real answer, and the more honest one for "Weeknight dinner" — see
/// <see cref="AiDishFacetSuggestion.Code"/>. An omitted facet means the same thing and needs no row at all;
/// what the validator refuses is the half-answer that names no code and gives no reason.
/// </para>
/// </remarks>
public sealed record AiDishFacetsOutputDocument
{
    /// <summary>The schema this answer claims to follow, checked before anything else about it.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// What the name was read as, at most one suggestion per facet.
    /// </summary>
    /// <remarks>
    /// One per facet because the stored rows are keyed by it (<c>facet.{Facet}</c>), so two readings of the
    /// cuisine would collide. The validator refuses a duplicate rather than letting the second silently
    /// replace the first. An empty list is valid and means the name carried nothing — a name may simply not
    /// be a dish.
    /// </remarks>
    public IReadOnlyList<AiDishFacetSuggestion> Suggestions { get; init; } = [];

    /// <summary>What the creator should know about this reading.</summary>
    public IReadOnlyList<AiDishFacetsOutputWarning> Warnings { get; init; } = [];
}

/// <summary>One reading of one facet of the dish name.</summary>
public sealed record AiDishFacetSuggestion
{
    /// <summary>Which facet this is about. Must be declared, and at most once across the answer.</summary>
    public required AiDishFacet Facet { get; init; }

    /// <summary>
    /// The catalogue code this facet is read as, or null to decline.
    /// </summary>
    /// <remarks>
    /// Null is not a failure and not an omission — it is the answer "this name does not tell me", which the
    /// creator is better served by than a guess. A null code requires a <see cref="Rationale"/> saying why
    /// and forbids a <see cref="Confidence"/>, because there is no reading to be confident about.
    /// </remarks>
    public string? Code { get; init; }

    /// <summary>
    /// How sure the reading is. Required when <see cref="Code"/> names one; refused when it does not.
    /// </summary>
    public AiDishFacetConfidence Confidence { get; init; }

    /// <summary>
    /// Which words of the name led here, or why nothing did. One short sentence.
    /// </summary>
    /// <remarks>
    /// Required in both directions, and it is what makes a suggestion reviewable rather than oracular: "the
    /// name says grilled" is checkable by the person who typed the name, where a bare selection is not. It is
    /// shown beside the control, so a misreading is corrected in the place it appears.
    /// </remarks>
    public required string Rationale { get; init; }
}

/// <summary>One thing to tell the creator about the reading.</summary>
public sealed record AiDishFacetsOutputWarning
{
    public required AiWarningKind Kind { get; init; }

    public required string Message { get; init; }
}
