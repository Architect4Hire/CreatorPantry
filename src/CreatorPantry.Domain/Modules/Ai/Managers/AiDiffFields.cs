using System.Globalization;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// Which fields a proposal may set on each target, and how the pinned version's current value is read.
/// </summary>
/// <remarks>
/// <para>
/// The allow-list the structured-output validator deferred to the layer that knows the schema. A field name
/// absent from here is not a field a model may change, whatever it says.
/// </para>
/// <para>
/// <strong>Every identifier field is missing on purpose.</strong> There is no <c>measurementUnitId</c>,
/// <c>ingredientId</c>, <c>techniqueId</c>, <c>temperatureUnitId</c>, <c>cuisineId</c> or
/// <c>yieldUnitId</c> here, and there will not be. A model naming a <c>Guid</c> is a model inventing an
/// identifier: it cannot know which row an id refers to, and a plausible-looking wrong one is worse than a
/// refusal. Resolving an ingredient or a unit belongs to the matching seam, and changing a unit changes what
/// a quantity means, which <c>CALC</c> requires deterministic code to do. The model proposes text and
/// numbers; the server resolves references.
/// </para>
/// <para>
/// <strong>Ingredients are absent for a different reason, and it is a capability gap rather than a rule.</strong>
/// The recipe update seam has patch fields for header content, tags, status and instructions, and none for
/// ingredients — so an accepted ingredient change has no path through ordinary recipe validation. Offering one
/// would mean a creator reviewing a change, accepting it, and being told no at the last moment, which is worse
/// than not offering it. When <c>UpdateRecipeViewModel</c> grows an ingredients patch field, the ingredient and
/// ingredient-group entries belong back here — <c>displayText</c>, <c>ingredientNameText</c>, <c>quantity</c>,
/// <c>quantityUpper</c>, <c>preparationNote</c>, <c>isOptional</c>, and a group's <c>title</c>.
/// </para>
/// <para>
/// <strong><c>sourceUrl</c> is absent, and it was here until an audit pointed out what that meant.</strong> A
/// model cannot know where a recipe came from, so proposing a source is inventing one — the same objection as an
/// identifier. Worse, the shape check that keeps a <c>javascript:</c> or <c>data:</c> value out of a field later
/// rendered as a link lives in <c>UpdateRecipeViewModelValidator</c>, which the apply path deliberately does not
/// run. Offering the field put a stored-link vector behind a diff a creator would plausibly accept.
/// </para>
/// <para>
/// <strong>Every value is bounded here too</strong>, by <see cref="RecipePolicy"/>'s own limits rather than by
/// <c>AiPolicy.ChangeValueMaxLength</c>, which is larger than most of them. Without that, an over-long accepted
/// value reached SQL Server as a truncation error, which the recipe data layer correctly declines to call a
/// conflict and rethrows — a 500 where the honest answer is a refusal, and the creator's decision lost with it.
/// </para>
/// <para>
/// One consequence to know: <c>AiOperationScope.Ingredients</c> therefore permits no settable field at all, so
/// a proposal requested in that scope can currently only add, remove or reorder.
/// </para>
/// </remarks>
public static class AiDiffFields
{
    /// <summary>Reads one field's current value from the pinned snapshot, as an invariant string.</summary>
    /// <remarks>
    /// Strings both sides, because the change row stores strings and a before value that formatted
    /// differently from its after value would show a difference where there is none.
    /// </remarks>
    private delegate string? Read(object target);

    private static readonly Dictionary<string, Read> RecipeFields = Fields<RecipeSnapshotHeader>(
        ("title", header => header.Title),
        ("description", header => header.Description),
        ("headnote", header => header.Headnote),
        ("notes", header => header.Notes),
        ("storageNotes", header => header.StorageNotes),
        ("attributionText", header => header.AttributionText),
        ("prepTimeMinutes", header => Format(header.PrepTimeMinutes)),
        ("cookTimeMinutes", header => Format(header.CookTimeMinutes)),
        ("restTimeMinutes", header => Format(header.RestTimeMinutes)),
        ("totalTimeMinutes", header => Format(header.TotalTimeMinutes)),
        ("yieldText", header => header.YieldText),
        ("yieldQuantity", header => Format(header.YieldQuantity)));

    private static readonly Dictionary<string, Read> InstructionGroupFields = Fields<RecipeSnapshotInstructionGroup>(
        ("title", group => group.Title));

    private static readonly Dictionary<string, Read> InstructionStepFields = Fields<RecipeSnapshotInstructionStep>(
        ("text", step => step.Text),
        ("note", step => step.Note),
        ("durationMinutes", step => Format(step.DurationMinutes)),
        ("temperatureValue", step => Format(step.TemperatureValue)));

    /// <summary>The fields settable on one target kind; empty for a kind that only supports add and remove.</summary>
    public static IReadOnlyCollection<string> For(AiChangeTargetKind kind) => Table(kind).Keys;

    public static bool IsSettable(AiChangeTargetKind kind, string fieldName) =>
        Table(kind).ContainsKey(fieldName);

    /// <summary>The pinned value of one field, or null when the field is legitimately empty.</summary>
    public static string? CurrentValue(AiChangeTargetKind kind, string fieldName, object target) =>
        Table(kind)[fieldName](target);

    /// <summary>
    /// Whether a proposed value can actually live in the field: the right kind, and within the field's bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A quantity is a decimal the scaling and conversion code depends on. "About 2 cups" is refused here
    /// rather than stored and discovered at acceptance — recipes.md says non-scalable language is flagged, not
    /// smuggled into a numeric field, and the model can say it in <c>displayText</c> with a warning beside it.
    /// </para>
    /// <para>
    /// <strong>Delegated to the recipe module, which owns the fields and the limits.</strong>
    /// <see cref="ProposedRecipeValues"/> is called from here while a diff is computed, so an unusable value is
    /// never offered, and again by the apply path, so a proposal row written before the check existed refuses
    /// rather than reaching the database as a truncation error. One table, two call sites, and no second opinion
    /// about how long a title may be. This wrapper stays so that a caller reading the allow-list finds the value
    /// check beside it.
    /// </para>
    /// </remarks>
    public static bool Accepts(string fieldName, string? value) =>
        ProposedRecipeValues.Accepts(fieldName, value);

    private static Dictionary<string, Read> Table(AiChangeTargetKind kind) => kind switch
    {
        AiChangeTargetKind.Recipe => RecipeFields,
        AiChangeTargetKind.InstructionGroup => InstructionGroupFields,
        AiChangeTargetKind.InstructionStep => InstructionStepFields,

        // Equipment, asset links and tags are attached and detached rather than edited in place. A kind with
        // no settable fields is a real answer here, not a gap.
        _ => [],
    };

    private static Dictionary<string, Read> Fields<T>(params (string Name, Func<T, string?> Read)[] fields) =>
        fields.ToDictionary(
            field => field.Name,
            field => new Read(target => field.Read((T)target)),
            StringComparer.Ordinal);

    private static string? Format(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string? Format(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Format(bool value) => value ? "true" : "false";
}
