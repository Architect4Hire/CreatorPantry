using CreatorPantry.Domain.Modules.Recipes.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>One change resolved against the pinned version, ready to be written as a row.</summary>
/// <param name="BeforeValue">
/// Read from the snapshot by this calculator. Never supplied by the model, and never carried through from its
/// answer — the output contract has no field for one.
/// </param>
public sealed record AiResolvedChange(
    AiChangeKind ChangeKind,
    AiChangeTargetKind TargetKind,
    Guid? TargetId,
    string? FieldName,
    string? BeforeValue,
    string? AfterValue,
    int? ProposedPosition,
    int SortOrder);

/// <summary>The diff, or the reason there isn't one.</summary>
public sealed record AiDiffResult(IReadOnlyList<AiResolvedChange>? Changes, AiOutputFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

/// <summary>
/// Turns validated model changes into a diff against the exact pinned version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Pure over a snapshot.</strong> No clock, no context, no database, no provider — so every mapping
/// rule is testable directly, and the staleness question (has the recipe moved past this version?) belongs to
/// the caller that can answer it.
/// </para>
/// <para>
/// <strong>Every before value is read here.</strong> That is the whole point of the stage: a model-supplied
/// before value is an assertion about content the server already holds, and trusting one would let a forged or
/// merely stale claim decide what a diff appears to change. The output contract gives a model nowhere to put
/// one, and this calculator would ignore it if it did.
/// </para>
/// </remarks>
public static class AiDiffCalculator
{
    public static AiDiffResult Calculate(RecipeSnapshotDocument source, AiOutputDocument output)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);

        var resolved = new List<AiResolvedChange>(output.Changes.Count);
        var addressed = new HashSet<(AiChangeTargetKind Kind, Guid? Id, string? Field)>();

        for (var index = 0; index < output.Changes.Count; index++)
        {
            var change = output.Changes[index];

            // Two changes on one target, or two sets of one field. The validator catches these within one
            // answer; this catches them again against the resolved target, which is where "the same ingredient
            // twice" becomes visible even when the model addressed it two different ways.
            if (!addressed.Add((change.TargetKind, change.TargetId, change.FieldName)))
            {
                return Refuse(
                    AiOutputReason.ConflictingChanges,
                    $"Two changes address the same {change.TargetKind}.");
            }

            var resolution = Resolve(source, change, index);

            if (resolution.Failure is not null)
            {
                return new AiDiffResult(null, resolution.Failure);
            }

            resolved.Add(resolution.Change!);
        }

        return new AiDiffResult(resolved, null);
    }

    private static (AiResolvedChange? Change, AiOutputFailure? Failure) Resolve(
        RecipeSnapshotDocument source,
        AiOutputChange change,
        int index)
    {
        // Before anything is read or resolved: a change nobody could accept must not become a row a creator is
        // invited to review. AiChangeApplicability records which pairs the recipe update seam can express and
        // why the others cannot.
        if (!AiChangeApplicability.IsApplicable(change.ChangeKind, change.TargetKind))
        {
            return (null, Failure(
                AiOutputReason.NotApplicable,
                $"A {change.ChangeKind} on a {change.TargetKind} is not a change that can be applied to a "
                    + $"recipe. Applicable on a {change.TargetKind}: "
                    + $"{Describe(AiChangeApplicability.For(change.TargetKind))}."));
        }

        if (change.ChangeKind is AiChangeKind.Set)
        {
            return ResolveSet(source, change, index);
        }

        // Add, Remove and Move do not read a field, but an existing target still has to exist. An addition
        // names the parent it goes into, which must also be real.
        if (change.TargetId is { } targetId && Find(source, change.TargetKind, targetId) is null
            && change.ChangeKind is not AiChangeKind.Add)
        {
            return (null, MissingTarget(change));
        }

        if (change.ChangeKind is AiChangeKind.Move
            && change.ProposedPosition is { } position
            && position > SiblingCount(source, change.TargetKind))
        {
            return (null, Failure(
                AiOutputReason.PositionMisplaced,
                $"A move puts a {change.TargetKind} beyond the end of its parent."));
        }

        return (
            new AiResolvedChange(
                change.ChangeKind,
                change.TargetKind,
                change.TargetId,

                // No field, and therefore no before value to read. A removal's "before" is the row itself,
                // which the target id already names — duplicating its text here would be a second copy of
                // creator content with nothing reading it.
                null,
                null,
                change.AfterValue,
                change.ProposedPosition,
                index),
            null);
    }

    private static (AiResolvedChange? Change, AiOutputFailure? Failure) ResolveSet(
        RecipeSnapshotDocument source,
        AiOutputChange change,
        int index)
    {
        var field = change.FieldName!;

        if (!AiDiffFields.IsSettable(change.TargetKind, field))
        {
            return (null, Failure(
                AiOutputReason.FieldNameMisplaced,
                $"'{field}' is not a field a proposal may set on a {change.TargetKind}. Settable: "
                    + $"{string.Join(", ", AiDiffFields.For(change.TargetKind))}."));
        }

        if (!AiDiffFields.Accepts(field, change.AfterValue))
        {
            return (null, Failure(
                AiOutputReason.DomainInvalid,
                $"The proposed value for '{field}' is not a value that field can hold."));
        }

        var target = change.TargetKind is AiChangeTargetKind.Recipe
            ? source.Recipe
            : Find(source, change.TargetKind, change.TargetId);

        if (target is null)
        {
            return (null, MissingTarget(change));
        }

        // A temperature without a unit is refused by the database — "bake at 180 g" is a food-safety-adjacent
        // fact recorded wrongly — and a change row has nowhere to carry a unit. So a step that has no
        // temperature unit today cannot be given a temperature: offering one would mean a creator accepting a
        // change that could only fail at the moment it was applied.
        if (field is "temperatureValue"
            && change.AfterValue is not null
            && target is RecipeSnapshotInstructionStep { TemperatureUnitId: null })
        {
            return (null, Failure(
                AiOutputReason.NotApplicable,
                "That step records no temperature unit, so a temperature cannot be proposed for it."));
        }

        return (
            new AiResolvedChange(
                AiChangeKind.Set,
                change.TargetKind,
                change.TargetId,
                field,
                AiDiffFields.CurrentValue(change.TargetKind, field, target),
                change.AfterValue,
                null,
                index),
            null);
    }

    /// <summary>The addressed row in the pinned snapshot, or null when it is not there.</summary>
    private static object? Find(RecipeSnapshotDocument source, AiChangeTargetKind kind, Guid? id) =>
        id is not { } targetId
            ? null
            : kind switch
            {
                AiChangeTargetKind.Recipe => source.Recipe,
                AiChangeTargetKind.IngredientGroup => source.IngredientGroups
                    .FirstOrDefault(group => group.Id == targetId),
                AiChangeTargetKind.Ingredient => source.IngredientGroups
                    .SelectMany(group => group.Ingredients)
                    .FirstOrDefault(ingredient => ingredient.Id == targetId),
                AiChangeTargetKind.InstructionGroup => source.InstructionGroups
                    .FirstOrDefault(group => group.Id == targetId),
                AiChangeTargetKind.InstructionStep => source.InstructionGroups
                    .SelectMany(group => group.Steps)
                    .FirstOrDefault(step => step.Id == targetId),
                AiChangeTargetKind.Equipment => source.Equipment
                    .FirstOrDefault(equipment => equipment.Id == targetId),
                AiChangeTargetKind.AssetLink => source.AssetLinks
                    .FirstOrDefault(link => link.Id == targetId),
                AiChangeTargetKind.Tag => source.Tags
                    .FirstOrDefault(tag => tag.WorkspaceTagId == targetId),
                _ => null,
            };

    /// <summary>
    /// How many siblings of this kind the snapshot holds, so a move cannot land past the end.
    /// </summary>
    /// <remarks>
    /// Counted across the whole document rather than within the target's own parent, which is the looser of
    /// the two checks. Moving a step between groups is a reorder this stage does not attempt to model, and a
    /// tighter bound would refuse a legitimate one; the recipe domain re-validates ordering on apply.
    /// </remarks>
    private static int SiblingCount(RecipeSnapshotDocument source, AiChangeTargetKind kind) => kind switch
    {
        AiChangeTargetKind.IngredientGroup => source.IngredientGroups.Count,
        AiChangeTargetKind.Ingredient => source.IngredientGroups.Sum(group => group.Ingredients.Count),
        AiChangeTargetKind.InstructionGroup => source.InstructionGroups.Count,
        AiChangeTargetKind.InstructionStep => source.InstructionGroups.Sum(group => group.Steps.Count),
        AiChangeTargetKind.Equipment => source.Equipment.Count,
        AiChangeTargetKind.AssetLink => source.AssetLinks.Count,
        AiChangeTargetKind.Tag => source.Tags.Count,
        _ => 0,
    };

    /// <summary>The applicable kinds, or a plain statement that there are none.</summary>
    /// <remarks>
    /// "nothing" rather than an empty list, because most target kinds have no applicable change at all and a
    /// message trailing off after a colon reads like a bug in the message.
    /// </remarks>
    private static string Describe(IReadOnlyList<AiChangeKind> kinds) =>
        kinds.Count == 0 ? "nothing" : string.Join(", ", kinds);

    private static AiOutputFailure MissingTarget(AiOutputChange change) => Failure(
        AiOutputReason.TargetNotInSource,
        $"The {change.TargetKind} the change addresses is not in the pinned version.");

    private static AiOutputFailure Failure(string reasonCode, string message) =>
        new(AiFailureCategory.DomainInvalid, reasonCode, message, IsCorrectableByReprompt: false);

    private static AiDiffResult Refuse(string reasonCode, string message) =>
        new(null, Failure(reasonCode, message));
}
