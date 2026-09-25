using System.Globalization;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>
/// Whether a value offered for one recipe field can actually live in it: the right kind, and within the field's
/// own bound.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This exists because the apply path has no ViewModel.</strong> A hand-typed edit is bounded by
/// <see cref="UpdateRecipeViewModelValidator"/>; an accepted AI proposal arrives as change rows and never passes
/// through it. Without a bound of its own, an over-long or out-of-range accepted value reached SQL Server as a
/// truncation or check-constraint error — which the recipe data layer correctly declines to call a concurrency
/// conflict and rethrows, producing a 500 and losing the creator's decision, where the honest answer is a
/// refusal naming the field.
/// </para>
/// <para>
/// <strong>It lives in this module because the fields and the limits do.</strong> The AI module calls it while
/// computing a diff, so an unusable value is never offered; this module calls it again while applying, so a
/// proposal row written before the check existed refuses rather than reaching the database. One table, two call
/// sites, and no second opinion about how long a title may be.
/// </para>
/// <para>
/// It deliberately does <em>not</em> check format. The one field that needed a format rule — <c>sourceUrl</c>,
/// whose scheme check keeps a <c>javascript:</c> value out of something later rendered as a link — is no longer
/// a field a proposal may set at all, because a model cannot know where a recipe came from. A field needing a
/// format rule is a field to reconsider offering.
/// </para>
/// </remarks>
public static class ProposedRecipeValues
{
    /// <param name="fieldName">The field, in the vocabulary a proposal addresses it by.</param>
    /// <param name="value">The proposed value as an invariant string; <c>null</c> clears, which is always fine.</param>
    public static bool Accepts(string fieldName, string? value) =>
        value is null || KindOf(fieldName) switch
        {
            // The same bounds the edge validator applies: a time in minutes between zero and a year. Generous,
            // because curing and ageing are real recipe steps, and still enough to catch a millisecond value
            // pasted into a field that means minutes.
            ValueKind.Minutes => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                && minutes is >= 0 and <= RecipePolicy.MaxTimeMinutes,

            ValueKind.Number => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            ValueKind.Flag => bool.TryParse(value, out _),
            _ => value.Length <= MaxLengthOf(fieldName),
        };

    /// <summary>The bound one text field carries, for a message that can say what it is.</summary>
    public static int MaxLength(string fieldName) => MaxLengthOf(fieldName);

    private static ValueKind KindOf(string fieldName) => fieldName switch
    {
        "prepTimeMinutes" or "cookTimeMinutes" or "restTimeMinutes" or "totalTimeMinutes"
            or "durationMinutes" => ValueKind.Minutes,
        "yieldQuantity" or "quantity" or "quantityUpper" or "temperatureValue" => ValueKind.Number,
        "isOptional" => ValueKind.Flag,
        _ => ValueKind.Text,
    };

    /// <summary>
    /// The field's own limit, from <see cref="RecipePolicy"/>.
    /// </summary>
    /// <remarks>
    /// Named per field rather than defaulted to the widest, because the widest is what caused the problem: a
    /// 4000-character title is not a title. <c>title</c> is a field of both a recipe and an instruction group and
    /// a field name here carries no target kind; the two limits are equal today, and the narrower of them is used
    /// so that a day when they stop being equal cannot make this the looser answer.
    /// </remarks>
    private static int MaxLengthOf(string fieldName) => fieldName switch
    {
        "title" => Math.Min(RecipePolicy.TitleMaxLength, RecipePolicy.GroupTitleMaxLength),
        "yieldText" => RecipePolicy.YieldTextMaxLength,
        "description" => RecipePolicy.DescriptionMaxLength,
        "headnote" or "notes" or "storageNotes" => RecipePolicy.LongTextMaxLength,
        "attributionText" => RecipePolicy.AttributionMaxLength,
        "text" => RecipePolicy.StepTextMaxLength,
        "note" => RecipePolicy.NoteMaxLength,
        "displayText" or "ingredientNameText" => RecipePolicy.LineTextMaxLength,
        "preparationNote" => RecipePolicy.NoteMaxLength,

        // An unrecognised field. Unreachable through either caller, because a field absent from the settable
        // allow-list is refused before this runs — so the widest recipe limit is the safe answer rather than a
        // licence.
        _ => RecipePolicy.LongTextMaxLength,
    };

    private enum ValueKind
    {
        Text,
        Minutes,
        Number,
        Flag,
    }
}
