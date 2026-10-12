using System.ComponentModel;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>Something about a document that its creator must acknowledge before accepting it (LF.3.4).</summary>
public enum LongFormAcceptanceIssue
{
    /// <summary>One or more sections of the plan have no text.</summary>
    UnwrittenSections = 1,

    /// <summary>One or more sections, or the document as a whole, are longer than they may be.</summary>
    OverCeiling = 2,
}

/// <summary>The acceptance of a long-form document (LF.3.4).</summary>
/// <remarks>
/// There is no workspace or draft field: the route names the draft, and the workspace is resolved from the
/// route and the caller's membership.
/// </remarks>
public sealed class AcceptLongFormDocumentViewModel
{
    [Description("The concurrencyToken of the draft this acceptance was composed against.")]
    public string? ExpectedConcurrencyToken { get; set; }

    [Description("The revision being accepted. It must be the draft's latest.")]
    public int RevisionNumber { get; set; }

    [Description("What the creator has been shown is wrong with the document and accepts anyway: exactly the issues the document has, no fewer and no more. Omit it, or send an empty list, for a document with nothing wrong.")]
    public IReadOnlyList<LongFormAcceptanceIssue>? Confirmed { get; set; }
}

public sealed class AcceptLongFormDocumentViewModelValidator : AbstractValidator<AcceptLongFormDocumentViewModel>
{
    public AcceptLongFormDocumentViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(CreativeContextConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrencyToken of the draft this acceptance was composed against.")
            .OverridePropertyName("expectedConcurrencyToken");

        RuleFor(model => model.RevisionNumber)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Name the revision being accepted.")
            .OverridePropertyName("revisionNumber");

        RuleFor(model => model.Confirmed)
            .Must(confirmed => confirmed is null || confirmed.All(issue => Enum.IsDefined(issue)))
            .WithMessage("Name each confirmed issue by one of the issues a document can have.")
            .OverridePropertyName("confirmed");
    }
}

/// <summary>One thing wrong with a document, and the sections it is wrong about.</summary>
/// <param name="SectionIds">
/// The sections concerned, in plan order. Empty when the issue is about the document as a whole.
/// </param>
public sealed record LongFormAcceptanceIssueDetailServiceModel(LongFormAcceptanceIssue Issue, IReadOnlyList<Guid> SectionIds);

/// <summary>One section of an assembled document.</summary>
/// <param name="Body">The section's text, or null when it has not been written.</param>
/// <param name="OverCeiling">Whether the text is longer than a section may be. Reported, never trimmed.</param>
/// <param name="WrittenBy">Whether the text as it reads now was generated or is the creator's own. Null when unwritten.</param>
/// <param name="AiProposalId">The AI proposal that wrote the text as it reads now, when it was generated.</param>
/// <param name="ReusedEditorialParts">
/// The parts of copy the creator had already accepted for a recipe that this text was written from (B-36).
/// Empty when it was written from nothing of the kind, and once the creator has replaced it with their own.
/// </param>
/// <param name="Findings">
/// What the claim scanner found in the text as it reads now, whoever wrote it (LF.3.2): a figure the pinned
/// recipes do not state, a safety or dietary claim, storage advice, a search metric, a promise the recipe will
/// work. Cautions for the creator; none of them stops a save or an acceptance.
/// </param>
public sealed record LongFormAssembledSectionServiceModel(
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
    IReadOnlyList<string> ReusedEditorialParts,
    IReadOnlyList<LongFormSectionFinding> Findings);

/// <summary>
/// A long-form draft's latest revision read whole (LF.3.4): the Markdown a creator would be accepting, what is
/// true of it, and what they would have to acknowledge to accept it.
/// </summary>
/// <param name="RevisionNumber">The revision assembled, or null when the draft has no plan yet.</param>
/// <param name="ConcurrencyToken">Sent back as <c>expectedConcurrencyToken</c> on the next write.</param>
/// <param name="Markdown">
/// Every written section in plan order under the creator's own headings. The same revision always gives the
/// same text.
/// </param>
/// <param name="WordTarget">Where the word count sits against the kind's target. Not assessed until every section is written.</param>
/// <param name="NeedsConfirmation">What is wrong with the document. Empty when it can be accepted as it stands.</param>
/// <param name="AcceptedRevisionNumber">The revision that was accepted, once one has been.</param>
public sealed record LongFormAssembledDocumentServiceModel(
    Guid DraftId,
    Guid CreativeContextId,
    LongFormKind Kind,
    LongFormDraftStatus Status,
    int? RevisionNumber,
    string ConcurrencyToken,
    string Markdown,
    int WordCount,
    int CharacterCount,
    LongFormWordRange TargetWords,
    LongFormWordTargetStatus WordTarget,
    int SectionMaxCharacters,
    int DocumentMaxCharacters,
    bool DocumentOverCeiling,
    IReadOnlyList<LongFormAssembledSectionServiceModel> Sections,
    IReadOnlyList<LongFormAcceptanceIssueDetailServiceModel> NeedsConfirmation,
    int? AcceptedRevisionNumber,
    DateTimeOffset? AcceptedAt);
