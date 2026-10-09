namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which of the three readings of a dish name a suggestion is about.
/// </summary>
/// <remarks>
/// <para>
/// Three, and the three that say what the food <em>is</em>. A dish name carries real evidence about its
/// cuisine, the role it plays in a meal, and how it is cooked — "Fattoush" names a cuisine, "salad" a course,
/// "grilled" a method. It carries almost none about the facets the Content Pipeline's idea generator also
/// draws: an occasion is a decision about the post rather than a fact about the food, and a photography style
/// is a decision about the photograph. Asking a model for either would be asking it to invent a preference
/// and then showing the answer as though the name had implied it.
/// </para>
/// <para>
/// The names mirror the pipeline's own vocabulary (<c>ContentSeedKeepName</c>) rather than the database's, so
/// that a suggestion and the pin it becomes are called the same thing from the model's answer to the select
/// it lands in. <see cref="DishType"/> is the platform's <c>Course</c> and <see cref="Method"/> its
/// <c>CookingTechnique</c>; the handler does that translation once.
/// </para>
/// </remarks>
public enum AiDishFacet
{
    /// <summary>Not declared. Never valid in an answer — the validator refuses it.</summary>
    /// <remarks>
    /// A suggestion that did not say which facet it was about would be unreadable: the handler stores rows
    /// keyed by this, and three answers that all claimed to be the first facet would collide.
    /// </remarks>
    Unspecified = 0,

    /// <summary>The regional or cultural cooking tradition. A platform <c>Cuisine</c> code.</summary>
    Cuisine = 1,

    /// <summary>The role the dish plays in a meal. A platform <c>Course</c> code.</summary>
    DishType = 2,

    /// <summary>How the dish is chiefly cooked. A platform <c>CookingTechnique</c> code.</summary>
    Method = 3,
}

/// <summary>
/// How sure one reading of a dish name is.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Required on every suggestion, and the surface acts on it.</strong> "Fattoush" says Levantine
/// outright; "Summer bowl" might mean a salad and might not. A creator shown both as plain selections cannot
/// tell which to check, so the band is a field rather than a sentence, and the idea step uses it: only a
/// <see cref="Likely"/> reading pre-fills a control, while a <see cref="Possible"/> one is offered and left
/// unset.
/// </para>
/// <para>
/// Two bands rather than three, where <see cref="AiReferenceImageConfidence"/> has three. That enum reads a
/// photograph, where "cannot be made out" is a useful third answer about the image itself. Here the third
/// answer is not a confidence at all — it is declining, which this capability expresses by returning no code
/// for that facet and saying why. A band meaning "I have no reading" alongside a code would let an answer be
/// both.
/// </para>
/// </remarks>
public enum AiDishFacetConfidence
{
    /// <summary>Not declared. Never valid on a suggestion that names a code — the validator refuses it.</summary>
    /// <remarks>
    /// Zero is the value a model gets by omitting the property, so without refusing it an answer that said
    /// nothing about how sure it was would read as the first real band in the list.
    /// </remarks>
    Unspecified = 0,

    /// <summary>The name says this, on its own terms. Pre-fills the creator's control.</summary>
    Likely = 1,

    /// <summary>
    /// Consistent with the name, but a reading of it rather than something it states. Offered, never
    /// pre-filled.
    /// </summary>
    Possible = 2,
}
