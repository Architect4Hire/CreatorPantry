using System.Globalization;
using CreatorPantry.Domain.Modules.Ai.Data.Entities;
using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>What composing an accepted draft produced.</summary>
/// <param name="Draft">The recipe to create.</param>
/// <param name="RewrittenChangeIds">
/// Accepted changes whose value the creator replaced before accepting.
/// </param>
/// <param name="DroppedChangeIds">
/// Accepted changes that produced nothing: a part the recipe create contract has nowhere to put (see
/// <see cref="ProposedRecipeDraft"/>), a field this view does not recognise, a value that would not parse, or
/// a row whose group the creator declined. Counted rather than skipped, so "accepted" never silently means
/// "and nothing came of it".
/// </param>
/// <remarks>
/// <strong>A rewritten change is still recorded <see cref="AiChangeDisposition.Accepted"/>, and that is not
/// the same call the proposal panel makes.</strong> There, an edited change is recorded as declined because
/// the disposition route <em>cannot apply it</em> — the contract carries ids only, so the creator's words have
/// to reach the recipe by some other path, and saying the change was taken would be false. Here the creator's
/// words are applied, by this call, into the very row the change describes: the part of the draft was taken,
/// and only its wording changed. <see cref="RewrittenChangeIds"/> is how the record says whose wording it was.
/// </remarks>
internal sealed record AiDraftComposition(
    ProposedRecipeDraft Draft,
    IReadOnlySet<Guid> RewrittenChangeIds,
    IReadOnlySet<Guid> DroppedChangeIds);

/// <summary>
/// Turns the accepted part of a stored first-draft proposal, plus the creator's own rewrites, into the recipe
/// they asked to be created.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The inverse of <see cref="RecipeFirstDraftAiTaskHandler"/>'s translation, and it relies on the same
/// convention.</strong> Group membership is positional: nothing existed on either end of a group/line
/// relationship when the proposal was written, so a line's <c>Add</c> row does not name its group — it
/// follows it. The changes are read in <c>SortOrder</c>, which is the order the handler emitted them in, so
/// "the group this line belongs to" is "the most recent group <c>Add</c> earlier in the list".
/// </para>
/// <para>
/// <strong>Declining a group declines what is inside it.</strong> A selection that takes an ingredient line
/// but not the group holding it describes a recipe with a line belonging to nothing, and there is no sensible
/// reading of that — so an unaccepted group takes its unaccepted children with it, and its accepted children
/// are reported as dropped rather than reparented somewhere the creator did not put them.
/// </para>
/// <para>
/// <strong>Nothing here reads the model's answer.</strong> Every value comes from a stored
/// <see cref="AiStructuredChange"/> row that was validated when the proposal was assembled, or from a
/// creator's edit that was checked against those rows. There is no second parse of provider output.
/// </para>
/// </remarks>
internal static class AiRecipeDraftComposer
{
    /// <summary>The recipe-level fields a change may set. Anything else is not something a recipe carries.</summary>
    private static readonly HashSet<string> RecipeFields = new(StringComparer.Ordinal)
    {
        "title", "description", "notes",
        "prepTimeMinutes", "cookTimeMinutes", "restTimeMinutes", "totalTimeMinutes",
        "yieldText", "yieldQuantity", "servingCount",
    };

    /// <summary>
    /// Recipe-level fields a draft can propose that a created recipe has nowhere to put.
    /// </summary>
    /// <remarks>
    /// Known and named, so an accepted one is reported as dropped rather than falling through the recipe-field
    /// check as though the model had invented it. <c>yieldUnitText</c> is free text where the column is a
    /// measurement id; <c>servingSize</c> needs a yield unit that therefore cannot be set.
    /// </remarks>
    private static readonly HashSet<string> UnmappableRecipeFields = new(StringComparer.Ordinal)
    {
        "yieldUnitText", "servingSize",
    };

    /// <summary>Which fields each row kind may carry, so an edit cannot address the wrong one.</summary>
    private static readonly Dictionary<AiChangeTargetKind, HashSet<string>> SettableFields = new()
    {
        [AiChangeTargetKind.Ingredient] = new(StringComparer.Ordinal)
        {
            "ingredientNameText", "unitText", "quantity", "quantityUpper", "preparationNote", "isOptional",
        },
        [AiChangeTargetKind.InstructionStep] = new(StringComparer.Ordinal) { "note", "durationMinutes" },
        [AiChangeTargetKind.Equipment] = new(StringComparer.Ordinal) { "note", "isOptional" },
        [AiChangeTargetKind.IngredientGroup] = new(StringComparer.Ordinal) { "isOptional" },
        [AiChangeTargetKind.InstructionGroup] = new(StringComparer.Ordinal) { "isOptional" },
    };

    /// <summary>
    /// Whether an edit addresses something it could sensibly replace.
    /// </summary>
    /// <remarks>
    /// An <c>Add</c> row's own value is the line, step or item itself, which is what a creator rewrites in the
    /// review UI; a <c>Set</c> row's value is the field it names. Anything else — a field belonging to a
    /// different kind of row, or a change this proposal does not contain — is refused, because the alternative
    /// is writing a creator's words into a recipe field they were not looking at.
    /// </remarks>
    public static bool CanEdit(AiStructuredChange change, string field) =>
        change.ChangeKind switch
        {
            AiChangeKind.Add => change.TargetKind switch
            {
                AiChangeTargetKind.Ingredient or AiChangeTargetKind.Equipment => field == "displayText",
                AiChangeTargetKind.InstructionStep => field == "text",
                AiChangeTargetKind.IngredientGroup or AiChangeTargetKind.InstructionGroup => field == "title",
                _ => false,
            },
            AiChangeKind.Set when change.TargetKind is AiChangeTargetKind.Recipe =>
                change.FieldName == field && RecipeFields.Contains(field),
            AiChangeKind.Set =>
                change.FieldName == field
                && SettableFields.TryGetValue(change.TargetKind, out var allowed)
                && allowed.Contains(field),
            _ => false,
        };

    /// <summary>
    /// Composes the recipe an accepted selection describes.
    /// </summary>
    /// <param name="changes">Every change on the proposal, in the order it was stored.</param>
    /// <param name="accepted">The changes the creator accepted.</param>
    /// <param name="edits">The creator's own wording, keyed by change id and field name.</param>
    public static AiDraftComposition Compose(
        IReadOnlyList<AiStructuredChange> changes,
        IReadOnlySet<Guid> accepted,
        IReadOnlyDictionary<(Guid ChangeId, string Field), string> edits)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(edits);

        var rewritten = new HashSet<Guid>();
        var dropped = new HashSet<Guid>();

        var recipeFields = new Dictionary<string, string>(StringComparer.Ordinal);
        var ingredientGroups = new List<DraftGroup<LineBuilder>>();
        var instructionGroups = new List<DraftGroup<StepBuilder>>();

        // The row a Set change addresses, populated as each Add row opens one.
        var lines = new Dictionary<Guid, LineBuilder>();
        var steps = new Dictionary<Guid, StepBuilder>();

        // Which group a line or step belongs to, and whether that group was itself accepted.
        DraftGroup<LineBuilder>? currentIngredientGroup = null;
        DraftGroup<StepBuilder>? currentInstructionGroup = null;

        string Value(AiStructuredChange change, string field, string? fallback)
        {
            if (!edits.TryGetValue((change.Id, field), out var edited)) return fallback ?? string.Empty;
            rewritten.Add(change.Id);
            return edited;
        }

        foreach (var change in changes.OrderBy(change => change.SortOrder))
        {
            var isAccepted = accepted.Contains(change.Id);

            if (change.ChangeKind is AiChangeKind.Add)
            {
                switch (change.TargetKind)
                {
                    case AiChangeTargetKind.IngredientGroup:
                        currentIngredientGroup = OpenGroup<LineBuilder>(change, isAccepted, Value);
                        if (isAccepted) ingredientGroups.Add(currentIngredientGroup);
                        break;

                    case AiChangeTargetKind.InstructionGroup:
                        currentInstructionGroup = OpenGroup<StepBuilder>(change, isAccepted, Value);
                        if (isAccepted) instructionGroups.Add(currentInstructionGroup);
                        break;

                    case AiChangeTargetKind.Ingredient:
                    {
                        if (!isAccepted) break;

                        // The group it belongs to was declined, so this has nowhere to live. Reported rather
                        // than reparented — see the type's remarks.
                        if (currentIngredientGroup is null || !currentIngredientGroup.Accepted)
                        {
                            dropped.Add(change.Id);
                            break;
                        }

                        var line = new LineBuilder { DisplayText = Value(change, "displayText", change.AfterValue) };
                        currentIngredientGroup.Items.Add(line);
                        if (change.TargetId is { } lineId) lines[lineId] = line;
                        break;
                    }

                    case AiChangeTargetKind.InstructionStep:
                    {
                        if (!isAccepted) break;

                        if (currentInstructionGroup is null || !currentInstructionGroup.Accepted)
                        {
                            dropped.Add(change.Id);
                            break;
                        }

                        var step = new StepBuilder { Text = Value(change, "text", change.AfterValue) };
                        currentInstructionGroup.Items.Add(step);
                        if (change.TargetId is { } stepId) steps[stepId] = step;
                        break;
                    }

                    // Equipment is a real thing the model proposed and the creator may well have ticked. It
                    // simply has nowhere to be created — see ProposedRecipeDraft — so it is counted, not lost.
                    case AiChangeTargetKind.Equipment:
                        if (isAccepted) dropped.Add(change.Id);
                        break;

                    default:
                        if (isAccepted) dropped.Add(change.Id);
                        break;
                }

                continue;
            }

            if (!isAccepted)
            {
                continue;
            }

            // A Remove or a Move accepted on a draft describes altering something that does not exist yet.
            // The handler never emits one, so this is unreachable today — counted rather than skipped because
            // "accepted but produced nothing" is exactly what DroppedChangeIds is for.
            if (change.ChangeKind is not AiChangeKind.Set || change.FieldName is not { } field)
            {
                dropped.Add(change.Id);
                continue;
            }

            if (change.TargetKind is AiChangeTargetKind.Recipe && change.TargetId is null)
            {
                // Unmappable and unrecognised are both dropped, and deliberately stay separate cases: one is
                // a field a recipe genuinely has nowhere for, the other is a name nothing here has heard of.
                if (!RecipeFields.Contains(field) || UnmappableRecipeFields.Contains(field))
                {
                    dropped.Add(change.Id);
                }
                else
                {
                    recipeFields[field] = Value(change, field, change.AfterValue);
                }

                continue;
            }

            var target = change.TargetId;

            if (target is { } id && lines.TryGetValue(id, out var ownerLine))
            {
                if (!ApplyToLine(ownerLine, field, Value(change, field, change.AfterValue)))
                {
                    dropped.Add(change.Id);
                }

                continue;
            }

            if (target is { } stepTarget && steps.TryGetValue(stepTarget, out var ownerStep))
            {
                if (!ApplyToStep(ownerStep, field, Value(change, field, change.AfterValue)))
                {
                    dropped.Add(change.Id);
                }

                continue;
            }

            // A group's own isOptional, or a field on a row whose Add was declined. Neither reaches a recipe:
            // RecipeIngredientGroup has no IsOptional column, and a field without its row has no owner.
            dropped.Add(change.Id);
        }

        // Whether the result is a recipe anyone would accept is not decided here. A missing title is refused
        // by the create validator, which is the one place that rule lives; the caller checks for it before
        // opening a transaction only so the refusal is cheap, not so it is enforced twice.
        var draft = new ProposedRecipeDraft
        {
            Title = recipeFields.TryGetValue("title", out var title) ? title : string.Empty,
            Description = Text(recipeFields, "description"),
            Notes = Text(recipeFields, "notes"),
            PrepTimeMinutes = Minutes(recipeFields, "prepTimeMinutes"),
            CookTimeMinutes = Minutes(recipeFields, "cookTimeMinutes"),
            RestTimeMinutes = Minutes(recipeFields, "restTimeMinutes"),
            TotalTimeMinutes = Minutes(recipeFields, "totalTimeMinutes"),
            YieldText = Text(recipeFields, "yieldText"),
            YieldQuantity = Number(recipeFields, "yieldQuantity"),
            ServingCount = Number(recipeFields, "servingCount"),
            IngredientGroups =
            [
                .. ingredientGroups.Select(group => new ProposedRecipeDraftIngredientGroup(
                    group.Title,
                    [.. group.Items.Select(line => line.Build())])),
            ],
            Instructions =
            [
                .. instructionGroups.Select(group => new ProposedRecipeDraftInstructionGroup(
                    group.Title,
                    [.. group.Items.Select(step => step.Build())])),
            ],
        };

        return new AiDraftComposition(draft, rewritten, dropped);
    }

    private static DraftGroup<TItem> OpenGroup<TItem>(
        AiStructuredChange change,
        bool accepted,
        Func<AiStructuredChange, string, string?, string> value)
    {
        var title = accepted ? value(change, "title", change.AfterValue) : change.AfterValue ?? string.Empty;

        return new DraftGroup<TItem>
        {
            // Blank is the same as absent: an untitled group travels with no heading (recipes.md), and an
            // empty string would become a heading made of nothing.
            Title = title.Length == 0 ? null : title,
            Accepted = accepted,
        };
    }

    /// <summary>
    /// Places one value on a line. False when the value could not be read, so the caller can count it as
    /// dropped rather than report a part as taken whose value is not in the recipe.
    /// </summary>
    private static bool ApplyToLine(LineBuilder line, string field, string value) => field switch
    {
        "ingredientNameText" => Set(() => line.IngredientNameText = value),
        "unitText" => Set(() => line.UnitText = value),
        "preparationNote" => Set(() => line.PreparationNote = value),
        "quantity" => Assign(Decimal(value), parsed => line.Quantity = parsed),
        "quantityUpper" => Assign(Decimal(value), parsed => line.QuantityUpper = parsed),
        "isOptional" => Assign(Flag(value), parsed => line.IsOptional = parsed),
        _ => false,
    };

    /// <inheritdoc cref="ApplyToLine"/>
    private static bool ApplyToStep(StepBuilder step, string field, string value) => field switch
    {
        "note" => Set(() => step.Note = value),
        "durationMinutes" => Assign(Integer(value), parsed => step.DurationMinutes = parsed),
        _ => false,
    };

    private static bool Set(Action assign)
    {
        assign();
        return true;
    }

    private static bool Assign<T>(T? parsed, Action<T> assign)
        where T : struct
    {
        if (parsed is not { } value) return false;

        assign(value);
        return true;
    }

    /// <summary>
    /// A boolean as written.
    /// </summary>
    /// <remarks>
    /// Parsed rather than tested for inequality with <c>"false"</c>. The handler only ever emits
    /// <c>"true"</c>, so the one route to anything else is a creator's rewrite — and <c>"False"</c>,
    /// <c>"0"</c> and <c>"no"</c> would all have read as <em>optional</em>, which is the opposite of what a
    /// creator typing one of them meant.
    /// </remarks>
    private static bool? Flag(string value) => bool.TryParse(value, out var parsed) ? parsed : null;

    private static string? Text(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <remarks>
    /// A value that will not parse is dropped rather than guessed at. The stored row came from a validated
    /// answer or a creator's own edit, so this is a backstop; turning "about 20" into a number would be the
    /// seam inventing a time.
    /// </remarks>
    private static int? Minutes(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) ? Integer(value) : null;

    private static decimal? Number(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) ? Decimal(value) : null;

    private static int? Integer(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static decimal? Decimal(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private sealed class DraftGroup<TItem>
    {
        public string? Title { get; init; }

        public bool Accepted { get; init; }

        public List<TItem> Items { get; } = [];
    }

    private sealed class LineBuilder
    {
        public string DisplayText { get; set; } = string.Empty;

        public string? IngredientNameText { get; set; }

        public string? UnitText { get; set; }

        public decimal? Quantity { get; set; }

        public decimal? QuantityUpper { get; set; }

        public string? PreparationNote { get; set; }

        public bool IsOptional { get; set; }

        public ProposedRecipeDraftIngredient Build() => new()
        {
            DisplayText = DisplayText,
            IngredientNameText = IngredientNameText,
            UnitText = UnitText,
            Quantity = Quantity,
            QuantityUpper = QuantityUpper,
            PreparationNote = PreparationNote,
            IsOptional = IsOptional,
        };
    }

    private sealed class StepBuilder
    {
        public string Text { get; set; } = string.Empty;

        public string? Note { get; set; }

        public int? DurationMinutes { get; set; }

        public ProposedRecipeDraftStep Build() => new(Text, Note, DurationMinutes);
    }
}
