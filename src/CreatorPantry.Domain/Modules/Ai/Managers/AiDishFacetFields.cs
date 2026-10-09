using System.Globalization;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// The field names a dish-name reading is stored under, as <c>AiStructuredChange.FieldName</c>, and the input
/// keys its request travels under.
/// </summary>
/// <remarks>
/// <para>
/// A reading is not stored as a reading. <see cref="DishFacetSuggestionAiTaskHandler"/> flattens it into one
/// <see cref="AiChangeKind.Add"/> row carrying the name that was read, plus three
/// <see cref="AiChangeKind.Set"/> rows per facet, so these strings are the only thing tying a written field
/// to a read one. Anything reading a suggestion back is doing that flattening in reverse and has to agree on
/// the names.
/// </para>
/// <para>
/// <strong>Today the only reader is the browser</strong>, over the generic <c>AiStructuredChange</c> rows the
/// status route returns. There is no typed server-side projection of a reading yet — which means the rule
/// that only a <see cref="AiDishFacetConfidence.Likely"/> reading pre-fills a control lives in the client,
/// not here. If a second caller ever appears, that rule should move into a service model before it is
/// duplicated.
/// </para>
/// </remarks>
public static class AiDishFacetFields
{
    /// <summary>The code a facet was read as: <c>facet.Cuisine</c>, <c>facet.DishType</c>, <c>facet.Method</c>.</summary>
    /// <remarks>
    /// The enum member's own name rather than a lower-cased variant, matching
    /// <c>observation.{Aspect}</c> in IMG-004: the row is read back by parsing this segment into the enum, and
    /// a second casing convention would be a second thing to keep in step for no gain.
    /// </remarks>
    public static string Code(AiDishFacet facet) =>
        string.Create(CultureInfo.InvariantCulture, $"facet.{facet}");

    /// <summary>How sure that reading is: <c>facet.Cuisine.confidence</c>. Absent where the facet was declined.</summary>
    public static string Confidence(AiDishFacet facet) =>
        string.Create(CultureInfo.InvariantCulture, $"facet.{facet}.confidence");

    /// <summary>Which words led there, or why none did: <c>facet.Cuisine.rationale</c>. Always present.</summary>
    public static string Rationale(AiDishFacet facet) =>
        string.Create(CultureInfo.InvariantCulture, $"facet.{facet}.rationale");
}

/// <summary>
/// The task-input keys a dish-facet request travels under, between
/// <c>AiDishFacetsRequestBusiness</c> and <see cref="DishFacetSuggestionAiTaskHandler"/>.
/// </summary>
/// <remarks>
/// One key, because the dish name is the whole request. There is deliberately no key for a cuisine, a course
/// or a method the creator already chose: handing the model their other answers would invite it to reason
/// from those instead of from the name, which is how a suggestion starts agreeing with itself.
/// <strong>So every reading covers all three facets</strong>, and which of them may touch a control is the
/// caller's decision, taken against what the creator has already set — not something this request narrows.
/// </remarks>
public static class AiDishFacetInputs
{
    /// <summary>The creator's own name for the dish, verbatim. Required; the task is nothing without it.</summary>
    public const string DishName = "dishName";
}
