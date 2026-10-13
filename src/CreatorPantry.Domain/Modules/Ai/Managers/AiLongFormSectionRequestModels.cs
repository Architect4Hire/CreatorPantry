using System.ComponentModel;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// A request to write one section of a long-form draft, or to write it again (LF.3.3).
/// </summary>
/// <remarks>
/// There is no workspace, draft or section field. The route names the draft and the section, and the workspace
/// is resolved from the route and the caller's membership.
/// </remarks>
public sealed class RequestLongFormSectionViewModel
{
    [Description("The draft revision this request was composed against. The section is written only if its text has not changed since.")]
    public int RevisionNumber { get; set; }

    [Description("What to change, in the creator's own words, when the section is being written again. Optional.")]
    public string? Instruction { get; set; }
}

/// <summary>A request to write every section of a long-form draft that has not been written yet (LF.3.3).</summary>
public sealed class RequestLongFormSectionBatchViewModel
{
    [Description("The draft revision this request was composed against.")]
    public int RevisionNumber { get; set; }
}

public sealed class RequestLongFormSectionViewModelValidator : AbstractValidator<RequestLongFormSectionViewModel>
{
    public RequestLongFormSectionViewModelValidator()
    {
        RuleFor(model => model.RevisionNumber)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Name the draft revision this request was composed against.")
            .OverridePropertyName("revisionNumber");

        RuleFor(model => model.Instruction)
            .Must(instruction => instruction is null || instruction.Length <= AiPolicy.LongFormSectionInstructionMaxLength)
            .WithMessage($"Say what to change in at most {AiPolicy.LongFormSectionInstructionMaxLength} characters.")
            .OverridePropertyName("instruction");
    }
}

public sealed class RequestLongFormSectionBatchViewModelValidator : AbstractValidator<RequestLongFormSectionBatchViewModel>
{
    public RequestLongFormSectionBatchViewModelValidator()
    {
        RuleFor(model => model.RevisionNumber)
            .GreaterThanOrEqualTo(1)
            .WithMessage("Name the draft revision this request was composed against.")
            .OverridePropertyName("revisionNumber");
    }
}

/// <summary>The stable codes LF.3.3's request seam answers with, beyond the content module's own.</summary>
public static class AiLongFormSectionRequestErrors
{
    public const string RequestInvalid = "ai.longFormSection.invalid_request";

    /// <summary>The task is recognised and switched off for this workspace or deployment.</summary>
    public const string TaskNotEnabled = "ai.longFormSection.not_enabled";

    /// <summary>
    /// The name a refused "write the rest" gives the number of sections it was asked to write.
    /// </summary>
    public const string SectionsRequestedExtension = "sectionsRequested";

    /// <summary>The name it gives the number the allowance could have covered.</summary>
    public const string SectionsCoverableExtension = "sectionsCoverable";
}

/// <summary>What LF.3.3's request seam is bounded by.</summary>
public static class AiLongFormSectionRequestPolicy
{
    /// <summary>
    /// The longest key a "write the rest" request may carry.
    /// </summary>
    /// <remarks>
    /// Shorter than a single request's, because each section's own request is stored under the key with the
    /// section's id after it, and that has to fit the same column.
    /// </remarks>
    public const int BatchKeyMaxLength = IdempotencyPolicy.KeyMaxLength - 33;

    /// <summary>
    /// How far apart the sections of one "write the rest" are stamped as available, so they are picked up in
    /// plan order. Backwards from now: every one of them is available at once.
    /// </summary>
    public static readonly TimeSpan BatchSpacing = TimeSpan.FromMilliseconds(10);

    /// <summary>How many of a draft's most recent section requests are read to say where each section stands.</summary>
    public const int StatusLookback = 200;

    /// <summary>The key one section's request is stored under, within a "write the rest".</summary>
    public static string BatchKey(string batchKey, Guid sectionId) => $"{batchKey}:{sectionId:N}";
}

/// <summary>One section, and the request that was queued to write it.</summary>
public sealed record LongFormSectionWritingRequestServiceModel(Guid SectionId, AiProposalStatusServiceModel Request);

/// <summary>A queued request to write one section, with the draft's sections as they now stand.</summary>
public sealed record LongFormSectionRequestedServiceModel(
    LongFormSectionsServiceModel Draft, LongFormSectionWritingRequestServiceModel Request);

/// <summary>
/// The requests queued by one "write the rest", in plan order, with the draft's sections as they now stand.
/// </summary>
/// <remarks>
/// Empty when every section is already written, settled or being written. That is an answer and not an error:
/// nothing was queued and nothing was spent.
/// </remarks>
public sealed record LongFormSectionBatchServiceModel(
    LongFormSectionsServiceModel Draft, IReadOnlyList<LongFormSectionWritingRequestServiceModel> Requests);

/// <summary>Where the latest request to write a section has got to, as far as the section is concerned.</summary>
public enum LongFormSectionRequestOutcome
{
    /// <summary>Queued or running. Nothing has been written by it yet.</summary>
    Waiting = 1,

    /// <summary>It finished, and its text is the section's text now.</summary>
    Written = 2,

    /// <summary>
    /// It finished, and its text is not the section's text: the creator changed or settled the section
    /// meanwhile, or has edited it since.
    /// </summary>
    Superseded = 3,

    /// <summary>It did not finish: it failed, was cancelled, or expired. The section is as it was.</summary>
    Failed = 4,
}

/// <summary>The latest request to write one section.</summary>
public sealed record LongFormSectionRequestSummaryServiceModel(
    Guid SectionId,
    Guid RequestId,
    AiOperationStatus Status,
    AiFailureCategory? FailureCategory,
    LongFormSectionRequestOutcome Outcome,
    DateTimeOffset RequestedAt);

/// <summary>One thing a creator should know about a section's text as it reads now.</summary>
/// <param name="Code">
/// The server's own code for the finding — <c>editorial.unsupported_number</c>,
/// <c>content.long_form.section.over_ceiling</c> — or null for a caution the model raised about its own work.
/// </param>
/// <param name="Message">The sentence, without the code or the model's label in front of it.</param>
/// <param name="FromModel">True when the model raised it about its own work; false when the server found it.</param>
public sealed record LongFormSectionWarningServiceModel(
    Guid SectionId, AiWarningKind Kind, string? Code, string Message, bool FromModel);

/// <summary>
/// A long-form draft read whole for review (LF.3.4): the assembled document, what was found about each
/// generated section, and the latest writing request for each section that has one.
/// </summary>
/// <remarks>
/// A section's warnings are those of the proposal that wrote its text as it reads now. A section the creator
/// has written or edited themselves has none here: nothing has read their words.
/// </remarks>
public sealed record LongFormDocumentReviewServiceModel(
    LongFormAssembledDocumentServiceModel Document,
    IReadOnlyList<LongFormSectionWarningServiceModel> Warnings,
    IReadOnlyList<LongFormSectionRequestSummaryServiceModel> Requests);

/// <summary>
/// A draft's sections with their text, and the latest writing request for each section that has one (LF.3.3).
/// </summary>
public sealed record LongFormSectionsStatusServiceModel(
    LongFormSectionsServiceModel Draft, IReadOnlyList<LongFormSectionRequestSummaryServiceModel> Requests);
