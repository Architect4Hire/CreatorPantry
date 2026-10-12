using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>The creator's own text for one section of a long-form draft (LF.3.3).</summary>
/// <remarks>
/// There is no workspace, draft or section field: the route names all three, and the workspace is resolved from
/// the route and the caller's membership.
/// </remarks>
public sealed class EditLongFormSectionViewModel
{
    [Description("The concurrencyToken of the draft this edit was composed against.")]
    public string? ExpectedConcurrencyToken { get; set; }

    [Description("The section's text, as Markdown. Blank makes the section not yet written again.")]
    public string? Body { get; set; }
}

/// <summary>Whether the creator is content with one section's text for now (LF.3.3).</summary>
public sealed class SettleLongFormSectionViewModel
{
    [Description("The concurrencyToken of the draft this change was composed against.")]
    public string? ExpectedConcurrencyToken { get; set; }

    [Description("True to mark the section settled, false to clear the mark. Not an acceptance of the document.")]
    public bool? Settled { get; set; }
}

public sealed class EditLongFormSectionViewModelValidator : AbstractValidator<EditLongFormSectionViewModel>
{
    public EditLongFormSectionViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(CreativeContextConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrencyToken of the draft this edit was composed against.")
            .OverridePropertyName("expectedConcurrencyToken");

        // A bound on what one section's row may hold, and not the section ceiling: a body over the ceiling is
        // saved and reported as over it, because a creator's own text is never refused for being long.
        RuleFor(model => model.Body)
            .Must(body => body is null || body.Length <= LongFormDraftPolicy.SectionBodyMaxLength)
            .WithMessage($"A section's text is at most {LongFormDraftPolicy.SectionBodyMaxLength} characters.")
            .OverridePropertyName("body");
    }
}

public sealed class SettleLongFormSectionViewModelValidator : AbstractValidator<SettleLongFormSectionViewModel>
{
    public SettleLongFormSectionViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(CreativeContextConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrencyToken of the draft this change was composed against.")
            .OverridePropertyName("expectedConcurrencyToken");

        RuleFor(model => model.Settled)
            .NotNull()
            .WithMessage("Say whether the section is settled.")
            .OverridePropertyName("settled");
    }
}

/// <summary>A generated body to store against one section, as the AI module hands it over (LF.3.3).</summary>
/// <param name="BasedOnRevisionNumber">
/// The revision the request named. The body lands only if the section's text has not changed since.
/// </param>
/// <remarks>
/// No workspace: the draft is found under the workspace filter of whoever is landing it.
/// </remarks>
public sealed record LongFormGeneratedSectionBodyInput(
    Guid DraftId,
    Guid SectionId,
    int BasedOnRevisionNumber,
    string Body,
    Guid AiProposalId,
    string? PromptTemplateId,
    string? PromptTemplateVersion,
    string? PromptTemplateBodyChecksum,
    IReadOnlyList<string>? ReusedEditorialParts = null);

/// <summary>The sections of a draft's latest revision, with their text (LF.3.3).</summary>
/// <param name="RevisionNumber">The latest revision, or null when the draft has no plan yet.</param>
/// <param name="ConcurrencyToken">Sent back as <c>expectedConcurrencyToken</c> on the next write.</param>
/// <param name="SectionMaxCharacters">The ceiling a section's text is measured against.</param>
public sealed record LongFormSectionsServiceModel(
    Guid DraftId,
    Guid CreativeContextId,
    LongFormKind Kind,
    LongFormDraftStatus Status,
    int? RevisionNumber,
    string ConcurrencyToken,
    int SectionMaxCharacters,
    IReadOnlyList<LongFormSectionStateServiceModel> Sections);

/// <summary>One section as it stands.</summary>
/// <param name="Body">The section's text, or null when it has not been written.</param>
/// <param name="OverCeiling">Whether the text is longer than a section may be. Reported, never trimmed.</param>
/// <param name="WrittenBy">Whether the text as it reads now was generated or is the creator's own. Null when unwritten.</param>
/// <param name="AiProposalId">The AI proposal that wrote the text as it reads now, when it was generated.</param>
/// <param name="SourceReferenceId">For a newsletter item, the creative-context reference it covers.</param>
/// <param name="SourceState">For a newsletter item, whether the piece of work still names that reference.</param>
public sealed record LongFormSectionStateServiceModel(
    Guid Id,
    LongFormSectionRole Role,
    string Heading,
    string Intent,
    string? Body,
    bool IsWritten,
    bool Settled,
    int CharacterCount,
    bool OverCeiling,
    ContentRevisionSource? WrittenBy,
    Guid? AiProposalId,
    Guid? SourceReferenceId,
    LongFormItemSourceState? SourceState);
