using CreatorPantry.Domain.Managers.Paging;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>Whether a submission is creating a recipe or editing one, which is all the per-position rules differ by.</summary>
public enum RecipeInputMode
{
    /// <summary>Nothing exists yet for an id to name, so a submitted id is a client bug.</summary>
    Create = 0,

    /// <summary>An id names an existing row to edit in place; only the empty guid is meaningless.</summary>
    Update = 1,
}

/// <summary>
/// Where a refused ingredient line or instruction step is, as a field path the client can resolve to the row it
/// submitted.
/// </summary>
/// <remarks>
/// <para>
/// The rules that are about a whole list stay on that list's own key — a total, or two rows naming one id, is
/// not about any one line. Everything a single group, line or step can be wrong about on its own is reported
/// here instead, keyed by position, because "A quantity must be greater than zero." about a recipe with forty
/// lines is a sentence no creator can act on.
/// </para>
/// <para>
/// The first segment is PascalCase and every later one camelCase deliberately:
/// <c>OperationError.Validation</c> lowercases only a key's first character, so this is the shape
/// that reads as <c>ingredientGroups[0].ingredients[2].quantity</c> on the wire.
/// </para>
/// <para>
/// Walked by index, never through the validators' <c>OfType</c> helpers — those filter nulls out and so discard
/// the very positions an index has to refer to.
/// </para>
/// </remarks>
public static class RecipeInputPositions
{
    public static string IngredientGroupPath(int group, string field) => $"IngredientGroups[{group}].{field}";

    public static string IngredientLinePath(int group, int line, string field) =>
        $"IngredientGroups[{group}].ingredients[{line}].{field}";

    public static string InstructionGroupPath(int group, string field) => $"Instructions[{group}].{field}";

    public static string InstructionStepPath(int group, int step, string field) =>
        $"Instructions[{group}].steps[{step}].{field}";

    /// <summary>
    /// Reports every ingredient problem that belongs to one group or one line, at its position.
    /// </summary>
    /// <remarks>
    /// A null group or line is skipped rather than reported here: that is a malformed request, the list-level
    /// rules refuse it under the list's own key, and there is nothing at that position for a creator to fix.
    /// Not a cascade either — a line with a blank text and a negative quantity is wrong twice, and a creator
    /// fixing one and being told about the other is a round trip this exists to remove.
    /// </remarks>
    public static void AddIngredientFailures(
        IReadOnlyList<RecipeIngredientGroupInputViewModel?>? groups,
        RecipeInputMode mode,
        Action<string, string> fail)
    {
        if (groups is null)
        {
            return;
        }

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            if (group is null)
            {
                continue;
            }

            if (mode == RecipeInputMode.Create && group.Id is not null)
            {
                fail(IngredientGroupPath(g, "id"), "A new recipe's ingredients cannot name an existing group.");
            }

            if (mode == RecipeInputMode.Update && group.Id == Guid.Empty)
            {
                fail(IngredientGroupPath(g, "id"), "That is not a valid ingredient group reference.");
            }

            if (Trim(group.Title).Length > RecipePolicy.GroupTitleMaxLength)
            {
                fail(
                    IngredientGroupPath(g, "title"),
                    $"An ingredient group heading can be at most {RecipePolicy.GroupTitleMaxLength} characters.");
            }

            var lines = group.Ingredients;
            if (lines is null)
            {
                continue;
            }

            for (var l = 0; l < lines.Count; l++)
            {
                var line = lines[l];
                if (line is null)
                {
                    continue;
                }

                AddIngredientLineFailures(g, l, line, mode, fail);
            }
        }
    }

    private static void AddIngredientLineFailures(
        int g,
        int l,
        RecipeIngredientInputViewModel line,
        RecipeInputMode mode,
        Action<string, string> fail)
    {
        if (mode == RecipeInputMode.Create && line.Id is not null)
        {
            fail(IngredientLinePath(g, l, "id"), "A new recipe's ingredients cannot name an existing line.");
        }

        if (mode == RecipeInputMode.Update && line.Id == Guid.Empty)
        {
            fail(IngredientLinePath(g, l, "id"), "That is not a valid ingredient line reference.");
        }

        if (string.IsNullOrWhiteSpace(line.DisplayText))
        {
            fail(IngredientLinePath(g, l, "displayText"), "An ingredient line cannot be blank.");
        }
        else if (Trim(line.DisplayText).Length > RecipePolicy.LineTextMaxLength)
        {
            fail(
                IngredientLinePath(g, l, "displayText"),
                $"An ingredient line can be at most {RecipePolicy.LineTextMaxLength} characters.");
        }

        if (Trim(line.PreparationNote).Length > RecipePolicy.NoteMaxLength)
        {
            fail(
                IngredientLinePath(g, l, "preparationNote"),
                $"A preparation note can be at most {RecipePolicy.NoteMaxLength} characters.");
        }

        if (Trim(line.IngredientNameText).Length > RecipePolicy.IngredientNameTextMaxLength)
        {
            fail(
                IngredientLinePath(g, l, "ingredientNameText"),
                $"An ingredient name can be at most {RecipePolicy.IngredientNameTextMaxLength} characters.");
        }

        if (Trim(line.UnitText).Length > RecipePolicy.UnitTextMaxLength)
        {
            fail(
                IngredientLinePath(g, l, "unitText"),
                $"A unit can be at most {RecipePolicy.UnitTextMaxLength} characters.");
        }

        if (line.Quantity is not null and <= 0m)
        {
            fail(IngredientLinePath(g, l, "quantity"), "A quantity must be greater than zero.");
        }

        if (line.QuantityUpper is not null and <= 0m)
        {
            fail(IngredientLinePath(g, l, "quantityUpper"), "A quantity must be greater than zero.");
        }

        // Mirrors CK_RecipeIngredients_Quantity_Range: a range needs both ends and has to run upward. Reported on
        // the upper bound, because that is the half a creator added to make it a range.
        if (line.QuantityUpper is not null and > 0m
            && (line.Quantity is null || line.QuantityUpper <= line.Quantity))
        {
            fail(
                IngredientLinePath(g, l, "quantityUpper"),
                "A quantity range needs a lower amount, and the upper amount must be greater than it.");
        }

        if (line.MeasurementUnitId == Guid.Empty)
        {
            fail(IngredientLinePath(g, l, "measurementUnitId"), "That is not a valid unit reference.");
        }

        if (line.IngredientId == Guid.Empty)
        {
            fail(IngredientLinePath(g, l, "ingredientId"), "That is not a valid ingredient reference.");
        }
    }

    /// <summary>Reports every instruction problem that belongs to one group or one step, at its position.</summary>
    public static void AddInstructionFailures(
        IReadOnlyList<RecipeInstructionGroupInputViewModel?>? groups,
        RecipeInputMode mode,
        Action<string, string> fail)
    {
        if (groups is null)
        {
            return;
        }

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            if (group is null)
            {
                continue;
            }

            if (mode == RecipeInputMode.Create && group.Id is not null)
            {
                fail(InstructionGroupPath(g, "id"), "A new recipe's instructions cannot name an existing group.");
            }

            if (mode == RecipeInputMode.Update && group.Id == Guid.Empty)
            {
                fail(InstructionGroupPath(g, "id"), "That is not a valid instruction group reference.");
            }

            if (Trim(group.Title).Length > RecipePolicy.GroupTitleMaxLength)
            {
                fail(
                    InstructionGroupPath(g, "title"),
                    $"An instruction group heading can be at most {RecipePolicy.GroupTitleMaxLength} characters.");
            }

            var steps = group.Steps;
            if (steps is null)
            {
                continue;
            }

            for (var s = 0; s < steps.Count; s++)
            {
                var step = steps[s];
                if (step is null)
                {
                    continue;
                }

                AddInstructionStepFailures(g, s, step, mode, fail);
            }
        }
    }

    private static void AddInstructionStepFailures(
        int g,
        int s,
        RecipeInstructionStepInputViewModel step,
        RecipeInputMode mode,
        Action<string, string> fail)
    {
        if (mode == RecipeInputMode.Create && step.Id is not null)
        {
            fail(InstructionStepPath(g, s, "id"), "A new recipe's instructions cannot name an existing step.");
        }

        if (mode == RecipeInputMode.Update && step.Id == Guid.Empty)
        {
            fail(InstructionStepPath(g, s, "id"), "That is not a valid instruction step reference.");
        }

        if (string.IsNullOrWhiteSpace(step.Text))
        {
            fail(InstructionStepPath(g, s, "text"), "An instruction step cannot be blank.");
        }
        else if (Trim(step.Text).Length > RecipePolicy.StepTextMaxLength)
        {
            fail(
                InstructionStepPath(g, s, "text"),
                $"An instruction step can be at most {RecipePolicy.StepTextMaxLength} characters.");
        }

        if (Trim(step.Note).Length > RecipePolicy.NoteMaxLength)
        {
            fail(InstructionStepPath(g, s, "note"), $"A step note can be at most {RecipePolicy.NoteMaxLength} characters.");
        }

        if (step.DurationMinutes is not null and (< 0 or > RecipePolicy.MaxTimeMinutes))
        {
            fail(
                InstructionStepPath(g, s, "durationMinutes"),
                "A step's duration must be a time in minutes between 0 and one year.");
        }

        if (step.TechniqueId == Guid.Empty)
        {
            fail(InstructionStepPath(g, s, "techniqueId"), "That is not a valid technique reference.");
        }

        if (step.TemperatureUnitId == Guid.Empty)
        {
            fail(InstructionStepPath(g, s, "temperatureUnitId"), "That is not a valid unit reference.");
        }

        // Reported on the unit, which is the half a creator is most likely to have left off.
        if ((step.TemperatureValue is null) != (step.TemperatureUnitId is null))
        {
            fail(
                InstructionStepPath(g, s, "temperatureUnitId"),
                "A step's temperature needs both a value and a unit, or neither.");
        }
    }

    private static string Trim(string? value) => value?.Trim() ?? string.Empty;
}
