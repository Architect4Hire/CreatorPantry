using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Whether the reference a newsletter item covers is still one the piece of work names (LF.4.1).</summary>
/// <remarks>
/// Read, never stored: the answer is the creative context's as it stands when the draft is read. A flag on the
/// revision would be a second copy of that, and a revision is not edited by what happens to its sources.
/// </remarks>
public enum LongFormItemSourceState
{
    /// <summary>The piece of work still names this reference, for the recipe the revision pinned.</summary>
    Resolved = 1,

    /// <summary>
    /// The piece of work no longer names it, or names a different recipe by it. The item, its text and its pin
    /// are all still in the revision; nothing can be written for it until the creator decides what it is now.
    /// </summary>
    Unresolved = 2,
}

/// <summary>A newsletter issue's subject line and the creator's note, as a creator is shown them.</summary>
public sealed record LongFormFramingServiceModel(string? Subject, string? Note);

/// <summary>One recipe reference a creative context holds now, as the draft seam reads it. Identifiers only.</summary>
public sealed record LongFormContextRecipeReference(Guid ReferenceId, Guid RecipeId);

/// <summary>The order a newsletter issue's items should read in, named by what each covers (LF.4.1).</summary>
/// <remarks>
/// References, not positions: an item is the reference it covers, so an order of references cannot lose an
/// item, repeat one or point one at another recipe. It carries no heading, no text and no workspace.
/// </remarks>
public sealed class ReorderLongFormItemsViewModel
{
    [Description("The concurrencyToken of the draft this change was composed against.")]
    public string? ExpectedConcurrencyToken { get; set; }

    [Description("The sourceReferenceId of every item in the issue, each once, in the order the items should read.")]
    public IReadOnlyList<Guid>? SourceReferenceIds { get; set; }
}

/// <summary>The creator's subject line and note for a newsletter issue (LF.4.1).</summary>
public sealed class PutLongFormFramingViewModel
{
    [Description("The concurrencyToken of the draft this change was composed against.")]
    public string? ExpectedConcurrencyToken { get; set; }

    [Description("The issue's subject line, on one line. Blank or omitted clears it.")]
    public string? Subject { get; set; }

    [Description("The creator's short note to their readers, as Markdown. Blank or omitted clears it.")]
    public string? Note { get; set; }
}

public sealed class ReorderLongFormItemsViewModelValidator : AbstractValidator<ReorderLongFormItemsViewModel>
{
    public ReorderLongFormItemsViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(CreativeContextConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrencyToken of the draft this change was composed against.")
            .OverridePropertyName("expectedConcurrencyToken");

        RuleFor(model => model.SourceReferenceIds)
            .NotNull()
            .WithMessage("Name every item by the reference it covers.")
            .Must(ids => ids is null || ids.Count <= LongFormDraftPolicy.MaxPlanSections)
            .WithMessage($"An issue has at most {LongFormDraftPolicy.MaxPlanSections} sections.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("Name each item once.")
            .OverridePropertyName("sourceReferenceIds");
    }
}

public sealed class PutLongFormFramingViewModelValidator : AbstractValidator<PutLongFormFramingViewModel>
{
    public PutLongFormFramingViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(CreativeContextConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrencyToken of the draft this change was composed against.")
            .OverridePropertyName("expectedConcurrencyToken");

        // Refused rather than saved and flagged, unlike a heading: these two are typed whole into one field
        // each, and nothing is lost by being told the limit before the save instead of after it.
        RuleFor(model => model.Subject)
            .Must(subject => subject is null || subject.AsSpan().IndexOfAny('\n', '\r') < 0)
            .WithMessage("Keep the subject to one line.")
            .Must(subject => subject is null
                || (subject.Length <= LongFormPolicy.SubjectMaxCharacters * 10
                    && LongFormWordCounter.CountCharacters(subject) <= LongFormPolicy.SubjectMaxCharacters))
            .WithMessage($"Keep the subject to {LongFormPolicy.SubjectMaxCharacters} characters.")
            .OverridePropertyName("subject");

        RuleFor(model => model.Note)
            .Must(note => note is null
                || (note.Length <= LongFormPolicy.NoteMaxCharacters * 10
                    && LongFormWordCounter.CountCharacters(note) <= LongFormPolicy.NoteMaxCharacters))
            .WithMessage($"Keep the note to {LongFormPolicy.NoteMaxCharacters} characters.")
            .OverridePropertyName("note");
    }
}

/// <summary>
/// Whether each item of a newsletter issue still has the source its revision pinned (LF.4.1). Pure.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An item is unresolved when the piece of work no longer names its reference for the same recipe.</strong>
/// The reference row was removed, or the id now names something else. It is not about whether the recipe is
/// still usable — that is the recipe module's answer, given when a section is written.
/// </para>
/// <para>
/// <strong>Unresolved is a report, never an edit.</strong> The item keeps its heading, its text, its reference id
/// and its pin; this only says what a reader of the draft should be told about it.
/// </para>
/// </remarks>
public static class LongFormItemSources
{
    /// <summary>Each item's state by section id. A section that is not an item is not in it.</summary>
    /// <param name="pinned">The references the revision pinned, each with the recipe it pinned it for.</param>
    /// <param name="held">The recipe references the piece of work holds now.</param>
    public static IReadOnlyDictionary<Guid, LongFormItemSourceState> Resolve(
        LongFormDocument document,
        IReadOnlyList<LongFormContextRecipeReference> pinned,
        IReadOnlyList<LongFormContextRecipeReference> held)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(held);

        var recipes = pinned
            .GroupBy(pin => pin.ReferenceId)
            .ToDictionary(group => group.Key, group => group.First().RecipeId);
        var current = held.ToHashSet();
        var states = new Dictionary<Guid, LongFormItemSourceState>();

        foreach (var section in document.Sections.Where(section => section.Role is LongFormSectionRole.Item))
        {
            // An item that names no reference, or one the revision never pinned, has nothing to resolve to.
            states[section.Id] = section.SourceReferenceId is { } source
                && recipes.TryGetValue(source, out var recipeId)
                && current.Contains(new LongFormContextRecipeReference(source, recipeId))
                    ? LongFormItemSourceState.Resolved
                    : LongFormItemSourceState.Unresolved;
        }

        return states;
    }
}
