using System.Globalization;
using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Recipes.Data.Entities;

namespace CreatorPantry.Domain.Modules.Recipes.Managers;

/// <summary>The patch a set of accepted changes amounts to, or why they do not amount to one.</summary>
/// <param name="Patch">The edit to apply, when every accepted change could be expressed.</param>
/// <param name="Error">
/// What could not be expressed, naming the change. A plain message rather than a code: the seam above turns it
/// into an <c>OperationError</c> with the code that route publishes.
/// </param>
public sealed record RecipeProposalPlan(CanonicalRecipePatch? Patch, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Turns changes a creator accepted into the ordinary partial edit that applies them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is what makes an accepted proposal pass the same validation as a hand-typed edit.</strong>
/// Nothing here writes to the recipe: it produces a <see cref="CanonicalRecipePatch"/>, which then travels the
/// one merge path every edit travels — the same invariant checks, the same instruction reconciliation, the same
/// concurrency guard, the same single new version. An apply path that touched the aggregate directly would be a
/// second definition of what a valid recipe is, and the model's output is the last thing that should have one.
/// </para>
/// <para>
/// <strong>Pure, and over the recipe as it stands.</strong> A field the changes do not mention stays absent
/// from the patch, so it is left alone rather than rewritten with the value it already had. The instruction
/// list and the tag set are the exceptions and cannot be otherwise: both replace wholesale in the patch
/// contract, so changing one step means re-emitting every step — from the live aggregate, never from the
/// model's answer.
/// </para>
/// </remarks>
public static class RecipeProposalApplication
{
    /// <param name="loaded">The recipe as the caller read it in this unit of work, with its tag vocabulary.</param>
    /// <param name="changes">The accepted changes, in the order the creator reviewed them.</param>
    public static RecipeProposalPlan Plan(TaggedRecipe loaded, IReadOnlyList<ProposedRecipeChange> changes)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(changes);

        var recipe = loaded.Recipe.Recipe;
        var builder = new PatchBuilder();

        // Materialized once and mutated in place, so several accepted changes to the method compose instead of
        // each producing its own list and the last one winning.
        var groups = CurrentInstructions(recipe);
        var instructionsTouched = false;

        var tags = CurrentTags(recipe, loaded.Tags);
        var tagsTouched = false;

        foreach (var change in changes)
        {
            // The bound the edge validator would have applied to a hand-typed edit. There is no ViewModel here,
            // so the checks that live on one have to be made where the accepted value is first read — otherwise an
            // over-long or out-of-range value reaches SQL Server as a truncation or constraint error, which the
            // recipe data layer correctly declines to call a conflict and rethrows: a 500 where the honest answer
            // is a refusal naming the change.
            if (change.Kind is ProposedRecipeChangeKind.Set
                && change.FieldName is { } fieldName
                && !ProposedRecipeValues.Accepts(fieldName, change.Value))
            {
                return Refuse(
                    $"'{Quoted(change.Value)}' is not a value '{fieldName}' can hold. It takes at most "
                        + $"{ProposedRecipeValues.MaxLength(fieldName)} characters, and a number where the field "
                        + "is numeric.");
            }

            switch (change.Target)
            {
                case ProposedRecipeTarget.Recipe when change.Kind is ProposedRecipeChangeKind.Set:
                    if (builder.Set(change.FieldName, change.Value) is { } headerError)
                    {
                        return Refuse(headerError);
                    }

                    break;

                case ProposedRecipeTarget.InstructionGroup:
                case ProposedRecipeTarget.InstructionStep:
                    if (ApplyToInstructions(groups, change) is { } instructionError)
                    {
                        return Refuse(instructionError);
                    }

                    instructionsTouched = true;
                    break;

                case ProposedRecipeTarget.Tag:
                    if (ApplyToTags(tags, change) is { } tagError)
                    {
                        return Refuse(tagError);
                    }

                    tagsTouched = true;
                    break;

                default:
                    return Refuse(
                        $"A {change.Kind} on a {change.Target} is not a change this recipe seam can apply.");
            }
        }

        return new RecipeProposalPlan(
            builder.Build(
                // The token the caller just read. The merge path checks it, and it must check something — but
                // the guard that matters for an accepted proposal is the pinned version id, checked by the
                // caller, because that is what the creator reviewed the diff against.
                RecipeConcurrencyToken.From(recipe.RowVersion),
                instructionsTouched ? Canonicalize(groups) : null,
                tagsTouched ? Canonicalize(tags) : null),
            null);
    }

    // ---- the recipe's own fields ---------------------------------------------------------------------------

    /// <summary>
    /// Accumulates the header fields the accepted changes set, leaving every other field absent.
    /// </summary>
    /// <remarks>
    /// A mutable builder rather than a chain of <c>with</c> expressions, because the field being set is named
    /// by a string at run time and a record expression cannot be indexed by one. The field vocabulary is the
    /// proposal's, which is the diff the creator read.
    /// </remarks>
    private sealed class PatchBuilder
    {
        private PatchField<string?> title;
        private PatchField<string?> description;
        private PatchField<string?> headnote;
        private PatchField<string?> notes;
        private PatchField<string?> storageNotes;
        private PatchField<string?> attributionText;
        private PatchField<string?> sourceUrl;
        private PatchField<int?> prepTimeMinutes;
        private PatchField<int?> cookTimeMinutes;
        private PatchField<int?> restTimeMinutes;
        private PatchField<int?> totalTimeMinutes;
        private PatchField<string?> yieldText;
        private PatchField<decimal?> yieldQuantity;

        /// <summary>Records one accepted field value, or says why it cannot be recorded.</summary>
        public string? Set(string? fieldName, string? value) => fieldName switch
        {
            "title" => Text(value, ref title),
            "description" => Text(value, ref description),
            "headnote" => Text(value, ref headnote),
            "notes" => Text(value, ref notes),
            "storageNotes" => Text(value, ref storageNotes),
            "attributionText" => Text(value, ref attributionText),
            "sourceUrl" => Text(value, ref sourceUrl),
            "prepTimeMinutes" => Integer(value, ref prepTimeMinutes),
            "cookTimeMinutes" => Integer(value, ref cookTimeMinutes),
            "restTimeMinutes" => Integer(value, ref restTimeMinutes),
            "totalTimeMinutes" => Integer(value, ref totalTimeMinutes),
            "yieldText" => Text(value, ref yieldText),
            "yieldQuantity" => Number(value, ref yieldQuantity),
            _ => $"'{fieldName}' is not a field of a recipe that an accepted change can set.",
        };

        public CanonicalRecipePatch Build(
            string expectedConcurrencyToken,
            IReadOnlyList<CanonicalInstructionGroup>? instructions,
            IReadOnlyList<RecipeTagName>? tags) => new()
            {
                ExpectedConcurrencyToken = expectedConcurrencyToken,

                // No reason text. The version records Source and AiProposalId, which say where the edit came
                // from more exactly than a sentence would, and inventing prose in the creator's voice about an
                // edit they only approved would be putting words in their mouth.
                Reason = null,

                Title = title,
                Description = description,
                Headnote = headnote,
                Notes = notes,
                StorageNotes = storageNotes,
                AttributionText = attributionText,
                SourceUrl = sourceUrl,
                PrepTimeMinutes = prepTimeMinutes,
                CookTimeMinutes = cookTimeMinutes,
                RestTimeMinutes = restTimeMinutes,
                TotalTimeMinutes = totalTimeMinutes,
                YieldText = yieldText,
                YieldQuantity = yieldQuantity,

                Instructions = instructions is null
                    ? PatchField<IReadOnlyList<CanonicalInstructionGroup>>.Absent
                    : PatchField<IReadOnlyList<CanonicalInstructionGroup>>.Submitted(instructions),

                Tags = tags is null
                    ? PatchField<IReadOnlyList<RecipeTagName>>.Absent
                    : PatchField<IReadOnlyList<RecipeTagName>>.Submitted(tags),

                // Every reference id and the status stay absent, and not for want of a line here: no proposal
                // can name one. AiDiffFields lists no identifier field and no status, because a model choosing
                // a Guid is a model inventing one, and moving a recipe through its workflow is the creator's
                // own act rather than a suggestion they approve in passing.
            };

        private static string? Text(string? value, ref PatchField<string?> field)
        {
            field = PatchField<string?>.Submitted(Trimmed(value));

            return null;
        }

        private static string? Integer(string? value, ref PatchField<int?> field)
        {
            if (value is null)
            {
                field = PatchField<int?>.Submitted(null);

                return null;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return $"'{value}' is not a whole number of minutes.";
            }

            field = PatchField<int?>.Submitted(parsed);

            return null;
        }

        private static string? Number(string? value, ref PatchField<decimal?> field)
        {
            if (value is null)
            {
                field = PatchField<decimal?>.Submitted(null);

                return null;
            }

            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                return $"'{value}' is not a number.";
            }

            field = PatchField<decimal?>.Submitted(parsed);

            return null;
        }
    }

    // ---- the method ---------------------------------------------------------------------------------------

    /// <summary>A group being rebuilt, mutable so several accepted changes to the method compose.</summary>
    private sealed class DraftGroup(Guid id, string? title)
    {
        public Guid Id { get; } = id;

        public string? Title { get; set; } = title;

        public List<DraftStep> Steps { get; } = [];
    }

    /// <inheritdoc cref="DraftGroup"/>
    private sealed class DraftStep(RecipeInstructionStep step)
    {
        public Guid Id { get; } = step.Id;

        public string Text { get; set; } = step.Text;

        public Guid? TechniqueId { get; } = step.TechniqueId;

        public int? DurationMinutes { get; set; } = step.DurationMinutes;

        public decimal? TemperatureValue { get; set; } = step.TemperatureValue;

        /// <summary>
        /// The step's existing temperature unit. Settable only to <c>null</c>, and only together with the value.
        /// </summary>
        /// <remarks>
        /// No proposal can choose a unit — <c>AiDiffFields</c> lists no identifier field, because a model naming
        /// a <c>Guid</c> is a model inventing one. So the unit either stays as the creator set it or goes away
        /// with the value it describes.
        /// </remarks>
        public Guid? TemperatureUnitId { get; private set; } = step.TemperatureUnitId;

        public string? Note { get; set; } = step.Note;

        public void ClearTemperatureUnit() => TemperatureUnitId = null;
    }

    /// <summary>The recipe's method as it stands, in its own order.</summary>
    private static List<DraftGroup> CurrentInstructions(Recipe recipe)
    {
        var groups = new List<DraftGroup>();

        foreach (var group in recipe.InstructionGroups.OrderBy(group => group.SortOrder))
        {
            var draft = new DraftGroup(group.Id, group.Title);

            foreach (var step in group.Steps.OrderBy(step => step.SortOrder))
            {
                draft.Steps.Add(new DraftStep(step));
            }

            groups.Add(draft);
        }

        return groups;
    }

    private static string? ApplyToInstructions(List<DraftGroup> groups, ProposedRecipeChange change)
    {
        if (change.TargetId is not { } targetId)
        {
            return $"A change to a {change.Target} does not say which one.";
        }

        return change.Target is ProposedRecipeTarget.InstructionGroup
            ? ApplyToGroup(groups, change, targetId)
            : ApplyToStep(groups, change, targetId);
    }

    private static string? ApplyToGroup(List<DraftGroup> groups, ProposedRecipeChange change, Guid targetId)
    {
        var index = groups.FindIndex(group => group.Id == targetId);

        if (index < 0)
        {
            return "The instruction group the change addresses is no longer part of the recipe.";
        }

        switch (change.Kind)
        {
            case ProposedRecipeChangeKind.Set when change.FieldName == "title":
                groups[index].Title = Trimmed(change.Value);

                return null;

            case ProposedRecipeChangeKind.Remove:
                groups.RemoveAt(index);

                return null;

            case ProposedRecipeChangeKind.Move:
                Move(groups, index, change.Position);

                return null;

            default:
                return change.Kind is ProposedRecipeChangeKind.Set
                    ? $"'{change.FieldName}' is not a field of an instruction group that a change can set."
                    : $"A {change.Kind} on an instruction group is not a change this recipe seam can apply.";
        }
    }

    private static string? ApplyToStep(List<DraftGroup> groups, ProposedRecipeChange change, Guid targetId)
    {
        var group = groups.FirstOrDefault(candidate => candidate.Steps.Any(step => step.Id == targetId));

        if (group is null)
        {
            return "The instruction step the change addresses is no longer part of the recipe.";
        }

        var index = group.Steps.FindIndex(step => step.Id == targetId);

        switch (change.Kind)
        {
            case ProposedRecipeChangeKind.Set:
                return SetStepField(group.Steps[index], change);

            case ProposedRecipeChangeKind.Remove:
                group.Steps.RemoveAt(index);

                return null;

            case ProposedRecipeChangeKind.Move:
                Move(group.Steps, index, change.Position);

                return null;

            default:
                return $"A {change.Kind} on an instruction step is not a change this recipe seam can apply.";
        }
    }

    private static string? SetStepField(DraftStep step, ProposedRecipeChange change)
    {
        switch (change.FieldName)
        {
            case "text":
                // Never cleared: a step with no text is not a step, and the reconciler would write an empty one
                // rather than refuse. Refusing here names the change that asked for it.
                var text = Trimmed(change.Value);

                if (text is null)
                {
                    return "An instruction step cannot be left with no text.";
                }

                step.Text = text;

                return null;

            case "note":
                step.Note = Trimmed(change.Value);

                return null;

            case "durationMinutes":
                if (change.Value is null)
                {
                    step.DurationMinutes = null;

                    return null;
                }

                if (!int.TryParse(change.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                {
                    return $"'{change.Value}' is not a whole number of minutes.";
                }

                step.DurationMinutes = minutes;

                return null;

            case "temperatureValue":
                if (change.Value is null)
                {
                    // Clearing takes the unit with it. All three temperature columns move together or the row is
                    // refused by a check constraint, and a value cleared without its unit would be that refusal
                    // arriving as a 500 instead of an answer.
                    step.TemperatureValue = null;
                    step.ClearTemperatureUnit();

                    return null;
                }

                if (!decimal.TryParse(change.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var degrees))
                {
                    return $"'{change.Value}' is not a temperature.";
                }

                // A change row has nowhere to carry a unit, so a step that records none cannot be given a
                // temperature. The diff calculator refuses this before a creator sees it; this is the second line,
                // for a proposal stored before that check existed.
                if (step.TemperatureUnitId is null)
                {
                    return "That step records no temperature unit, so a temperature cannot be set on it.";
                }

                step.TemperatureValue = degrees;

                return null;

            default:
                return $"'{change.FieldName}' is not a field of an instruction step that a change can set.";
        }
    }

    /// <summary>
    /// Moves one item to <paramref name="position"/> among its siblings.
    /// </summary>
    /// <remarks>
    /// Clamped rather than refused. A position past the end reads as "put it last", which is what a reorder
    /// means, and turning a change the creator already reviewed and accepted into a failure over an
    /// off-by-something in a sibling count would be the least useful possible answer. A move with no position
    /// leaves the order alone.
    /// </remarks>
    private static void Move<T>(List<T> items, int from, int? position)
    {
        if (position is not { } to)
        {
            return;
        }

        var item = items[from];

        items.RemoveAt(from);
        items.Insert(Math.Clamp(to, 0, items.Count), item);
    }

    private static IReadOnlyList<CanonicalInstructionGroup> Canonicalize(List<DraftGroup> groups) =>
        [.. groups.Select(group => new CanonicalInstructionGroup
        {
            // Always the existing id, because nothing here creates a group or a step: the reconciler treats an
            // id it recognises as an update in place, which is what every accepted change is.
            Id = group.Id,
            Title = group.Title,
            Steps = [.. group.Steps.Select(step => new CanonicalInstructionStep
            {
                Id = step.Id,
                Text = step.Text,
                TechniqueId = step.TechniqueId,
                DurationMinutes = step.DurationMinutes,
                TemperatureValue = step.TemperatureValue,
                TemperatureUnitId = step.TemperatureUnitId,
                Note = step.Note,
            })],
        })];

    // ---- tags ---------------------------------------------------------------------------------------------

    /// <summary>One entry in the tag set being rebuilt, and the vocabulary row it came from if it had one.</summary>
    private sealed record DraftTag(Guid? WorkspaceTagId, RecipeTagName Name);

    /// <summary>
    /// The recipe's tags as names, keyed by the vocabulary id so a removal can name one by id.
    /// </summary>
    /// <remarks>
    /// A link whose vocabulary row was not resolved is dropped rather than carried as a nameless entry — see
    /// <see cref="TaggedRecipe.Tags"/> for the race that allows it. The alternative is submitting a tag set
    /// with a hole in it, which would silently delete the creator's tag.
    /// </remarks>
    private static List<DraftTag> CurrentTags(Recipe recipe, IReadOnlyList<WorkspaceTag> vocabulary)
    {
        var byId = vocabulary.ToDictionary(tag => tag.Id);

        return
        [
            .. recipe.Tags
                .Select(link => byId.TryGetValue(link.WorkspaceTagId, out var tag) ? tag : null)
                .Where(tag => tag is not null)
                .Select(tag => new DraftTag(tag!.Id, new RecipeTagName(tag.Name, tag.NormalizedName))),
        ];
    }

    private static string? ApplyToTags(List<DraftTag> tags, ProposedRecipeChange change)
    {
        switch (change.Kind)
        {
            case ProposedRecipeChangeKind.Add:
                var name = Trimmed(change.Value);

                if (name is null)
                {
                    return "An added tag has no name.";
                }

                var normalized = NameNormalization.NormalizeName(name);

                if (normalized.Length == 0)
                {
                    return $"'{change.Value}' is not a usable tag name.";
                }

                // Both bounds the edge validator would have applied. Without them an over-long name reached the
                // column as a truncation error, and repeated additions could push a recipe past its tag limit —
                // in each case a 500 rather than a refusal a creator could act on.
                if (name.Length > TagPolicy.NameMaxLength)
                {
                    return $"A tag can be at most {TagPolicy.NameMaxLength} characters.";
                }

                if (tags.Count >= TagPolicy.MaxTagsPerRecipe)
                {
                    return $"A recipe can carry at most {TagPolicy.MaxTagsPerRecipe} tags.";
                }

                // Already there under a different spelling: accepting is a no-op rather than a duplicate. A
                // recipe's tags are a set, and two entries normalizing alike are one tag.
                if (!tags.Any(tag => string.Equals(tag.Name.NormalizedName, normalized, StringComparison.Ordinal)))
                {
                    tags.Add(new DraftTag(null, new RecipeTagName(name, normalized)));
                }

                return null;

            case ProposedRecipeChangeKind.Remove:
                if (change.TargetId is not { } targetId)
                {
                    return "A tag removal does not say which tag.";
                }

                var index = tags.FindIndex(tag => tag.WorkspaceTagId == targetId);

                if (index < 0)
                {
                    return "The tag the change removes is no longer on the recipe.";
                }

                tags.RemoveAt(index);

                return null;

            default:
                return $"A {change.Kind} on a tag is not a change this recipe seam can apply.";
        }
    }

    private static IReadOnlyList<RecipeTagName> Canonicalize(List<DraftTag> tags) =>
        [.. tags.Select(tag => tag.Name).OrderBy(tag => tag.NormalizedName, StringComparer.Ordinal)];

    // ---- shared -------------------------------------------------------------------------------------------

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();

        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// The offending value, short enough to put in a message.
    /// </summary>
    /// <remarks>
    /// Quoted because naming the value is what makes the refusal useful — a creator seeing "not a value that
    /// field can hold" with nothing else has to guess which of their accepted changes was the problem. Truncated
    /// because the commonest reason to be here is a value that was too long, and repeating all of it in a
    /// <c>ProblemDetails</c> would make the refusal as unwieldy as the value.
    /// </remarks>
    private const int QuotedValueMaxLength = 60;

    private static string Quoted(string? value) =>
        value is null
            ? string.Empty
            : value.Length <= QuotedValueMaxLength
                ? value
                : value[..QuotedValueMaxLength] + "…";

    private static RecipeProposalPlan Refuse(string error) => new(null, error);
}
